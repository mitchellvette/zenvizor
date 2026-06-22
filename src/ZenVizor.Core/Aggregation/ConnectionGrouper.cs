using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Aggregation;

/// <summary>
/// Collapses flat <see cref="ConnectionRow"/> rows — keyed by
/// <c>(Protocol, RemoteAddress, RemotePort)</c> on the wire — into
/// <see cref="EndpointGroup"/> rows keyed by **endpoint identity**: the
/// resolved hostname when present, else the bare remote address.
///
/// Phase 9.5 lives here rather than in the storage SQL so the 1.0.0 IPC
/// contract stays at <c>IpcSchemaVersion.Query = 2</c> (no positional
/// bump right before the ship gate) and the collapse logic is testable
/// with deterministic synthetic input in <c>ZenVizor.Core.Tests</c>.
/// </summary>
public static class ConnectionGrouper
{
    /// <summary>
    /// Group the flat rows by endpoint identity. Output is sorted
    /// by total bytes (Up + Down) descending, then identity ascending —
    /// matches the server's pre-collapse ordering so a single-port group
    /// looks indistinguishable from the old flat row at the same position.
    /// </summary>
    public static IReadOnlyList<EndpointGroup> Collapse(IReadOnlyList<ConnectionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return Array.Empty<EndpointGroup>();

        var byIdentity = new Dictionary<string, GroupAccumulator>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var normalizedHost = string.IsNullOrWhiteSpace(row.ResolvedHost)
                ? null
                : row.ResolvedHost;
            var identity = normalizedHost ?? row.RemoteAddress;

            if (!byIdentity.TryGetValue(identity, out var acc))
            {
                acc = new GroupAccumulator(identity, normalizedHost);
                byIdentity[identity] = acc;
            }
            acc.Add(row);
        }

        var groups = new List<EndpointGroup>(byIdentity.Count);
        foreach (var acc in byIdentity.Values)
        {
            groups.Add(acc.Build());
        }

        groups.Sort(static (a, b) =>
        {
            var byBytes = (b.BytesUp + b.BytesDown).CompareTo(a.BytesUp + a.BytesDown);
            return byBytes != 0
                ? byBytes
                : string.CompareOrdinal(a.Identity, b.Identity);
        });
        return groups;
    }

    private sealed class GroupAccumulator
    {
        private readonly string _identity;
        private readonly string? _resolvedHost;
        private readonly HashSet<string> _addresses = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Protocol, int Port), PortAccumulator> _ports = new();
        private long _bytesUp;
        private long _bytesDown;
        private long _firstSeen = long.MaxValue;
        private long _lastSeen = long.MinValue;
        private int _connectionCount;
        private bool _anyWan;

        public GroupAccumulator(string identity, string? resolvedHost)
        {
            _identity = identity;
            _resolvedHost = resolvedHost;
        }

        public void Add(ConnectionRow row)
        {
            _addresses.Add(row.RemoteAddress);
            _bytesUp += row.BytesUp;
            _bytesDown += row.BytesDown;
            if (row.FirstSeenUnixMs < _firstSeen) _firstSeen = row.FirstSeenUnixMs;
            if (row.LastSeenUnixMs > _lastSeen) _lastSeen = row.LastSeenUnixMs;
            _connectionCount++;
            if (string.Equals(row.RemoteClass, "Wan", StringComparison.OrdinalIgnoreCase))
            {
                _anyWan = true;
            }

            var key = (row.Protocol, row.RemotePort);
            if (!_ports.TryGetValue(key, out var port))
            {
                port = new PortAccumulator(row.Protocol, row.RemotePort);
                _ports[key] = port;
            }
            port.Add(row);
        }

        public EndpointGroup Build()
        {
            // Resolved hostname picks the lexicographically-first non-null —
            // matches the storage layer's MAX(resolved_host) policy so a
            // group whose rows mostly carry the same hostname doesn't flip
            // identity when one straggler row arrives without a name. The
            // identity itself is what we group on, so this only matters for
            // round-tripping ResolvedHost back into the DTO when the rows
            // disagree (rare — CDN-aliasing only).
            var addresses = _addresses.ToArray();
            Array.Sort(addresses, StringComparer.Ordinal);

            var ports = new List<EndpointPortChild>(_ports.Count);
            foreach (var p in _ports.Values)
            {
                ports.Add(p.Build());
            }
            // Children sorted by bytes DESC then proto ASC then port ASC so
            // the expand-in-place row order is deterministic for tests and
            // matches the "biggest first" reading direction the parent uses.
            ports.Sort(static (a, b) =>
            {
                var byBytes = (b.BytesUp + b.BytesDown).CompareTo(a.BytesUp + a.BytesDown);
                if (byBytes != 0) return byBytes;
                var byProto = string.CompareOrdinal(a.Protocol, b.Protocol);
                return byProto != 0 ? byProto : a.Port.CompareTo(b.Port);
            });

            return new EndpointGroup(
                Identity:           _identity,
                ResolvedHost:       _resolvedHost,
                Addresses:          addresses,
                RemoteClass:        _anyWan ? "Wan" : "Local",
                BytesUp:            _bytesUp,
                BytesDown:          _bytesDown,
                ConnectionCount:    _connectionCount,
                DistinctPortCount:  _ports.Count,
                FirstSeenUnixMs:    _firstSeen,
                LastSeenUnixMs:     _lastSeen,
                Ports:              ports);
        }
    }

    private sealed class PortAccumulator
    {
        private readonly string _protocol;
        private readonly int _port;
        private long _bytesUp;
        private long _bytesDown;
        private int _connectionCount;

        public PortAccumulator(string protocol, int port)
        {
            _protocol = protocol;
            _port = port;
        }

        public void Add(ConnectionRow row)
        {
            _bytesUp += row.BytesUp;
            _bytesDown += row.BytesDown;
            _connectionCount++;
        }

        public EndpointPortChild Build() => new(
            Protocol:        _protocol,
            Port:            _port,
            BytesUp:         _bytesUp,
            BytesDown:       _bytesDown,
            ConnectionCount: _connectionCount);
    }
}

/// <summary>
/// One row in the collapsed Connections grid. Identity is the resolved
/// hostname when <see cref="ResolvedHost"/> is non-null, else the bare
/// remote address. A group may cover multiple <see cref="Addresses"/>
/// (CDN-fronted hostname fanout) and multiple <see cref="Ports"/>
/// (same identity, different ports/protocols). A group with one address
/// and one port renders indistinguishably from the pre-9.5 flat row.
/// </summary>
public sealed record EndpointGroup(
    string Identity,
    string? ResolvedHost,
    IReadOnlyList<string> Addresses,
    string RemoteClass,
    long BytesUp,
    long BytesDown,
    int ConnectionCount,
    int DistinctPortCount,
    long FirstSeenUnixMs,
    long LastSeenUnixMs,
    IReadOnlyList<EndpointPortChild> Ports);

/// <summary>
/// One per-port detail row inside an <see cref="EndpointGroup"/>.
/// Aggregated across all underlying <see cref="ConnectionRow"/>s that
/// shared this <c>(Protocol, Port)</c> within the group — so a CDN
/// hostname carrying TCP/443 across four IPs surfaces as one
/// <c>TCP / 443</c> child row, not four.
/// </summary>
public sealed record EndpointPortChild(
    string Protocol,
    int Port,
    long BytesUp,
    long BytesDown,
    int ConnectionCount);
