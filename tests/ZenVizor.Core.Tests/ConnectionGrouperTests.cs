using FluentAssertions;
using ZenVizor.Core.Aggregation;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Tests;

/// <summary>
/// Phase 9.5 CI gate. Asserts <see cref="ConnectionGrouper.Collapse"/>
/// produces exactly the rolled-up shape the App Detail Connections grid
/// renders — resolved hostnames collapse across CDN edge IPs and ports,
/// bare IPs collapse across ports only, distinct IPs stay as distinct
/// rows (the discovery > ranking invariant), and byte / port-count /
/// connection-count rollups are exact.
/// </summary>
public sealed class ConnectionGrouperTests
{
    private static ConnectionRow Row(
        string protocol,
        string addr,
        int port,
        string cls,
        long up,
        long down,
        long first,
        long last,
        string? host = null) => new(
            Protocol:        protocol,
            RemoteAddress:   addr,
            RemotePort:      port,
            RemoteClass:     cls,
            BytesUp:         up,
            BytesDown:       down,
            FirstSeenUnixMs: first,
            LastSeenUnixMs:  last,
            ResolvedHost:    host);

    [Fact]
    public void Collapse_EmptyInput_ReturnsEmpty()
    {
        ConnectionGrouper.Collapse(Array.Empty<ConnectionRow>())
            .Should().BeEmpty();
    }

    [Fact]
    public void Collapse_ResolvedHostnameAcrossManyEdgeIps_CollapsesToOneRow()
    {
        // The CDN-fronted case the brief calls out: same hostname,
        // four edge IPs, all TCP/443. Expect one group, four addresses,
        // one port child (because (proto, port) sub-aggregates within
        // the group), bytes summed exactly across all four.
        var rows = new[]
        {
            Row("TCP", "151.101.1.69",  443, "Wan", 10, 100, 1_000, 2_000, "edge.example.net"),
            Row("TCP", "151.101.65.69", 443, "Wan", 20, 200, 1_500, 2_500, "edge.example.net"),
            Row("TCP", "151.101.129.69",443, "Wan", 30, 300, 2_000, 3_000, "edge.example.net"),
            Row("TCP", "151.101.193.69",443, "Wan", 40, 400, 2_500, 3_500, "edge.example.net"),
        };

        var groups = ConnectionGrouper.Collapse(rows);

        groups.Should().ContainSingle();
        var g = groups[0];
        g.Identity.Should().Be("edge.example.net");
        g.ResolvedHost.Should().Be("edge.example.net");
        // Ordinal sort: '.' (0x2E) sorts before '0'..'9', and within
        // 1xx the second digit decides — so "1.69" < "129" < "193" < "65".
        g.Addresses.Should().Equal(
            "151.101.1.69", "151.101.129.69", "151.101.193.69", "151.101.65.69");
        g.RemoteClass.Should().Be("Wan");
        g.BytesUp.Should().Be(100);
        g.BytesDown.Should().Be(1_000);
        g.ConnectionCount.Should().Be(4);
        g.DistinctPortCount.Should().Be(1);
        g.FirstSeenUnixMs.Should().Be(1_000);
        g.LastSeenUnixMs.Should().Be(3_500);

        g.Ports.Should().ContainSingle();
        var child = g.Ports[0];
        child.Protocol.Should().Be("TCP");
        child.Port.Should().Be(443);
        child.BytesUp.Should().Be(100);
        child.BytesDown.Should().Be(1_000);
        child.ConnectionCount.Should().Be(4);
    }

