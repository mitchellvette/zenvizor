using System.Net;
using ZenVizor.Core.Observations;

namespace ZenVizor.Core.Attribution;

/// <summary>
/// A point-in-time map of <c>(protocol, local endpoint) → owning PID</c>,
/// sourced from <c>GetExtendedTcpTable</c> / <c>GetExtendedUdpTable</c>.
/// PRD §8 step 2: this is a CORRECTION layer over the ETW PID, not redundancy —
/// it fixes the receive-path PID-ambiguity that ETW alone cannot.
/// </summary>
public sealed class PidTableSnapshot
{
    private readonly Dictionary<EndpointKey, int> _byEndpoint;

    public PidTableSnapshot(
        long takenAtUnixMs,
        IEnumerable<PidTableEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        TakenAtUnixMs = takenAtUnixMs;

        _byEndpoint = new Dictionary<EndpointKey, int>();
        var pids = new HashSet<int>();
        foreach (var entry in entries)
        {
            var key = new EndpointKey(entry.Protocol, entry.LocalEndpoint);
            // Last-write-wins on duplicate keys; the IP Helper tables shouldn't
            // produce them but we tolerate the edge gracefully.
            _byEndpoint[key] = entry.OwningPid;
            pids.Add(entry.OwningPid);
        }

        Pids = pids;
    }

    /// <summary>When this snapshot was taken (Unix-ms).</summary>
    public long TakenAtUnixMs { get; }

    /// <summary>All distinct PIDs that owned at least one endpoint in this snapshot.</summary>
    public IReadOnlySet<int> Pids { get; }

    /// <summary>
    /// Look up the owning PID for a given local endpoint.
    /// </summary>
    public bool TryGetOwningPid(Protocol protocol, IPEndPoint localEndpoint, out int owningPid)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);
        return _byEndpoint.TryGetValue(new EndpointKey(protocol, localEndpoint), out owningPid);
    }

    public static PidTableSnapshot Empty(long takenAtUnixMs) =>
        new(takenAtUnixMs, Array.Empty<PidTableEntry>());

    private readonly record struct EndpointKey(Protocol Protocol, IPEndPoint LocalEndpoint)
    {
        public bool Equals(EndpointKey other) =>
            Protocol == other.Protocol &&
            LocalEndpoint.Port == other.LocalEndpoint.Port &&
            LocalEndpoint.Address.Equals(other.LocalEndpoint.Address);

        public override int GetHashCode() =>
            HashCode.Combine(Protocol, LocalEndpoint.Address, LocalEndpoint.Port);
    }
}

public sealed record PidTableEntry(
    Protocol Protocol,
    IPEndPoint LocalEndpoint,
    int OwningPid);
