using System.Net;
using FluentAssertions;
using ZenVizor.Capture.Sni;
using ZenVizor.Core.Dns;

namespace ZenVizor.Core.Tests.Sni;

public sealed class SniPacketProcessorTests
{
    private const long Now = 1_700_000_000_000;

    private static SniPacketProcessor NewProcessor(out DnsResolutionStore store)
    {
        store = new DnsResolutionStore();
        return new SniPacketProcessor(store, new SniFlowTracker());
    }

    [Fact]
    public void Ipv4_tcp443_clienthello_records_exact_host()
    {
        var proc = NewProcessor(out var store);
        var hello = SniTestFixtures.BuildClientHelloRecord("www.google.com");
        var packet = SniTestFixtures.BuildIpv4Tcp("142.250.72.4", srcPort: 51514, dstPort: 443, hello);

        proc.ProcessIpPacket(packet, Now).Should().Be(SniIngestResult.Hit);

        store.TryGetHostname(IPAddress.Parse("142.250.72.4"), Now + 1000, out var host).Should().BeTrue();
        host.Should().Be("www.google.com");
        proc.Hits.Should().Be(1);
    }

    [Fact]
    public void Ipv4_udp443_quic_initial_records_exact_host()
    {
        var proc = NewProcessor(out var store);
        var dcid = Convert.FromHexString("0011223344556677");
        var initial = SniTestFixtures.BuildProtectedQuicInitial(dcid, "youtube.com");
        var packet = SniTestFixtures.BuildIpv4Udp("142.250.72.14", srcPort: 50000, dstPort: 443, initial);

        proc.ProcessIpPacket(packet, Now).Should().Be(SniIngestResult.Hit);

        store.TryGetHostname(IPAddress.Parse("142.250.72.14"), Now + 1000, out var host).Should().BeTrue();
        host.Should().Be("youtube.com");
    }

    [Fact]
    public void Ipv4_tcp80_http_records_exact_host()
    {
        var proc = NewProcessor(out var store);
        var req = System.Text.Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: neverssl.com\r\n\r\n");
        var packet = SniTestFixtures.BuildIpv4Tcp("13.107.21.200", srcPort: 49200, dstPort: 80, req);

        proc.ProcessIpPacket(packet, Now).Should().Be(SniIngestResult.Hit);

        store.TryGetHostname(IPAddress.Parse("13.107.21.200"), Now + 1000, out var host).Should().BeTrue();
        host.Should().Be("neverssl.com");
    }

    [Fact]
    public void Ipv6_tcp443_clienthello_records_exact_host()
    {
        var proc = NewProcessor(out var store);
        var hello = SniTestFixtures.BuildClientHelloRecord("ipv6.example.com");
        var packet = SniTestFixtures.BuildIpv6Tcp("2606:2800:220:1:248:1893:25c8:1946", 51500, 443, hello);

        proc.ProcessIpPacket(packet, Now).Should().Be(SniIngestResult.Hit);

        store.TryGetHostname(
            IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"), Now + 1000, out var host).Should().BeTrue();
        host.Should().Be("ipv6.example.com");
    }

    [Fact]
    public void Server_to_client_direction_is_ignored()
    {
        var proc = NewProcessor(out var store);
        var hello = SniTestFixtures.BuildClientHelloRecord("www.google.com");
        // Source port 443 (server), destination an ephemeral port -> not a candidate.
        var packet = SniTestFixtures.BuildIpv4Tcp("10.0.0.5", srcPort: 443, dstPort: 51514, hello);

        proc.ProcessIpPacket(packet, Now).Should().Be(SniIngestResult.Ignored);
        store.TryGetHostname(IPAddress.Parse("10.0.0.5"), Now + 1000, out _).Should().BeFalse();
    }

    [Fact]
    public void Non_target_port_is_ignored()
    {
        var proc = NewProcessor(out var store);
        var hello = SniTestFixtures.BuildClientHelloRecord("www.google.com");
        var packet = SniTestFixtures.BuildIpv4Tcp("10.0.0.6", srcPort: 51514, dstPort: 8443, hello);

        proc.ProcessIpPacket(packet, Now).Should().Be(SniIngestResult.Ignored);
        store.TryGetHostname(IPAddress.Parse("10.0.0.6"), Now + 1000, out _).Should().BeFalse();
    }

    [Fact]
    public void Truncated_ip_packet_is_ignored_without_throwing()
    {
        var proc = NewProcessor(out var store);
        var hello = SniTestFixtures.BuildClientHelloRecord("www.google.com");
        var packet = SniTestFixtures.BuildIpv4Tcp("10.0.0.7", 51514, 443, hello);
        var truncated = packet[..15]; // cut inside the IPv4 header

        proc.ProcessIpPacket(truncated, Now).Should().Be(SniIngestResult.Ignored);
        store.TryGetHostname(IPAddress.Parse("10.0.0.7"), Now + 1000, out _).Should().BeFalse();
    }

    [Fact]
    public void Empty_input_is_ignored()
    {
        var proc = NewProcessor(out _);
        proc.ProcessIpPacket(ReadOnlySpan<byte>.Empty, Now).Should().Be(SniIngestResult.Ignored);
    }

    [Fact]
    public void Classified_flow_gates_subsequent_packets_without_reparse()
    {
        var proc = NewProcessor(out _);
        var hello = SniTestFixtures.BuildClientHelloRecord("www.google.com");
        var packet = SniTestFixtures.BuildIpv4Tcp("142.250.72.4", srcPort: 51514, dstPort: 443, hello);

        proc.ProcessIpPacket(packet, Now).Should().Be(SniIngestResult.Hit);
        // Same 4-tuple again -> the gate drops it without re-parsing.
        proc.ProcessIpPacket(packet, Now).Should().Be(SniIngestResult.Gated);
        proc.Hits.Should().Be(1);
    }
}