    [Fact]
    public void Collapse_BareIpManyPorts_CollapsesToOneRowWithPortChildren()
    {
        // The unresolved-IP scan/swarm pattern: one IP, ten ports. We
        // want one group with ten distinct port children — the
        // *concentration* of bytes on a single unresolved IP is the
        // signal the per-app total can't express.
        var rows = new[]
        {
            Row("TCP", "2001:db8::1", 8001, "Wan", 100, 0, 1_000, 2_000),
            Row("TCP", "2001:db8::1", 8002, "Wan", 200, 0, 1_000, 2_000),
            Row("TCP", "2001:db8::1", 8003, "Wan", 300, 0, 1_000, 2_000),
            Row("TCP", "2001:db8::1", 8004, "Wan", 400, 0, 1_000, 2_000),
            Row("UDP", "2001:db8::1", 8004, "Wan",  50, 0, 1_000, 2_000),
        };

        var groups = ConnectionGrouper.Collapse(rows);

        groups.Should().ContainSingle();
        var g = groups[0];
        g.Identity.Should().Be("2001:db8::1");
        g.ResolvedHost.Should().BeNull();
        g.Addresses.Should().Equal("2001:db8::1");
        g.BytesUp.Should().Be(1_050);
        g.DistinctPortCount.Should().Be(5); // (TCP,8001..8004) + (UDP,8004)
        g.ConnectionCount.Should().Be(5);
        g.Ports.Should().HaveCount(5);
        // Highest-bytes port leads.
        g.Ports[0].Protocol.Should().Be("TCP");
        g.Ports[0].Port.Should().Be(8004);
        g.Ports[0].BytesUp.Should().Be(400);
    }

    [Fact]
    public void Collapse_DistinctUnresolvedIps_StayAsDistinctRows()
    {
        // discovery > ranking: a swarm of distinct unresolved IPs is
        // itself signal. They must NOT collapse into each other.
        var rows = new[]
        {
            Row("TCP", "203.0.113.1", 443, "Wan", 100, 100, 1_000, 2_000),
            Row("TCP", "203.0.113.2", 443, "Wan", 100, 100, 1_000, 2_000),
            Row("TCP", "203.0.113.3", 443, "Wan", 100, 100, 1_000, 2_000),
        };

        var groups = ConnectionGrouper.Collapse(rows);

        groups.Should().HaveCount(3);
        groups.Select(g => g.Identity).Should().BeEquivalentTo(
            new[] { "203.0.113.1", "203.0.113.2", "203.0.113.3" });
    }

    [Fact]
    public void Collapse_AnyWanInGroup_RollsUpToWan()
    {
        // The Q2 ruling: if a hostname spans IPs whose RemoteClass
        // disagrees (rare — CDN edges should all be WAN), the group
        // surfaces as WAN. WAN is the notable signal; folding a WAN
        // child into a "Local" parent would hide it.
        var rows = new[]
        {
            Row("TCP", "10.0.0.5",     443, "Local", 100, 100, 1_000, 2_000, "split.example.net"),
            Row("TCP", "203.0.113.10", 443, "Wan",   200, 200, 1_000, 2_000, "split.example.net"),
        };

        var groups = ConnectionGrouper.Collapse(rows);

        groups.Should().ContainSingle();
        groups[0].RemoteClass.Should().Be("Wan");
    }

    [Fact]
    public void Collapse_AllLocal_StaysLocal()
    {
        var rows = new[]
        {
            Row("UDP", "192.168.1.1", 5353, "Local", 100, 0, 1_000, 2_000),
            Row("UDP", "192.168.1.1", 1900, "Local", 200, 0, 1_000, 2_000),
        };

        var groups = ConnectionGrouper.Collapse(rows);

        groups.Should().ContainSingle();
        groups[0].RemoteClass.Should().Be("Local");
    }

    [Fact]
    public void Collapse_SortsGroupsByBytesDescThenIdentityAsc()
    {
        var rows = new[]
        {
            Row("TCP", "203.0.113.10", 443, "Wan",   10,    10, 1_000, 2_000),
            Row("TCP", "203.0.113.20", 443, "Wan", 1_000, 1_000, 1_000, 2_000),
            Row("TCP", "203.0.113.30", 443, "Wan",   500,   500, 1_000, 2_000),
        };

        var groups = ConnectionGrouper.Collapse(rows);

        groups.Select(g => g.Identity).Should().Equal(
            "203.0.113.20", "203.0.113.30", "203.0.113.10");
    }

