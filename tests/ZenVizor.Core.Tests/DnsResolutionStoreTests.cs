using System.Net;
using FluentAssertions;
using ZenVizor.Core.Dns;

namespace ZenVizor.Core.Tests;

public sealed class DnsResolutionStoreTests
{
    private static readonly IPAddress Ip1 = IPAddress.Parse("13.107.42.14");
    private static readonly IPAddress Ip2 = IPAddress.Parse("2606:4700:20::ac43:4a55");
    private static readonly IPAddress Ip3 = IPAddress.Parse("1.2.3.4");

    [Fact]
    public void Record_then_TryGet_returns_hostname_when_unexpired()
    {
        var store = new DnsResolutionStore();
        store.Record(Ip1, "outlook.office.com", ttlSeconds: 60, observedAtUnixMs: 1_000_000);

        var hit = store.TryGetHostname(Ip1, nowUnixMs: 1_000_500, out var hostname);

        hit.Should().BeTrue();
        hostname.Should().Be("outlook.office.com");
    }

    [Fact]
    public void TryGet_returns_false_for_missing_ip()
    {
        var store = new DnsResolutionStore();
        store.Record(Ip1, "outlook.office.com", 60, 1_000_000);

        var hit = store.TryGetHostname(Ip2, 1_000_500, out var hostname);

        hit.Should().BeFalse();
        hostname.Should().BeEmpty();
    }

    [Fact]
    public void TryGet_returns_false_after_ttl_elapses()
    {
        var store = new DnsResolutionStore();
        store.Record(Ip1, "outlook.office.com", ttlSeconds: 30, observedAtUnixMs: 1_000_000);

        // 30s TTL → expires at 1_030_000. At exactly the expiry, lookup MUST miss.
        store.TryGetHostname(Ip1, nowUnixMs: 1_030_000, out _).Should().BeFalse();
        store.TryGetHostname(Ip1, nowUnixMs: 1_030_001, out _).Should().BeFalse();
        store.TryGetHostname(Ip1, nowUnixMs: 1_029_999, out _).Should().BeTrue();
    }

    [Fact]
    public void Record_overwrites_existing_hostname_for_same_ip()
    {
        var store = new DnsResolutionStore();
        store.Record(Ip1, "old.example.com", 60, 1_000_000);
        store.Record(Ip1, "new.example.com", 60, 1_005_000);

        store.TryGetHostname(Ip1, 1_005_500, out var hostname).Should().BeTrue();
        hostname.Should().Be("new.example.com");
        store.Count.Should().Be(1);
    }

    [Fact]
    public void Record_clamps_zero_or_negative_ttl_to_one_second()
    {
        var store = new DnsResolutionStore();
        store.Record(Ip1, "edge.example.com", ttlSeconds: 0, observedAtUnixMs: 1_000_000);

        // Without clamping, expiresAt would equal observedAt and the entry
        // would already be expired at the same millisecond. The clamp gives
        // it at least one second of life — verify the boundary.
        store.TryGetHostname(Ip1, nowUnixMs: 1_000_999, out _).Should().BeTrue();
        store.TryGetHostname(Ip1, nowUnixMs: 1_001_000, out _).Should().BeFalse();
    }

    [Fact]
    public void Record_at_capacity_evicts_least_recently_recorded()
    {
        var store = new DnsResolutionStore(capacity: 2);
        store.Record(Ip1, "first.example.com",  60, 1_000_000);
        store.Record(Ip2, "second.example.com", 60, 1_000_001);
        // Both present; Ip1 at the tail.
        store.Count.Should().Be(2);

        store.Record(Ip3, "third.example.com", 60, 1_000_002);

        store.Count.Should().Be(2);
        store.TryGetHostname(Ip1, 1_000_500, out _).Should().BeFalse();   // evicted
        store.TryGetHostname(Ip2, 1_000_500, out var h2).Should().BeTrue();
        store.TryGetHostname(Ip3, 1_000_500, out var h3).Should().BeTrue();
        h2.Should().Be("second.example.com");
        h3.Should().Be("third.example.com");
    }

