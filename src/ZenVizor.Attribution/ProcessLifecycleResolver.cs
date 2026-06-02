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
///         once the grace window has elapsed.</item>
///   <item>PID reuse: a new <see cref="OnProcessStart"/> for a PID that is
///         already in the cache overwrites the entry (correct attribution
///         to the new process).</item>
/// </list>
/// </para>
/// <para>
/// <see cref="Resolve"/> falls back to a Win32 <see cref="Process.GetProcessById"/>
/// lookup for PIDs not in the cache (covers processes that started before
/// ZenVizor was running or any miss due to ETW buffer overrun).
/// <see cref="PrimeFromRunningProcesses"/> seeds the cache at startup to
/// reduce the fallback rate to near-zero.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ProcessLifecycleResolver : IProcessImageResolver, IProcessLifecycleSink
{
    public const long DefaultGraceMs = 60_000;
    private const int SystemPid = 4;

    private static readonly ProcessImageInfo SystemImage = new(
        Pid: SystemPid,
        ImagePath: "(kernel)",
        ImageName: "System",
        StartTimeUnixMs: 0);

    private readonly Dictionary<int, CacheEntry> _byPid = new();
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly Func<long> _now;

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
            EvictStale(now);

            if (_byPid.TryGetValue(pid, out var entry))
            {
                return entry.Image;
            }
        }

        // Cache miss: PID we didn't see start (started before ZenVizor, or we
        // missed the start event). Fall back to a one-shot Win32 lookup and
        // populate the cache with whatever we learn so future calls hit.
        var resolved = TryResolveViaWin32(pid);
        if (resolved is null) return null;

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
