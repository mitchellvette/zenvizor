namespace ZenVizor.Capture.Sni;

/// <summary>
/// Phase 8.6 — strips the L2 Ethernet header off a PktMon payload to expose the
/// IP-layer packet the substrate-agnostic <see cref="SniPacketProcessor"/>
/// expects. Used <b>only</b> by the PktMon adapter: PktMon delivers full
/// Ethernet frames (<c>dstMAC|srcMAC|EtherType|…</c>, Phase 8.5 findings §8.1),
/// whereas the raw-socket substrate already starts at the IP header and does
/// not call this. Keeping the strip here keeps the IP/TCP/UDP walk
/// substrate-agnostic.
/// </summary>
internal static class EthernetFrame
{
    private const ushort EtherTypeIPv4 = 0x0800;
    private const ushort EtherTypeIPv6 = 0x86DD;
    private const ushort EtherTypeVlan = 0x8100;   // 802.1Q
    private const ushort EtherTypeVlanQinQ = 0x88A8; // 802.1ad (stacked)
    private const int EthernetHeaderLength = 14;     // dst(6) + src(6) + type(2)
    private const int VlanTagLength = 4;             // TCI(2) + inner type(2)

    /// <summary>
    /// Strip the L2 header (plus up to two stacked VLAN tags) and return the
    /// IPv4/IPv6 payload. Returns false — and never throws — on a frame that is
    /// too short or carries a non-IP EtherType.
    /// </summary>
    public static bool TryStripToIp(ReadOnlySpan<byte> frame, out ReadOnlySpan<byte> ip)
    {
        if (TryGetIpOffset(frame, out var offset))
        {
            ip = frame[offset..];
            return true;
        }
        ip = default;
        return false;
    }

    /// <summary>
    /// Compute the byte offset of the IP header within an Ethernet frame, after
    /// skipping the L2 header and any VLAN tags. Lets the PktMon adapter slice a
    /// <see cref="System.ReadOnlyMemory{T}"/> without copying. Returns false on a
    /// short frame or a non-IP EtherType; never throws.
    /// </summary>
    public static bool TryGetIpOffset(ReadOnlySpan<byte> frame, out int ipOffset)
    {
        ipOffset = 0;
        if (frame.Length < EthernetHeaderLength) return false;

        var offset = 12;
        var etherType = (ushort)((frame[offset] << 8) | frame[offset + 1]);
        offset += 2;

        for (var i = 0; i < 2 && (etherType == EtherTypeVlan || etherType == EtherTypeVlanQinQ); i++)
        {
            if (offset + VlanTagLength > frame.Length) return false;
            offset += 2; // skip TCI
            etherType = (ushort)((frame[offset] << 8) | frame[offset + 1]);
            offset += 2;
        }

        if (etherType != EtherTypeIPv4 && etherType != EtherTypeIPv6) return false;
        if (offset > frame.Length) return false;

        ipOffset = offset;
        return true;
    }
}
