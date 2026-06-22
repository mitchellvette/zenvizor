// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;

namespace ZenVizor.Capture.Sni;

/// <summary>
/// Flow key for the per-flow gate. A client connection is uniquely identified
/// by (remote IP, remote port, local/ephemeral port, protocol) — the local
/// port distinguishes the parallel connections a browser opens to the same CDN
/// edge so their TCP segments don't interleave in one accumulation buffer.
/// </summary>
internal readonly record struct SniFlowKey(IPAddress RemoteIp, ushort RemotePort, ushort LocalPort, byte Protocol);

/// <summary>
/// Phase 8.6 — the mandatory per-flow "already-classified" gate. Same
/// bounded-LRU shape as <see cref="ZenVizor.Core.Dns.DnsResolutionStore"/>: it
/// keeps steady-state cost on the <i>new-flow</i> rate rather than the packet
/// rate (Phase 8.5 findings §8.3). Without it, a bulk download would re-parse
/// the ClientHello slot of every inbound data packet.
/// <para>
/// Per flow it tracks: a "classified" flag (set once we extract a hostname, or
/// give up), a TCP accumulation buffer (to ride over ClientHello segmentation,
/// capped so a flow that never yields an SNI can't grow unbounded), and a UDP
/// attempt counter (QUIC Initials are self-contained datagrams; we give up
/// after a few non-Initial datagrams on a UDP/443 flow).
/// </para>
/// <para>Thread-safe: a single mutex guards all state. Writes are infrequent
/// (new-flow rate), so contention is not a concern at the target workload.</para>
/// </summary>
internal sealed class SniFlowTracker
{
    /// <summary>Max tracked flows before LRU eviction of the least-recently-touched.</summary>
    public const int DefaultCapacity = 4096;

    /// <summary>
    /// Per-flow TCP accumulation cap. A realistic TLS 1.3 ClientHello fits well
    /// under this; the cap exists so a flow that never produces a parseable
    /// ClientHello is abandoned rather than buffered indefinitely.
    /// </summary>
    public const int DefaultPerFlowCapBytes = 8 * 1024;

    /// <summary>
    /// Give up on a UDP/443 flow after this many datagrams fail to parse as a
    /// QUIC v1 Initial — it isn't QUIC v1, or the SNI wasn't in the first
    /// datagram (cross-datagram CRYPTO reassembly is out of scope).
    /// </summary>
    public const int DefaultMaxUdpAttempts = 16;

    private readonly int _capacity;
    private readonly int _perFlowCapBytes;
    private readonly int _maxUdpAttempts;
    private readonly LinkedList<Flow> _order = new();
    private readonly Dictionary<SniFlowKey, LinkedListNode<Flow>> _index;
    private readonly object _gate = new();

    public SniFlowTracker(
        int capacity = DefaultCapacity,
        int perFlowCapBytes = DefaultPerFlowCapBytes,
        int maxUdpAttempts = DefaultMaxUdpAttempts)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (perFlowCapBytes <= 0) throw new ArgumentOutOfRangeException(nameof(perFlowCapBytes));
        if (maxUdpAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxUdpAttempts));
        _capacity = capacity;
        _perFlowCapBytes = perFlowCapBytes;
        _maxUdpAttempts = maxUdpAttempts;
        _index = new Dictionary<SniFlowKey, LinkedListNode<Flow>>(capacity);
    }

    /// <summary>Current tracked-flow count. Diagnostic + test surface.</summary>
    public int Count
    {
        get { lock (_gate) { return _index.Count; } }
    }

    /// <summary>
    /// TCP path: append <paramref name="payload"/> to this flow's buffer and
    /// hand back the accumulated bytes for a parse attempt. Returns false —
    /// with no parse attempt — when the flow is already classified or has
    /// exceeded the per-flow byte cap (the latter marks the flow classified so
    /// it is never revisited).
    /// </summary>
    public bool TryAccumulateTcp(in SniFlowKey key, ReadOnlySpan<byte> payload, out byte[] assembled)
    {
        assembled = Array.Empty<byte>();
        lock (_gate)
        {
            var flow = GetOrCreate(key);
            if (flow.Classified) return false;

            flow.Buffer ??= new List<byte>(payload.Length);
            flow.Buffer.AddRange(payload);
            if (flow.Buffer.Count > _perFlowCapBytes)
            {
                flow.Classified = true; // give up; free the buffer
                flow.Buffer = null;
                return false;
            }
            assembled = flow.Buffer.ToArray();
            return true;
        }
    }

    /// <summary>
    /// UDP path: register a parse attempt for this flow. Returns false — caller
    /// should not parse — when the flow is already classified or has burned
    /// through its attempt budget (the latter marks it classified).
    /// </summary>
    public bool TryBeginUdp(in SniFlowKey key)
    {
        lock (_gate)
        {
            var flow = GetOrCreate(key);
            if (flow.Classified) return false;
            flow.UdpAttempts++;
            if (flow.UdpAttempts > _maxUdpAttempts)
            {
                flow.Classified = true;
                return false;
            }
            return true;
        }
    }

    /// <summary>True if the flow has been classified (hostname found or abandoned).</summary>
    public bool IsClassified(in SniFlowKey key)
    {
        lock (_gate)
        {
            return _index.TryGetValue(key, out var node) && node.Value.Classified;
        }
    }

    /// <summary>
    /// Mark a flow classified — called after a successful hostname extraction
    /// so no further packet on this 4-tuple is re-parsed. Frees the TCP buffer.
    /// </summary>
    public void MarkClassified(in SniFlowKey key)
    {
        lock (_gate)
        {
            var flow = GetOrCreate(key);
            flow.Classified = true;
            flow.Buffer = null;
        }
    }

    // Caller must hold _gate. Promotes an existing flow to the LRU head, or
    // creates one (evicting the LRU tail at capacity).
    private Flow GetOrCreate(in SniFlowKey key)
    {
        if (_index.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value;
        }

        if (_index.Count >= _capacity)
        {
            var evicted = _order.Last!;
            _order.RemoveLast();
            _index.Remove(evicted.Value.Key);
        }

        var flow = new Flow(key);
        var fresh = new LinkedListNode<Flow>(flow);
        _order.AddFirst(fresh);
        _index[key] = fresh;
        return flow;
    }

    private sealed class Flow(SniFlowKey key)
    {
        public readonly SniFlowKey Key = key;
        public bool Classified;
        public List<byte>? Buffer;
        public int UdpAttempts;
    }
}
