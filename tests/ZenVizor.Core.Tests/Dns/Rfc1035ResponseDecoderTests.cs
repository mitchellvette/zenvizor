using System.Net;
using FluentAssertions;
using ZenVizor.Capture.Dns;

namespace ZenVizor.Core.Tests.Dns;

public sealed class Rfc1035ResponseDecoderTests
{
    [Fact]
    public void Plain_A_record_decodes_to_qname_and_ipv4()
    {
        var packet = DnsFixtures.Response(
            qname: "example.com",
            DnsFixtures.A("example.com", ttl: 300, ipv4: "93.184.216.34"));

        var answers = Rfc1035ResponseDecoder.Decode(packet);

        answers.Should().ContainSingle();
        answers[0].Hostname.Should().Be("example.com");
        answers[0].Ip.Should().Be(IPAddress.Parse("93.184.216.34"));
        answers[0].TtlSeconds.Should().Be(300);
    }

    [Fact]
    public void Plain_AAAA_record_decodes_to_qname_and_ipv6()
    {
        var packet = DnsFixtures.Response(
            qname: "example.com",
            DnsFixtures.Aaaa("example.com", ttl: 60, ipv6: "2606:2800:220:1:248:1893:25c8:1946"));

        var answers = Rfc1035ResponseDecoder.Decode(packet);

        answers.Should().ContainSingle();
        answers[0].Hostname.Should().Be("example.com");
        answers[0].Ip.Should().Be(IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"));
        answers[0].TtlSeconds.Should().Be(60);
    }

    [Fact]
    public void CNAME_chain_resolves_terminal_A_record_to_QNAME_with_min_TTL()
    {
        // QNAME = outlook.office.com →
        //   CNAME → outlook.office365.com.s-0001.s-msedge.net →
        //   A     → 52.96.222.114
        // The IP record's name is the CNAME target; the decoder should
        // emit (52.96.222.114, "outlook.office.com", min(CNAME ttl, A ttl)).
        var packet = DnsFixtures.Response(
            qname: "outlook.office.com",
            DnsFixtures.Cname("outlook.office.com", ttl: 30, target: "outlook.office365.com.s-0001.s-msedge.net"),
            DnsFixtures.A    ("outlook.office365.com.s-0001.s-msedge.net", ttl: 60, ipv4: "52.96.222.114"));

        var answers = Rfc1035ResponseDecoder.Decode(packet);

        answers.Should().ContainSingle();
        answers[0].Hostname.Should().Be("outlook.office.com");
        answers[0].Ip.Should().Be(IPAddress.Parse("52.96.222.114"));
        answers[0].TtlSeconds.Should().Be(30); // min(30, 60)
    }

    [Fact]
    public void Two_link_CNAME_chain_carries_minimum_TTL_across_the_chain()
    {
        // A → B (ttl 200) → C (ttl 10) → 1.2.3.4 (ttl 500). Min = 10.
        var packet = DnsFixtures.Response(
            qname: "a.example.com",
            DnsFixtures.Cname("a.example.com", ttl: 200, target: "b.example.com"),
            DnsFixtures.Cname("b.example.com", ttl: 10,  target: "c.example.com"),
            DnsFixtures.A    ("c.example.com", ttl: 500, ipv4: "1.2.3.4"));

        var answers = Rfc1035ResponseDecoder.Decode(packet);

        answers.Should().ContainSingle();
        answers[0].Hostname.Should().Be("a.example.com");
        answers[0].TtlSeconds.Should().Be(10);
    }

    [Fact]
    public void Multiple_A_records_for_same_qname_emit_one_answer_per_ip()
    {
        var packet = DnsFixtures.Response(
            qname: "edge.example.com",
            DnsFixtures.A("edge.example.com", ttl: 60, ipv4: "10.0.0.1"),
            DnsFixtures.A("edge.example.com", ttl: 60, ipv4: "10.0.0.2"),
            DnsFixtures.A("edge.example.com", ttl: 60, ipv4: "10.0.0.3"));

        var answers = Rfc1035ResponseDecoder.Decode(packet);

        answers.Should().HaveCount(3);
        answers.Select(a => a.Ip).Should().Equal(
            IPAddress.Parse("10.0.0.1"),
            IPAddress.Parse("10.0.0.2"),
            IPAddress.Parse("10.0.0.3"));
        answers.Should().AllSatisfy(a => a.Hostname.Should().Be("edge.example.com"));
    }

    [Fact]
    public void Unrelated_A_record_with_no_chain_link_to_QNAME_is_dropped()
    {
        // Response includes an A record for a totally unrelated name. The
        // decoder must not emit it under QNAME.
        var packet = DnsFixtures.Response(
            qname: "wanted.example.com",
            DnsFixtures.A("wanted.example.com",     ttl: 60, ipv4: "10.0.0.1"),
            DnsFixtures.A("unrelated.example.com",  ttl: 60, ipv4: "172.16.0.1"));

        var answers = Rfc1035ResponseDecoder.Decode(packet);

        answers.Should().ContainSingle();
        answers[0].Ip.Should().Be(IPAddress.Parse("10.0.0.1"));
        answers[0].Hostname.Should().Be("wanted.example.com");
    }

    [Fact]
    public void Cname_only_response_without_terminal_A_emits_nothing()
    {
        var packet = DnsFixtures.Response(
            qname: "a.example.com",
            DnsFixtures.Cname("a.example.com", ttl: 60, target: "b.example.com"));

        Rfc1035ResponseDecoder.Decode(packet).Should().BeEmpty();
    }

    [Fact]
    public void Empty_answer_section_returns_empty()
    {
        var packet = DnsFixtures.Response(qname: "example.com");
        Rfc1035ResponseDecoder.Decode(packet).Should().BeEmpty();
    }

    [Fact]
    public void Query_QR_zero_returns_empty()
    {
        Rfc1035ResponseDecoder.Decode(DnsFixtures.Query("example.com")).Should().BeEmpty();
    }

    [Fact]
    public void Truncated_response_TC_flag_returns_empty()
    {
        Rfc1035ResponseDecoder.Decode(DnsFixtures.TruncatedResponse("example.com")).Should().BeEmpty();
    }

    [Fact]
    public void Nx_domain_RCODE_nonzero_returns_empty()
    {
        Rfc1035ResponseDecoder.Decode(DnsFixtures.NxDomainResponse("nope.example.com")).Should().BeEmpty();
    }

    [Fact]
    public void Short_buffer_returns_empty()
    {
        Rfc1035ResponseDecoder.Decode(new byte[] { 0, 0, 0 }).Should().BeEmpty();
        Rfc1035ResponseDecoder.Decode(ReadOnlySpan<byte>.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Name_compression_pointer_to_QNAME_decodes_correctly()
    {
        // Real production responses ALWAYS compress the answer name as a
        // pointer to QNAME — uncompressed names in the answer section are
        // rare. Make sure the decoder follows the pointer.
        var packet = DnsFixtures.ResponseWithPointerToQname(
            qname: "example.com",
            ttl: 120,
            a4Ip: "1.2.3.4");

        var answers = Rfc1035ResponseDecoder.Decode(packet);

        answers.Should().ContainSingle();
        answers[0].Hostname.Should().Be("example.com");
        answers[0].Ip.Should().Be(IPAddress.Parse("1.2.3.4"));
        answers[0].TtlSeconds.Should().Be(120);
    }

    [Fact]
    public void Self_referencing_compression_pointer_is_rejected_without_hanging()
    {
        // The decoder's pointer-must-go-backward guard should bail; no
        // infinite loop, no exception, just an empty result.
        var packet = DnsFixtures.ResponseWithSelfReferentialPointer("example.com");
        Rfc1035ResponseDecoder.Decode(packet).Should().BeEmpty();
    }

    [Fact]
    public void Trailing_dot_on_QNAME_is_stripped_in_output_hostname()
    {
        // QNAME with explicit trailing dot — the decoder should normalise.
        var packet = DnsFixtures.Response(
            qname: "example.com.",
            DnsFixtures.A("example.com.", ttl: 60, ipv4: "1.2.3.4"));

        var answers = Rfc1035ResponseDecoder.Decode(packet);

        answers.Should().ContainSingle();
        answers[0].Hostname.Should().Be("example.com");
    }
}
