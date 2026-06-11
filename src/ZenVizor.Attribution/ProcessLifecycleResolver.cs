using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Core.Attribution;

namespace ZenVizor.Attribution;

/// <summary>
/// PID → process identity backed by an in-memory cache that is populated
/// EAGERLY by kernel ETW <c>ProcessStart</c> events (via
/// <see cref="IProcessLifecycleSink"/>). This is the fix for the short-lived
/// process attribution race: when curl downloads 50 MB and exits in &lt;1 s,
/// the network events are delivered AFTER the process exits, and the old
/// post-hoc <c>Process.GetProcessById</c> lookup fails. With this resolver,
/// the cache already has curl's image from the start event, so trailing
/// network events still resolve.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><see cref="OnProcessStart"/> populates the cache with the
///         process's image identity at creation time.</item>
///   <item><see cref="OnProcessStop"/> marks the entry as exited but
///         retains it for <see cref="GraceMs"/> ms so trailing ETW events
///         still resolve. This is critical for short-lived processes.</item>
///   <item>Entries are evicted opportunistically on <see cref="Resolve"/>
///         once the grace window has elapsed. The full <c>_byPid</c> scan is
///         amortized via <c>_nextEvictAtUnixMs</c>: scans run only after the
///         earliest pending exit's grace expires, not on every observation.</item>
///   <item>PID reuse: a new <see cref="OnProcessStart"/> for a PID that is
///         already in the cache overwrites the entry (correct attribution
///         to the new process).</item>
/// </list>
/// </para>
/// <para>
/// <see cref="Resolve"/> falls back to a Win32 <see cref="Process.GetProcessById"/>
/// lookup for PIDs not in the cache (covers processes that started before
/// ZenVizor was running or any miss due to ETW buffer overrun). A short-TTL
/// negative cache (<c>_negativeCache</c>) absorbs the per-event repeat cost
/// of <see cref="Process.GetProcessById"/> throwing <see cref="ArgumentException"/>
/// for an exited PID — without it, every trailing event from a phantom PID
/// re-pays the same exception.
/// </para>
/// <para>
/// <see cref="PrimeFromRunningProcesses"/> seeds the cache at startup to
/// reduce the fallback rate to near-zero.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ProcessLifecycleResolver : IProcessImageResolver, IProcessLifecycleSink
{
    public const long DefaultGraceMs = 60_000;

    /// <summary>
    /// How long a failed <c>GetProcessById</c> result is remembered before
    /// the resolver retries. Short enough that a genuinely-started process
    /// resolves on its next event; long enough to absorb a burst of trailing
    /// events for the same phantom PID.
    /// </summary>
    public const long NegativeCacheTtlMs = 5_000;

    /// <summary>
    /// Hard cap on the negative cache. Bounds the worst case where many
    /// distinct PIDs miss in close succession. When full, the oldest entry
    /// is evicted FIFO.
    /// </summary>
    private const int NegativeCacheCapacity = 256;

    private const int SystemPid = 4;

    private static readonly ProcessImageInfo SystemImage = new(
        Pid: SystemPid,
        ImagePath: "(kernel)",
        ImageName: "System",
        StartTimeUnixMs: 0);

    private readonly Dictionary<int, CacheEntry> _byPid = new();
    private readonly Dictionary<int, long> _negativeCache = new();
    private readonly Queue<int> _negativeCacheInsertionOrder = new();
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly Func<long> _now;

    // Eviction is amortized: full _byPid scan runs only when we know at
    // least one pending-exit entry's grace window has expired. Init to
    // long.MaxValue (no scan needed).
    private long _nextEvictAtUnixMs = long.MaxValue;

    public ProcessLifecycleResolver(
        ILogger<ProcessLifecycleResolver>? logger = null,
        long graceMs = DefaultGraceMs,
        Func<long>? nowProvider = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        GraceMs = graceMs;
        _now = nowProvider ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>How long to retain a cache entry past <see cref="OnProcessStop"/>.</summary>
    public long GraceMs { get; }

    /// <summary>Tracked-PID count, for diagnostics/tests.</summary>
    public int CachedCount
    {
        get { lock (_gate) return _byPid.Count; }
    }

    /// <summary>Negative-cache size, for diagnostics/tests.</summary>
    internal int NegativeCachedCount
    {
        get { lock (_gate) return _negativeCache.Count; }
    }

    public void OnProcessStart(int pid, string imagePath, long startUnixMs)
    {
        if (pid <= 0 || string.IsNullOrEmpty(imagePath))
        {
            return;
        }

        var info = new ProcessImageInfo(
            Pid: pid,
            ImagePath: imagePath,
            ImageName: Path.GetFileName(imagePath),
            StartTimeUnixMs: startUnixMs);

        lock (_gate)
        {
            // PID reuse: overwrite. The previous entry's bytes were already
            // attributed correctly under its session; the new entry takes over
            // from this point.
            _byPid[pid] = new CacheEntry(info, ExitedAtUnixMs: null);
            // ETW saw the process; flush any stale negative cache hit for it.
            if (_negativeCache.Remove(pid))
            {
                // Don't bother repacking _negativeCacheInsertionOrder — the
                // queue is allowed to contain dead PIDs; we re-check on
                // dequeue.
            }
        }
    }

    public void OnProcessStop(int pid, long stopUnixMs)
    {
        if (pid <= 0) return;

        lock (_gate)
        {
            if (_byPid.TryGetValue(pid, out var entry))
            {
                _byPid[pid] = entry with { ExitedAtUnixMs = stopUnixMs };
                var scheduledEvictAt = stopUnixMs + GraceMs + 1;
                if (scheduledEvictAt < _nextEvictAtUnixMs)
                {
                    _nextEvictAtUnixMs = scheduledEvictAt;
                }
            }
        }
    }

    public ProcessImageInfo? Resolve(int pid)
    {
        if (pid == SystemPid) return SystemImage;
        if (pid <= 0) return null;

        var now = _now();

        lock (_gate)
        {
            if (now >= _nextEvictAtUnixMs)
            {
                EvictStale(now);
                _nextEvictAtUnixMs = ComputeNextEvictAt();
            }

            if (_byPid.TryGetValue(pid, out var entry))
            {
                return entry.Image;
            }

            // Short-circuit if we already know this PID is dead.
            if (_negativeCache.TryGetValue(pid, out var negExpiry))
            {
                if (negExpiry > now)
                {
                    return null;
                }
                _negativeCache.Remove(pid);
            }
        }

        // Cache miss: PID we didn't see start (started before ZenVizor, or we
        // missed the start event). Fall back to a one-shot Win32 lookup.
        var resolved = TryResolveViaWin32(pid);
        if (resolved is null)
        {
            // Remember the miss for a short window so subsequent trailing
            // events from this PID don't each re-throw ArgumentException
            // inside Process.GetProcessById.
            lock (_gate)
            {
                AddNegativeCacheEntry(pid, now + NegativeCacheTtlMs);
            }
            return null;
        }

        lock (_gate)
        {
            // Re-check under the lock in case another thread cached it meanwhile.
            if (!_byPid.ContainsKey(pid))
            {
                _byPid[pid] = new CacheEntry(resolved, ExitedAtUnixMs: null);
            }
        }
        return resolved;
    }

    /// <summary>
    /// Seed the cache with every currently-running process. Called once at
    /// startup before the ETW session begins so traffic from pre-existing
    /// processes resolves on first observation.
    /// </summary>
    public void PrimeFromRunningProcesses()
    {
        int primed = 0;
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var info = BuildFromLiveProcess(proc);
                    if (info is null)
                    {
                        continue;
                    }
                    lock (_gate)
                    {
                        _byPid[info.Pid] = new CacheEntry(info, ExitedAtUnixMs: null);
                    }
                    primed++;
                }
                catch
                {
                    // Per-process failures are common (protected processes,
                    // race with exit). Skip and continue.
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PrimeFromRunningProcesses failed; resolver will rely on ETW + on-demand lookups.");
        }

        _logger.LogInformation("ProcessLifecycleResolver primed with {Count} running processes.", primed);
    }

    private void EvictStale(long nowUnixMs)
    {
        // Caller MUST hold _gate.
        List<int>? toRemove = null;
        foreach (var (pid, entry) in _byPid)
        {
            if (entry.ExitedAtUnixMs is long exited &&
                nowUnixMs - exited > GraceMs)
            {
                (toRemove ??= new List<int>()).Add(pid);
            }
        }
        if (toRemove is null) return;
        foreach (var pid in toRemove)
        {
            _byPid.Remove(pid);
        }
    }

    /// <summary>
    /// Caller MUST hold _gate. Returns the earliest Unix-ms at which a
    /// pending-exit entry's grace window will expire, or
    /// <see cref="long.MaxValue"/> if no entries have an exit timestamp.
    /// </summary>
    private long ComputeNextEvictAt()
    {
        var earliestExit = long.MaxValue;
        foreach (var (_, entry) in _byPid)
        {
            if (entry.ExitedAtUnixMs is long exited && exited < earliestExit)
            {
                earliestExit = exited;
            }
        }
        return earliestExit == long.MaxValue
            ? long.MaxValue
            : earliestExit + GraceMs + 1;
    }

    /// <summary>
    /// Caller MUST hold _gate. Adds a negative-cache entry, evicting the
    /// oldest one (FIFO) if the cache is at capacity. The insertion-order
    /// queue may contain stale entries (e.g. a PID that was later seen via
    /// ETW); those are skipped on dequeue.
    /// </summary>
    private void AddNegativeCacheEntry(int pid, long expiryUnixMs)
    {
        if (_negativeCache.Count >= NegativeCacheCapacity)
        {
            while (_negativeCacheInsertionOrder.Count > 0)
            {
                var oldest = _negativeCacheInsertionOrder.Dequeue();
                if (_negativeCache.Remove(oldest))
                {
                    break;
                }
                // queue had a stale entry; keep dequeueing
            }
        }
        _negativeCache[pid] = expiryUnixMs;
        _negativeCacheInsertionOrder.Enqueue(pid);
    }

    private ProcessImageInfo? TryResolveViaWin32(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return BuildFromLiveProcess(process);
        }
        catch (ArgumentException)
        {
            // Process no longer exists.
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Win32 fallback Process.GetProcessById({Pid}) failed.", pid);
            return null;
        }
    }

    private static ProcessImageInfo? BuildFromLiveProcess(Process process)
    {
        long startMs;
        try
        {
            startMs = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero)
                .ToUnixTimeMilliseconds();
        }
        catch
        {
            // Some system processes deny StartTime; we still want them in the cache.
            startMs = 0;
        }

        string imagePath;
        try
        {
            imagePath = process.MainModule?.FileName ?? process.ProcessName;
        }
        catch
        {
            // Protected processes deny MainModule access; fall back to name only.
            imagePath = process.ProcessName;
        }

        if (string.IsNullOrEmpty(imagePath)) return null;

        return new ProcessImageInfo(
            Pid: process.Id,
            ImagePath: imagePath,
            ImageName: Path.GetFileName(imagePath),
            StartTimeUnixMs: startMs);
    }

    private readonly record struct CacheEntry(ProcessImageInfo Image, long? ExitedAtUnixMs);
}
