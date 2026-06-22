using FluentAssertions;
using ZenVizor.Capture.Sni;

namespace ZenVizor.Core.Tests.Sni;

public sealed class EthernetFrameTests
{
    private const ushort EtherTypeIPv4 = 0x0800;
    private const ushort EtherTypeIPv6 = 0x86DD;
    private const ushort EtherTypeVlan = 0x8100;
    private const ushort EtherTypeArp = 0x0806;

    [Fact]
    public void Strips_l2_header_to_ipv4_payload()
    {
        var ip = new byte[] { 0x45, 0x00, 0xDE, 0xAD };
        var frame = SniTestFixtures.BuildEthernet(EtherTypeIPv4, ip);

        EthernetFrame.TryStripToIp(frame, out var stripped).Should().BeTrue();
        stripped.ToArray().Should().Equal(ip);
    }

    [Fact]
    public void Computes_ip_offset_for_ipv6()
    {
        var ip = new byte[] { 0x60, 0x00, 0x00 };
        var frame = SniTestFixtures.BuildEthernet(EtherTypeIPv6, ip);

        EthernetFrame.TryGetIpOffset(frame, out var offset).Should().BeTrue();
        offset.Should().Be(14);
    }

    [Fact]
    public void Single_vlan_tag_is_skipped()
    {
        var ip = new byte[] { 0x45, 0x00 };
        var inner = new byte[14 + 4 + ip.Length];
        // dst+src MAC zero; outer EtherType = VLAN
        inner[12] = (byte)(EtherTypeVlan >> 8); inner[13] = (byte)(EtherTypeVlan & 0xFF);
        // TCI (2) then inner EtherType IPv4
        inner[16] = (byte)(EtherTypeIPv4 >> 8); inner[17] = (byte)(EtherTypeIPv4 & 0xFF);
        ip.CopyTo(inner.AsSpan(18));

        EthernetFrame.TryGetIpOffset(inner, out var offset).Should().BeTrue();
        offset.Should().Be(18);
    }

    [Fact]
    public void Non_ip_ethertype_is_rejected()
    {
        var frame = SniTestFixtures.BuildEthernet(EtherTypeArp, new byte[] { 1, 2, 3, 4 });

        EthernetFrame.TryStripToIp(frame, out _).Should().BeFalse();
    }

    [Fact]
    public void Short_frame_is_rejected_without_throwing()
    {
        EthernetFrame.TryStripToIp(new byte[] { 1, 2, 3 }, out _).Should().BeFalse();
        EthernetFrame.TryStripToIp(ReadOnlySpan<byte>.Empty, out _).Should().BeFalse();
    }
}