    [Fact]
    public void Collapse_TiedBytes_BreaksTieByIdentityAscending()
    {
        var rows = new[]
        {
            Row("TCP", "203.0.113.20", 443, "Wan", 100, 100, 1_000, 2_000),
            Row("TCP", "203.0.113.10", 443, "Wan", 100, 100, 1_000, 2_000),
        };

        var groups = ConnectionGrouper.Collapse(rows);

        groups.Select(g => g.Identity).Should().Equal(
            "203.0.113.10", "203.0.113.20");
    }

    [Fact]
    public void Collapse_SinglePortBareIp_LooksIdenticalToOldFlatRow()
    {
        // A single-port, single-IP, no-hostname group should be
        // indistinguishable in shape from the pre-9.5 flat row:
        // one address, one port, count==1.
        var rows = new[]
        {
            Row("TCP", "8.8.8.8", 53, "Wan", 100, 1_000, 1_000, 2_000),
        };

        var groups = ConnectionGrouper.Collapse(rows);

        groups.Should().ContainSingle();
        var g = groups[0];
        g.Identity.Should().Be("8.8.8.8");
        g.ResolvedHost.Should().BeNull();
        g.Addresses.Should().Equal("8.8.8.8");
        g.Ports.Should().ContainSingle();
        g.DistinctPortCount.Should().Be(1);
        g.ConnectionCount.Should().Be(1);
    }

    [Fact]
    public void Collapse_PortChildren_SortedByBytesDescThenProtoThenPort()
    {
        // Children share a parent (one IP, many ports) and must sort
        // deterministically: bytes DESC, proto ASC, port ASC.
        var rows = new[]
        {
            Row("UDP", "203.0.113.5", 443,  "Wan", 100, 100, 1_000, 2_000),
            Row("TCP", "203.0.113.5", 443,  "Wan", 100, 100, 1_000, 2_000),
            Row("TCP", "203.0.113.5", 80,   "Wan", 500, 500, 1_000, 2_000),
            Row("TCP", "203.0.113.5", 8080, "Wan",  10,  10, 1_000, 2_000),
        };

        var groups = ConnectionGrouper.Collapse(rows);

        groups.Should().ContainSingle();
        var ports = groups[0].Ports;
        ports.Should().HaveCount(4);
        // (TCP, 80) leads on bytes; then the two 443s — TCP before UDP
        // by proto-ASC; then TCP/8080 last.
        ports[0].Should().Match<EndpointPortChild>(p => p.Protocol == "TCP" && p.Port == 80);
        ports[1].Should().Match<EndpointPortChild>(p => p.Protocol == "TCP" && p.Port == 443);
        ports[2].Should().Match<EndpointPortChild>(p => p.Protocol == "UDP" && p.Port == 443);
        ports[3].Should().Match<EndpointPortChild>(p => p.Protocol == "TCP" && p.Port == 8080);
    }

    [Fact]
    public void Collapse_NullVsEmptyResolvedHost_BothTreatedAsBareIp()
    {
        // The server's MAX(resolved_host) emits null when no session
        // had a name. Defensive: an empty/whitespace string from a
        // future source should not become a phantom identity.
        var rows = new[]
        {
            Row("TCP", "203.0.113.50", 443, "Wan", 100, 100, 1_000, 2_000, host: null),
            Row("TCP", "203.0.113.50", 80,  "Wan", 200, 200, 1_000, 2_000, host: "   "),
        };

        var groups = ConnectionGrouper.Collapse(rows);

        groups.Should().ContainSingle();
        groups[0].Identity.Should().Be("203.0.113.50");
        groups[0].ResolvedHost.Should().BeNull();
        groups[0].Ports.Should().HaveCount(2);
    }
}
