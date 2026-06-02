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
            }
        }
    }

    public PidTableSnapshot CurrentSnapshot
    {
        get
        {
            var now = _now();
            var fallback = _fallback.CurrentSnapshot;
            var entries = new List<PidTableEntry>(fallback.EntryCount + 16);
            var seen = new HashSet<EndpointKey>();

            lock (_gate)
            {
                EvictStale(now);

                foreach (var (key, entry) in _byEndpoint)
                {
                    entries.Add(new PidTableEntry(key.Protocol, key.LocalEndpoint, entry.Pid));
                    seen.Add(key);
                }
            }

            // Merge fallback entries that the cache hasn't observed.
            foreach (var fb in fallback.Entries)
            {
                var key = new EndpointKey(fb.Protocol, fb.LocalEndpoint);
                if (seen.Contains(key)) continue;
                entries.Add(fb);
            }

            return new PidTableSnapshot(now, entries);
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
