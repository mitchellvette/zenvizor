using System.Net;
using FluentAssertions;
using ZenVizor.Capture.Sni;

namespace ZenVizor.Core.Tests.Sni;

public sealed class SniFlowTrackerTests
{
    private static SniFlowKey Key(int localPort = 50000) =>
        new(IPAddress.Parse("203.0.113.10"), 443, (ushort)localPort, 6);

    [Fact]
    public void Tcp_accumulation_concatenates_segments_in_order()
    {
        var tracker = new SniFlowTracker();
        var key = Key();

        tracker.TryAccumulateTcp(key, new byte[] { 1, 2, 3 }, out var first).Should().BeTrue();
        first.Should().Equal(1, 2, 3);

        tracker.TryAccumulateTcp(key, new byte[] { 4, 5 }, out var second).Should().BeTrue();
        second.Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public void Classified_flow_is_not_reparsed()
    {
        var tracker = new SniFlowTracker();
        var key = Key();

        tracker.TryAccumulateTcp(key, new byte[] { 1, 2 }, out _).Should().BeTrue();
        tracker.MarkClassified(key);

        tracker.TryAccumulateTcp(key, new byte[] { 3, 4 }, out var assembled).Should().BeFalse();
        assembled.Should().BeEmpty();
        tracker.IsClassified(key).Should().BeTrue();
    }

    [Fact]
    public void Per_flow_byte_cap_abandons_and_classifies_the_flow()
    {
        var tracker = new SniFlowTracker(perFlowCapBytes: 8);
        var key = Key();

        tracker.TryAccumulateTcp(key, new byte[6], out _).Should().BeTrue();
        // Next segment pushes past the 8-byte cap -> give up, mark classified.
        tracker.TryAccumulateTcp(key, new byte[6], out var assembled).Should().BeFalse();
        assembled.Should().BeEmpty();
        tracker.IsClassified(key).Should().BeTrue();
    }

    [Fact]
    public void Udp_attempt_budget_is_exhausted_then_flow_is_classified()
    {
        var tracker = new SniFlowTracker(maxUdpAttempts: 3);
        var key = Key();

        tracker.TryBeginUdp(key).Should().BeTrue();
        tracker.TryBeginUdp(key).Should().BeTrue();
        tracker.TryBeginUdp(key).Should().BeTrue();
        // 4th attempt exceeds the budget -> false + classified.
        tracker.TryBeginUdp(key).Should().BeFalse();
        tracker.IsClassified(key).Should().BeTrue();
    }

    [Fact]
    public void Parallel_connections_to_same_endpoint_do_not_interleave()
    {
        var tracker = new SniFlowTracker();
        var a = Key(localPort: 50001);
        var b = Key(localPort: 50002);

        tracker.TryAccumulateTcp(a, new byte[] { 0xAA }, out _);
        tracker.TryAccumulateTcp(b, new byte[] { 0xBB }, out var bufB);

        // Distinct local ports => distinct flows => no cross-contamination.
        bufB.Should().Equal(0xBB);
        tracker.Count.Should().Be(2);
    }

    [Fact]
    public void Lru_evicts_least_recently_touched_flow_at_capacity()
    {
        var tracker = new SniFlowTracker(capacity: 2);
        var a = Key(localPort: 1);
        var b = Key(localPort: 2);
        var c = Key(localPort: 3);

        tracker.TryAccumulateTcp(a, new byte[] { 1 }, out _);
        tracker.TryAccumulateTcp(b, new byte[] { 1 }, out _);
        // Touch a so b becomes the LRU tail.
        tracker.TryAccumulateTcp(a, new byte[] { 2 }, out _);
        // Inserting c evicts b.
        tracker.TryAccumulateTcp(c, new byte[] { 1 }, out _);

        tracker.Count.Should().Be(2);
        // b was evicted: a fresh accumulate starts a new buffer (no history).
        tracker.TryAccumulateTcp(b, new byte[] { 9 }, out var bufB).Should().BeTrue();
        bufB.Should().Equal(9);
    }
}
