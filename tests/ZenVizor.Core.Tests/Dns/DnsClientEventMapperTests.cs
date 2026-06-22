using System.Net;
using FluentAssertions;
using ZenVizor.Capture.Dns;

namespace ZenVizor.Core.Tests.Dns;

public sealed class DnsClientEventMapperTests
{
    private const int DefaultTtl = 300;

    [Fact]
    public void Single_ipv4_result_emits_one_answer()
    {
        var result = DnsClientEventMapper.Map("example.com", "93.184.216.34", DefaultTtl);

        result.Should().ContainSingle();
        result[0].Hostname.Should().Be("example.com");
        result[0].Ip.Should().Be(IPAddress.Parse("93.184.216.34"));
        result[0].TtlSeconds.Should().Be(DefaultTtl);
    }

    [Fact]
    public void Trailing_dot_on_query_name_is_stripped()
    {
        var result = DnsClientEventMapper.Map("example.com.", "1.2.3.4", DefaultTtl);

        result.Should().ContainSingle();
        result[0].Hostname.Should().Be("example.com");
    }

    [Fact]
    public void Mixed_cname_and_ipv4_strips_cname_keeps_ips_under_qname()
    {
        // Production-shape payload: CNAME alias preceding the resolved IPs.
        var result = DnsClientEventMapper.Map(
            queryName:    "outlook.office.com",
            queryResults: "outlook.office365.com.s-0001.s-msedge.net;52.96.222.114;52.96.222.130",
            defaultTtlSeconds: DefaultTtl);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(a => a.Hostname.Should().Be("outlook.office.com"));
        result.Select(a => a.Ip).Should().BeEquivalentTo(new[]
        {
            IPAddress.Parse("52.96.222.114"),
            IPAddress.Parse("52.96.222.130"),
        });
    }

    [Fact]
    public void Trailing_semicolon_is_tolerated()
    {
        var result = DnsClientEventMapper.Map("example.com", "1.2.3.4;", DefaultTtl);
        result.Should().ContainSingle();
    }

    [Fact]
    public void Ipv6_results_decode()
    {
        var result = DnsClientEventMapper.Map(
            "example.com",
            "2606:2800:220:1:248:1893:25c8:1946",
            DefaultTtl);

        result.Should().ContainSingle();
        result[0].Ip.Should().Be(IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"));
    }

    [Fact]
    public void Type_prefix_variant_is_unwrapped()
    {
        // Some Windows builds emit "type:1 1.2.3.4;type:5 alias.example.com;".
        var result = DnsClientEventMapper.Map(
            "example.com",
            "type:5 alias.example.com;type:1 1.2.3.4;type:28 ::1",
            DefaultTtl);

        result.Should().HaveCount(2);
        result.Select(a => a.Ip).Should().BeEquivalentTo(new[]
        {
            IPAddress.Parse("1.2.3.4"),
            IPAddress.Parse("::1"),
        });
    }

    [Fact]
    public void Empty_query_name_returns_empty()
    {
        DnsClientEventMapper.Map("",   "1.2.3.4", DefaultTtl).Should().BeEmpty();
        DnsClientEventMapper.Map(null!, "1.2.3.4", DefaultTtl).Should().BeEmpty();
    }

    [Fact]
    public void Empty_query_results_returns_empty()
    {
        DnsClientEventMapper.Map("example.com", "",   DefaultTtl).Should().BeEmpty();
        DnsClientEventMapper.Map("example.com", null!, DefaultTtl).Should().BeEmpty();
    }

    [Fact]
    public void Only_cname_results_returns_empty()
    {
        // All tokens are textual hostnames — none parse as IPs.
        DnsClientEventMapper.Map(
            "example.com",
            "alias1.example.com;alias2.example.com",
            DefaultTtl).Should().BeEmpty();
    }
}

public sealed class DnsCaptureSourceIngestTests
{
    [Fact]
    public void Ingest_with_success_status_writes_answers_to_store()
    {
        var store = new ZenVizor.Core.Dns.DnsResolutionStore();
        var source = new DnsCaptureSource(store);

        source.Ingest(
            queryName:    "example.com",
            queryResults: "1.2.3.4;1.2.3.5",
            queryStatus:  0,
            observedAtUnixMs: 1_000_000);

        store.TryGetHostname(IPAddress.Parse("1.2.3.4"), 1_000_500, out var h1).Should().BeTrue();
        store.TryGetHostname(IPAddress.Parse("1.2.3.5"), 1_000_500, out var h2).Should().BeTrue();
        h1.Should().Be("example.com");
        h2.Should().Be("example.com");
    }

    [Fact]
    public void Ingest_with_non_zero_status_records_nothing_and_increments_ignored()
    {
        var store = new ZenVizor.Core.Dns.DnsResolutionStore();
        var source = new DnsCaptureSource(store);

        source.Ingest(
            queryName:    "nope.example.com",
            queryResults: "1.2.3.4",
            queryStatus:  9003, // ERROR_TIMEOUT for example
            observedAtUnixMs: 1_000_000);

        store.Count.Should().Be(0);
        source.EventsIgnored.Should().Be(1);
    }

    [Fact]
    public void Ingest_with_empty_results_increments_ignored_and_writes_nothing()
    {
        var store = new ZenVizor.Core.Dns.DnsResolutionStore();
        var source = new DnsCaptureSource(store);

        source.Ingest("example.com", "", 0, 1_000_000);

        store.Count.Should().Be(0);
        source.EventsIgnored.Should().Be(1);
    }
}
