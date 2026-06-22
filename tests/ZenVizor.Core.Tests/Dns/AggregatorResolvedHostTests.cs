using System.Net;
using FluentAssertions;
using ZenVizor.Core.Aggregation;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Dns;
using ZenVizor.Core.Observations;
using ZenVizor.Core.Tests.Fakes;

namespace ZenVizor.Core.Tests.Dns;

/// <summary>
/// Verifies the Phase 8 flush-time DNS lookup: <see cref="TrafficAggregator"/>
/// reads from the supplied <see cref="IDnsResolutionStore"/> when building
/// each <see cref="ZenVizor.Core.Storage.PendingConnection"/>, and degrades to
/// <c>ResolvedHost = null</c> cleanly when the store is absent or misses.
/// </summary>
public sealed class AggregatorResolvedHostTests
{
    private static NetworkObservation Obs(
        long ts, int pid, IPEndPoint local, IPEndPoint remote,
        Direction direction, long bytes) =>
        new(ts, pid, Protocol.Tcp, local, remote, direction, bytes);

    private sealed class Harness
    {
        public InMemoryProcessImageResolver Resolver { get; } = new();
        public InMemoryPidTableSource SnapshotSource { get; } = new();
        public FakeFlushSink Sink { get; } = new();
        public SessionTracker Tracker { get; }
        public TrafficAggregator Aggregator { get; }
        public DnsResolutionStore? DnsStore { get; }

        public long FakeNowUnixMs { get; set; }

        public Harness(DnsResolutionStore? dnsStore = null, long initialNowUnixMs = 1_000_000)
        {
            FakeNowUnixMs = initialNowUnixMs;
            DnsStore = dnsStore;
            Tracker = new SessionTracker(Resolver);
            Aggregator = new TrafficAggregator(
                Tracker,
                new PidCorrector(),
                SnapshotSource,
                Sink,
                nowProvider: () => FakeNowUnixMs,
                dnsStore: dnsStore);
        }
    }

    [Fact]
    public void Flush_with_null_dns_store_emits_pending_connections_with_null_resolved_host()
    {
        // Pre-Phase-8 behaviour MUST remain intact when no store is wired.
        var h = new Harness(dnsStore: null);
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 0));
        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);
        h.Aggregator.Observe(Obs(1_000_000, 100, local, remote, Direction.Up, 500));

        h.Aggregator.Flush(1_000_500);

        h.Sink.AllConnections.Should().ContainSingle()
            .Which.ResolvedHost.Should().BeNull();
    }

    [Fact]
    public void Flush_with_store_hit_stamps_hostname_on_pending_connection()
    {
        var store = new DnsResolutionStore();
        store.Record(IPAddress.Parse("8.8.8.8"), "dns.google", ttlSeconds: 300, observedAtUnixMs: 1_000_000);

        var h = new Harness(dnsStore: store);
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 0));
        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);
        h.Aggregator.Observe(Obs(1_000_100, 100, local, remote, Direction.Up, 500));

        h.Aggregator.Flush(1_000_500);

        h.Sink.AllConnections.Should().ContainSingle()
            .Which.ResolvedHost.Should().Be("dns.google");
    }

    [Fact]
    public void Flush_with_store_miss_emits_null_resolved_host()
    {
        var store = new DnsResolutionStore();
        // Record a name for an unrelated IP — lookup for 8.8.8.8 will miss.
        store.Record(IPAddress.Parse("1.1.1.1"), "one.one.one.one", 300, 1_000_000);

        var h = new Harness(dnsStore: store);
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 0));
        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);
        h.Aggregator.Observe(Obs(1_000_100, 100, local, remote, Direction.Up, 500));

        h.Aggregator.Flush(1_000_500);

        h.Sink.AllConnections.Should().ContainSingle()
            .Which.ResolvedHost.Should().BeNull();
    }

    [Fact]
    public void Flush_uses_now_for_ttl_check_so_expired_entries_miss()
    {
        var store = new DnsResolutionStore();
        store.Record(IPAddress.Parse("8.8.8.8"), "dns.google", ttlSeconds: 10, observedAtUnixMs: 1_000_000);

        // FakeNow advances past the TTL before Flush runs — store should miss.
        var h = new Harness(dnsStore: store, initialNowUnixMs: 1_000_000);
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 0));
        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);
        h.Aggregator.Observe(Obs(1_000_100, 100, local, remote, Direction.Up, 500));

        h.FakeNowUnixMs = 1_020_000;     // 20 s later — past the 10 s TTL
        h.Aggregator.Flush(h.FakeNowUnixMs);

        h.Sink.AllConnections.Should().ContainSingle()
            .Which.ResolvedHost.Should().BeNull();
    }
}
