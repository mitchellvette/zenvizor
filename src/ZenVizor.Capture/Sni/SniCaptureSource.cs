using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Core.Dns;

namespace ZenVizor.Capture.Sni;

/// <summary>Which packet substrate <see cref="SniCaptureSource"/> drives.</summary>
public enum SniSubstrate
{
    /// <summary>
    /// Primary — in-kernel <c>Microsoft-Windows-PktMon</c> capture with a
    /// server-port filter. Sees IPv4 and IPv6.
    /// </summary>
    PktMon,

    /// <summary>
    /// Fallback — receive-only <c>SIO_RCVALL</c> raw sockets. Needs no PktMon
    /// control surface but is IPv4-only (per-address-family).
    /// </summary>
    RawSocket,
}

/// <summary>
/// Phase 8.6 — passive SNI / QUIC-SNI / HTTP-Host observer. Closes the Phase 8
/// DoH gap: Chrome and other in-app/DoH resolvers bypass the Windows DNS
/// resolver, so the DNS observer sees nothing for them. This source recovers the
/// hostname from the wire instead — the unencrypted SNI in a TLS ClientHello,
/// the SNI inside a decrypted QUIC v1 Initial, and the HTTP/1.1 Host header —
/// and writes <c>(remote IP → hostname)</c> into the SAME
/// <see cref="DnsResolutionStore"/> the DNS observer feeds. The aggregator
/// stamps <c>connections.resolved_host</c> at flush; no IPC/UI/schema change.
/// <para>
/// INVARIANT #1: strictly observational, originates ZERO traffic of its own.
/// Both substrates are receive-only (see <see cref="IRawPacketSource"/>). The
/// QUIC decrypt uses BCL crypto only (<c>System.Security.Cryptography</c>) — no
/// network-calling dependency.
/// </para>
/// <para>
/// Sibling to <see cref="ZenVizor.Capture.Dns.DnsCaptureSource"/> (Phase 8
/// decision D5 — lifecycle isolation): a fault here cannot disturb DNS
/// observation or the load-bearing kernel-network capture. Eviction of the
/// shared store is driven by the DNS source's evict loop; the per-flow LRU gate
/// here bounds SNI's own working set independently.
/// </para>
/// <para>
/// Residual gap: TLS 1.3 Encrypted ClientHello (ECH) encrypts the SNI, so it is
/// not recoverable from the wire — the documented structural limit carried
/// forward from Phase 8 (DoH) into Phase 8.6.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SniCaptureSource : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly Func<long> _now;
    private readonly SniPacketProcessor _processor;
    private readonly IRawPacketSource _packetSource;
    private bool _started;

    public SniCaptureSource(
        DnsResolutionStore store,
        ILogger<SniCaptureSource>? logger = null,
        SniSubstrate substrate = SniSubstrate.PktMon,
        Func<long>? now = null)
        : this(store, BuildSubstrate(substrate, (ILogger?)logger), logger, now)
    {
    }

    /// <summary>Test seam — inject a fake <see cref="IRawPacketSource"/>.</summary>
    internal SniCaptureSource(
        DnsResolutionStore store,
        IRawPacketSource packetSource,
        ILogger? logger = null,
        Func<long>? now = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _packetSource = packetSource ?? throw new ArgumentNullException(nameof(packetSource));
        _logger = logger ?? NullLogger.Instance;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _processor = new SniPacketProcessor(store, new SniFlowTracker());
    }

    /// <summary>True once the underlying substrate has terminated unexpectedly.</summary>
    public bool IsFaulted => _packetSource.IsFaulted;

    /// <summary>Count of hostnames extracted + recorded. Diagnostic surface.</summary>
    public long Hits => _processor.Hits;

    /// <summary>
    /// Begin capturing. Throws on a hard substrate-start failure; the caller
    /// treats SNI capture as non-load-bearing (logs + continues), exactly as the
    /// DNS observer is treated.
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _packetSource.Start(OnIpPacket);
        _logger.LogInformation("SNI capture started.");
    }

    private void OnIpPacket(ReadOnlyMemory<byte> ip) =>
        _processor.ProcessIpPacket(ip.Span, _now());

    private static IRawPacketSource BuildSubstrate(SniSubstrate substrate, ILogger? logger) =>
        substrate switch
        {
            SniSubstrate.PktMon => new PktMonPacketSource(logger: logger),
            SniSubstrate.RawSocket => new RawSocketPacketSource(logger),
            _ => throw new ArgumentOutOfRangeException(nameof(substrate), substrate, null),
        };

    public ValueTask DisposeAsync()
    {
        try { _packetSource.Dispose(); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }
}
