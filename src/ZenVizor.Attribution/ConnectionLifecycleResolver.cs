using System.Net;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Observations;

namespace ZenVizor.Attribution;

/// <summary>
/// <see cref="IPidTableSnapshotSource"/> that is populated EAGERLY by kernel
/// ETW <c>TcpIpConnect</c> / <c>TcpIpAccept</c> events (via
/// <see cref="IConnectionLifecycleSink"/>). This is the Phase-3 fix for the
/// receive-path attribution race: when curl downloads 50 MB and exits in
/// &lt;1 s, the polled <see cref="IpHelper.IpHelperPidTableSource"/> can fail
/// to capture the connection at any point, and even if it does, Windows
/// reports <c>OwningPid = 0</c> once the process exits and the IP stack
/// loses the binding. With this resolver, the cache already has the
/// connection-to-PID mapping the moment the connect syscall fires, and
/// retains it for a grace window past disconnect so trailing receive events
/// still resolve.
/// </summary>
/// <remarks>
/// <para>Acts as a layered cache on top of a fallback <see cref="IPidTableSnapshotSource"/>
/// (typically the IpHelper poller). The fallback handles connections that
/// existed before ZenVizor started — for those we never see a connect event.</para>
/// <para><see cref="CurrentSnapshot"/> returns a merged view: cache entries
/// take precedence; the fallback's entries fill in everything the cache
/// hasn't observed yet.</para>
/// <para>The merged snapshot itself is cached. <see cref="TrafficAggregator"/>
/// reads <see cref="CurrentSnapshot"/> on every ETW event; without the cache,
/// each read allocates a fresh list, a HashSet, and scans the cache map.
/// Invalidation: any <see cref="OnConnect"/> / <see cref="OnDisconnect"/>
/// marks the cache stale; a fallback whose <c>TakenAtUnixMs</c> moves forward
/// (it refreshes ~1 Hz) also forces a rebuild; pending-exit entries whose
/// grace window expires force a rebuild via <c>_cacheValidUntilUnixMs</c>.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ConnectionLifecycleResolver : IPidTableSnapshotSource, IConnectionLifecycleSink
{
    public const long DefaultGraceMs = 60_000;

    private readonly IPidTableSnapshotSource _fallback;
    private readonly ILogger _logger;
    private readonly long _graceMs;
    private readonly Func<long> _now;
    private readonly Dictionary<EndpointKey, CacheEntry> _byEndpoint = new();
    private readonly object _gate = new();

    // Cached merged snapshot. Reused across CurrentSnapshot calls until
    // invalidated by a mutation, a fallback refresh, or the earliest pending
    // exit aging past _graceMs.
    private PidTableSnapshot? _cachedMerged;
    private long _cachedFallbackTakenAtUnixMs;
    private bool _cacheStale = true;
    private long _cacheValidUntilUnixMs = long.MaxValue;

    public ConnectionLifecycleResolver(
        IPidTableSnapshotSource fallback,
        ILogger<ConnectionLifecycleResolver>? logger = null,
        long graceMs = DefaultGraceMs,
        Func<long>? nowProvider = null)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _graceMs = graceMs;
        _now = nowProvider ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>Number of currently cached endpoints, for diagnostics/tests.</summary>
    public int CachedCount
    {
        get { lock (_gate) return _byEndpoint.Count; }
    }

    public void OnConnect(
        Protocol protocol,
        IPEndPoint localEndpoint,
        IPEndPoint remoteEndpoint,
        int pid,
        long timestampUnixMs)
    {
        if (pid <= 0 || localEndpoint is null) return;
        _ = remoteEndpoint;

        lock (_gate)
        {
            _byEndpoint[new EndpointKey(protocol, localEndpoint)] =
                new CacheEntry(pid, ExitedAtUnixMs: null);
            _cacheStale = true;
        }
    }

    public void OnDisconnect(
        Protocol protocol,
        IPEndPoint localEndpoint,
        long timestampUnixMs)
    {
        if (localEndpoint is null) return;

        lock (_gate)
        {
            var key = new EndpointKey(protocol, localEndpoint);
            if (_byEndpoint.TryGetValue(key, out var entry))
            {
                _byEndpoint[key] = entry with { ExitedAtUnixMs = timestampUnixMs };
                _cacheStale = true;
            }
        }
    }

    public PidTableSnapshot CurrentSnapshot
    {
        get
        {
            var fallback = _fallback.CurrentSnapshot;
            var now = _now();

            lock (_gate)
            {
                // Cheap reuse path: nothing local mutated, the fallback's
                // own snapshot hasn't advanced, and no pending-exit entry
                // has yet aged past its grace window. This is the path the
                // per-event hot caller (TrafficAggregator.Observe) takes
                // most of the time.
                if (!_cacheStale &&
                    _cachedMerged is not null &&
                    fallback.TakenAtUnixMs == _cachedFallbackTakenAtUnixMs &&
                    now < _cacheValidUntilUnixMs)
                {
                    return _cachedMerged;
                }

                EvictStale(now);

                // Merge: cache first (precedence), then fallback fills gaps.
                // We size the lists for the worst case so neither List<T>
                // resize nor HashSet rehash kicks in.
                var entries = new List<PidTableEntry>(_byEndpoint.Count + fallback.EntryCount);
                HashSet<EndpointKey>? seen = _byEndpoint.Count > 0
                    ? new HashSet<EndpointKey>(_byEndpoint.Count)
                    : null;

                foreach (var (key, entry) in _byEndpoint)
                {
                    entries.Add(new PidTableEntry(key.Protocol, key.LocalEndpoint, entry.Pid));
                    seen!.Add(key);
                }

                foreach (var fb in fallback.Entries)
                {
                    if (seen is not null)
                    {
                        var key = new EndpointKey(fb.Protocol, fb.LocalEndpoint);
                        if (seen.Contains(key)) continue;
                    }
                    entries.Add(fb);
                }

                var merged = new PidTableSnapshot(now, entries);
                _cachedMerged = merged;
                _cachedFallbackTakenAtUnixMs = fallback.TakenAtUnixMs;
                _cacheStale = false;
                _cacheValidUntilUnixMs = ComputeCacheValidUntil();
                return merged;
            }
        }
    }

    private void EvictStale(long nowUnixMs)
    {
        // Caller MUST hold _gate.
        List<EndpointKey>? toRemove = null;
        foreach (var (key, entry) in _byEndpoint)
        {
            if (entry.ExitedAtUnixMs is long exited &&
                nowUnixMs - exited > _graceMs)
            {
                (toRemove ??= new List<EndpointKey>()).Add(key);
            }
        }
        if (toRemove is null) return;
        foreach (var key in toRemove)
        {
            _byEndpoint.Remove(key);
        }
    }

    /// <summary>
    /// Caller MUST hold _gate. Returns the earliest Unix-ms at which a
    /// pending-exit entry will age past <c>_graceMs</c>, or
    /// <see cref="long.MaxValue"/> if no entries have an exit timestamp.
    /// The cache stays valid up to (but not including) this time.
    /// </summary>
    private long ComputeCacheValidUntil()
    {
        var earliestExit = long.MaxValue;
        foreach (var (_, entry) in _byEndpoint)
        {
            if (entry.ExitedAtUnixMs is long exited && exited < earliestExit)
            {
                earliestExit = exited;
            }
        }
        return earliestExit == long.MaxValue
            ? long.MaxValue
            : earliestExit + _graceMs + 1; // +1: "> _graceMs" is the eviction condition
    }

    private readonly record struct EndpointKey(Protocol Protocol, IPEndPoint LocalEndpoint)
    {
        public bool Equals(EndpointKey other) =>
            Protocol == other.Protocol &&
            LocalEndpoint.Port == other.LocalEndpoint.Port &&
            LocalEndpoint.Address.Equals(other.LocalEndpoint.Address);

        public override int GetHashCode() =>
            HashCode.Combine(Protocol, LocalEndpoint.Address, LocalEndpoint.Port);
    }

    private readonly record struct CacheEntry(int Pid, long? ExitedAtUnixMs);
}
