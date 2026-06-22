using System.Net;
using ZenVizor.Core.Dns;

namespace ZenVizor.Capture.Sni;

/// <summary>Outcome of a single packet ingest, for diagnostics + tests.</summary>
internal enum SniIngestResult
{
    /// <summary>Not a candidate (wrong protocol/port, too short, non-IP).</summary>
    Ignored,

    /// <summary>A candidate whose hostname is not (yet) extractable — keep feeding the flow.</summary>
    Accumulating,

    /// <summary>The flow is already classified; dropped without re-parse (the gate doing its job).</summary>
    Gated,

    /// <summary>A hostname was extracted and written to the store.</summary>
    Hit,
}

/// <summary>
/// Phase 8.6 — the substrate-agnostic heart of the SNI feeder. Takes an
/// <b>IP-layer</b> packet (IPv4 or IPv6; the L2 Ethernet header is stripped by
/// the PktMon adapter, the raw-socket substrate already starts at the IP
/// header), walks TCP/UDP, applies the per-flow gate, runs the matching parser,
/// and on success writes <c>(remote IP → hostname)</c> into the same
/// <see cref="DnsResolutionStore"/> the Phase 8 DNS observer feeds. The
/// aggregator reads that store at flush to stamp <c>connections.resolved_host</c>
/// — no IPC/UI/schema change (SNI reuses the v2 <c>ResolvedHost</c> field).
/// <para>
/// Direction: we select the client→server packets by destination port (TCP
/// 443/80, UDP 443). The remote (destination) IP is the store key. Server
/// responses arrive from port 443 to an ephemeral port and so never match the
/// filter — and the parsers self-validate (a ServerHello / data segment is not
/// a ClientHello) as a second line of defence.
/// </para>
/// <para>
/// Robustness contract, mirroring <c>Rfc1035ResponseDecoder</c>: every path
/// returns a result and NEVER throws on malformed input.
/// </para>
/// </summary>
internal sealed class SniPacketProcessor
{
    /// <summary>
    /// TTL stamped on every SNI-derived entry. SNI carries no TTL (same as DNS
    /// event 3008), so we substitute the same fixed default the DNS source uses
    /// — see <c>DnsCaptureSource.DefaultTtlSeconds</c>.
    /// </summary>
    public const int DefaultTtlSeconds = 300;

    private const byte ProtoTcp = 6;
    private const byte ProtoUdp = 17;
    private const ushort PortHttps = 443;
    private const ushort PortHttp = 80;

    // QUIC Initials are padded to ~1200 bytes; ignore tiny UDP/443 datagrams
    // (QUIC short-header packets, STUN, etc.) before attempting an expensive
    // key derivation + AEAD.
    private const int MinQuicInitialBytes = 64;

    private readonly DnsResolutionStore _store;
    private readonly SniFlowTracker _flows;
    private readonly int _ttlSeconds;
    private long _hits;

    public SniPacketProcessor(DnsResolutionStore store, SniFlowTracker flows, int ttlSeconds = DefaultTtlSeconds)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _flows = flows ?? throw new ArgumentNullException(nameof(flows));
        _ttlSeconds = ttlSeconds < 1 ? 1 : ttlSeconds;
    }

    /// <summary>Count of hostnames extracted + recorded. Diagnostic surface.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>
    /// Ingest one IP-layer packet. Returns the per-packet outcome; never throws.
    /// </summary>
    public SniIngestResult ProcessIpPacket(ReadOnlySpan<byte> ip, long observedAtUnixMs)
    {
        try
        {
            return ProcessCore(ip, observedAtUnixMs);
        }
        catch
        {
            // Defence in depth — the parsers already honour "never throw", but a
            // malformed header walk must not take down the capture thread.
            return SniIngestResult.Ignored;
        }
    }

    private SniIngestResult ProcessCore(ReadOnlySpan<byte> ip, long now)
    {
        if (ip.Length < 1) return SniIngestResult.Ignored;

        byte proto;
        IPAddress dstIp;
        ReadOnlySpan<byte> l4;

        var version = ip[0] >> 4;
        if (version == 4)
        {
            if (ip.Length < 20) return SniIngestResult.Ignored;
            var ihl = (ip[0] & 0x0f) * 4;
            if (ihl < 20 || ip.Length < ihl) return SniIngestResult.Ignored;
            proto = ip[9];
            if (proto != ProtoTcp && proto != ProtoUdp) return SniIngestResult.Ignored;
            dstIp = new IPAddress(ip.Slice(16, 4));
            l4 = ip[ihl..];
        }
        else if (version == 6)
        {
            if (ip.Length < 40) return SniIngestResult.Ignored;
            // Next Header. We only handle the common case where TCP/UDP follows
            // the fixed header directly; an extension-header chain on the FIRST
            // packet of a TLS/QUIC flow is vanishingly rare — bail gracefully.
            proto = ip[6];
            if (proto != ProtoTcp && proto != ProtoUdp) return SniIngestResult.Ignored;
            dstIp = new IPAddress(ip.Slice(24, 16));
            l4 = ip[40..];
        }
        else
        {
            return SniIngestResult.Ignored;
        }

        return proto == ProtoTcp
            ? ProcessTcp(l4, dstIp, now)
            : ProcessUdp(l4, dstIp, now);
    }

    private SniIngestResult ProcessTcp(ReadOnlySpan<byte> tcp, IPAddress dstIp, long now)
    {
        if (tcp.Length < 20) return SniIngestResult.Ignored;
        var srcPort = (ushort)((tcp[0] << 8) | tcp[1]);
        var dstPort = (ushort)((tcp[2] << 8) | tcp[3]);
        if (dstPort != PortHttps && dstPort != PortHttp) return SniIngestResult.Ignored;

        var dataOffset = (tcp[12] >> 4) * 4;
        if (dataOffset < 20 || tcp.Length < dataOffset) return SniIngestResult.Ignored;
        var payload = tcp[dataOffset..];
        if (payload.Length == 0) return SniIngestResult.Ignored;

        var key = new SniFlowKey(dstIp, dstPort, srcPort, ProtoTcp);
        if (!_flows.TryAccumulateTcp(key, payload, out var assembled))
        {
            return SniIngestResult.Gated;
        }

        var found = dstPort == PortHttps
            ? TlsClientHelloParser.TryParse(assembled, out var host)
            : HttpHostParser.TryParse(assembled, out host);
        if (!found) return SniIngestResult.Accumulating;

        Record(dstIp, host, key, now);
        return SniIngestResult.Hit;
    }

    private SniIngestResult ProcessUdp(ReadOnlySpan<byte> udp, IPAddress dstIp, long now)
    {
        if (udp.Length < 8) return SniIngestResult.Ignored;
        var srcPort = (ushort)((udp[0] << 8) | udp[1]);
        var dstPort = (ushort)((udp[2] << 8) | udp[3]);
        if (dstPort != PortHttps) return SniIngestResult.Ignored;

        var payload = udp[8..];
        if (payload.Length < MinQuicInitialBytes) return SniIngestResult.Ignored;

        var key = new SniFlowKey(dstIp, dstPort, srcPort, ProtoUdp);
        if (!_flows.TryBeginUdp(key)) return SniIngestResult.Gated;

        if (!QuicInitialParser.TryParse(payload, out var host)) return SniIngestResult.Accumulating;

        Record(dstIp, host, key, now);
        return SniIngestResult.Hit;
    }

    private void Record(IPAddress dstIp, string host, in SniFlowKey key, long now)
    {
        _store.Record(dstIp, host, _ttlSeconds, now);
        _flows.MarkClassified(key);
        Interlocked.Increment(ref _hits);
    }
}