    [Fact]
    public void Re_recording_existing_ip_moves_it_to_head_so_other_entry_evicts_first()
    {
        var store = new DnsResolutionStore(capacity: 2);
        store.Record(Ip1, "first.example.com",  60, 1_000_000);
        store.Record(Ip2, "second.example.com", 60, 1_000_001);

        // Re-record Ip1 — it moves to head; Ip2 becomes the tail.
        store.Record(Ip1, "first-refreshed.example.com", 60, 1_000_002);

        // Now insert a third → Ip2 should be the one evicted, not Ip1.
        store.Record(Ip3, "third.example.com", 60, 1_000_003);

        store.TryGetHostname(Ip1, 1_000_500, out var h1).Should().BeTrue();
        store.TryGetHostname(Ip2, 1_000_500, out _).Should().BeFalse();
        store.TryGetHostname(Ip3, 1_000_500, out _).Should().BeTrue();
        h1.Should().Be("first-refreshed.example.com");
    }

    [Fact]
    public void EvictExpired_drops_only_entries_past_expiry_and_returns_count()
    {
        var store = new DnsResolutionStore();
        store.Record(Ip1, "shortlived.example.com",  ttlSeconds: 10,  observedAtUnixMs: 1_000_000);
        store.Record(Ip2, "longerlived.example.com", ttlSeconds: 600, observedAtUnixMs: 1_000_000);

        var dropped = store.EvictExpired(nowUnixMs: 1_010_000); // shortlived expires at 1_010_000

        dropped.Should().Be(1);
        store.Count.Should().Be(1);
        store.TryGetHostname(Ip1, 1_010_001, out _).Should().BeFalse();
        store.TryGetHostname(Ip2, 1_010_001, out var h2).Should().BeTrue();
        h2.Should().Be("longerlived.example.com");
    }

    [Fact]
    public void EvictExpired_with_nothing_to_drop_returns_zero()
    {
        var store = new DnsResolutionStore();
        store.Record(Ip1, "alive.example.com", 60, 1_000_000);

        store.EvictExpired(nowUnixMs: 1_000_500).Should().Be(0);
        store.Count.Should().Be(1);
    }

    [Fact]
    public void Constructor_rejects_non_positive_capacity()
    {
        var act0 = () => new DnsResolutionStore(capacity: 0);
        var actNeg = () => new DnsResolutionStore(capacity: -1);
        act0.Should().Throw<ArgumentOutOfRangeException>();
        actNeg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Record_rejects_null_ip_and_blank_hostname()
    {
        var store = new DnsResolutionStore();
        var actNullIp = () => store.Record(null!, "x.example.com", 60, 1_000_000);
        var actBlank  = () => store.Record(Ip1, "   ",            60, 1_000_000);
        var actEmpty  = () => store.Record(Ip1, "",               60, 1_000_000);
        actNullIp.Should().Throw<ArgumentNullException>();
        actBlank.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Concurrent_record_and_lookup_does_not_throw_or_corrupt_state()
    {
        // Smoke test: 4 writers + 4 readers hammer the store; the assertion is
        // simply "no exception escapes and the final state is consistent."
        // The store uses a single mutex so this only proves the lock holds —
        // not throughput.
        var store = new DnsResolutionStore(capacity: 4096);
        var rng = new Random(42);
        var ips = Enumerable.Range(0, 1024)
            .Select(i => IPAddress.Parse($"10.0.{i / 256}.{i % 256}"))
            .ToArray();
        const long now = 1_000_000;
        var stop = DateTime.UtcNow.AddMilliseconds(250);

        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var local = new Random(rng.Next());
            while (DateTime.UtcNow < stop)
            {
                var ip = ips[local.Next(ips.Length)];
                store.Record(ip, $"h{ip.GetAddressBytes()[3]}.example.com", ttlSeconds: 60, observedAtUnixMs: now);
            }
        })).ToArray();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var local = new Random(rng.Next());
            while (DateTime.UtcNow < stop)
            {
                store.TryGetHostname(ips[local.Next(ips.Length)], now + 1000, out string _);
            }
        })).ToArray();

        await Task.WhenAll(writers.Concat(readers));
        store.Count.Should().BeLessThanOrEqualTo(store.Capacity);
    }
}
