// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Capture.Sni;

/// <summary>
/// Phase 8.6 — a receive-only source of <b>IP-layer</b> packets feeding the SNI
/// extractor. Two implementations: <see cref="PktMonPacketSource"/> (primary,
/// in-kernel port filter + truncation) and <see cref="RawSocketPacketSource"/>
/// (fallback, <c>SIO_RCVALL</c>). Both are invariant-#1-safe: strictly
/// receive-only, never connected, never send a byte.
/// <para>
/// The callback is invoked synchronously on the substrate's capture thread with
/// a buffer that is only valid for the duration of the call — the consumer
/// copies what it needs (the flow accumulator) before returning. Implementations
/// hand IP-layer bytes (L2 already stripped where applicable), keeping the
/// downstream IP/TCP/UDP walk substrate-agnostic.
/// </para>
/// </summary>
internal interface IRawPacketSource : IDisposable
{
    /// <summary>
    /// Begin delivering IP-layer packets to <paramref name="onIpPacket"/>.
    /// Throws on a hard start failure (substrate cannot be opened); the caller
    /// treats SNI capture as non-load-bearing and logs + continues.
    /// </summary>
    void Start(Action<ReadOnlyMemory<byte>> onIpPacket);

    /// <summary>True once the substrate has terminated unexpectedly.</summary>
    bool IsFaulted { get; }
}
