using System.Net;
using FluentAssertions;
using ZenVizor.Core.Aggregation;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Observations;
using ZenVizor.Core.Tests.Fakes;

namespace ZenVizor.Core.Tests;

public sealed class TrafficAggregatorTests
{
    private static NetworkObservation Obs(
        long ts, int? pid, IPEndPoint local, IPEndPoint remote,
        Direction direction, long bytes, Protocol proto = Protocol.Tcp) =>
        new(ts, pid, proto, local, remote, direction, bytes);

    private sealed class Harness
    {
        public InMemoryProcessImageResolver Resolver { get; } = new();
        public InMemoryPidTableSource SnapshotSource { get; } = new();
        public FakeFlushSink Sink { get; } = new();
        public SessionTracker Tracker { get; }
        public TrafficAggregator Aggregator { get; }

        public long FakeNowUnixMs { get; set; }

        public Harness(long initialNowUnixMs = 0)
        {
            FakeNowUnixMs = initialNowUnixMs;
            Tracker = new SessionTracker(Resolver);
            Aggregator = new TrafficAggregator(
                Tracker,
                new PidCorrector(),
                SnapshotSource,
                Sink,
                nowProvider: () => FakeNowUnixMs);
        }
    }

    [Fact]
    public void Observe_NeverCallsSink_FlushIsTheSoleWritePath()
    {
        var h = new Harness();
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 0));

        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);

        for (var i = 0; i < 10; i++)
        {
            h.Aggregator.Observe(Obs(1000 + i, pid: 100, local, remote, Direction.Up, 100));
        }

        // Critical invariant: zero sink touches on the hot path.
        h.Sink.Batches.Should().BeEmpty();

        h.Aggregator.Flush(nowUnixMs: 6_000);
        h.Sink.Batches.Should().ContainSingle();
        h.Sink.AllSamples.Should().ContainSingle().Which.BytesUp.Should().Be(1000);
    }

    [Fact]
    public void Flush_FirstObservationOfPid_IncludesNewSession()
    {
        var h = new Harness();
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", StartTimeUnixMs: 200));

        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);
        h.Aggregator.Observe(Obs(1000, 100, local, remote, Direction.Up, 500));

        h.Aggregator.Flush(2000);

        h.Sink.AllNewSessions.Should().ContainSingle();
        var newSession = h.Sink.AllNewSessions.First();
        newSession.Pid.Should().Be(100);
        newSession.StartTimeUnixMs.Should().Be(200);
        newSession.App.ImagePath.Should().Be(@"C:\a\a.exe");
    }

    [Fact]
    public void Flush_SecondFlushForSamePid_OmitsNewSession_UsesKnownMap()
    {
        var h = new Harness();
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 200));

        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);

        h.Aggregator.Observe(Obs(1000, 100, local, remote, Direction.Up, 500));
        h.Aggregator.Flush(2000);

        h.Aggregator.Observe(Obs(7000, 100, local, remote, Direction.Up, 250));
        h.Aggregator.Flush(8000);

        h.Sink.Batches.Should().HaveCount(2);
        h.Sink.Batches[0].NewSessions.Should().ContainSingle();
        h.Sink.Batches[1].NewSessions.Should().BeEmpty();
        h.Sink.Batches[1].KnownPidToSessionId.Should().ContainKey(100);
    }

    [Fact]
    public void Observe_PidCorrection_AppliedBeforeAccumulation()
    {
        var h = new Harness();
        h.Resolver.Set(new ProcessImageInfo(4242, @"C:\a\real.exe", "real.exe", 0));

        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 51234);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);
        h.SnapshotSource.SetSnapshot(new PidTableSnapshot(0, new[]
        {
            new PidTableEntry(Protocol.Tcp, local, OwningPid: 4242),
        }));

        // Receive-path with wrong ETW PID — corrector should fix it.
        h.Aggregator.Observe(Obs(1000, pid: 0, local, remote, Direction.Down, 1024));
        h.Aggregator.Flush(2000);

        h.Sink.AllNewSessions.Should().ContainSingle().Which.Pid.Should().Be(4242);
    }

    [Fact]
    public void Observe_BucketAlignment_GroupsSamplesBy60s()
    {
        var h = new Harness();
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 0));

        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);

        h.Aggregator.Observe(Obs(60_500,  100, local, remote, Direction.Up,   100));
        h.Aggregator.Observe(Obs(119_999, 100, local, remote, Direction.Down, 200));
        h.Aggregator.Observe(Obs(120_000, 100, local, remote, Direction.Up,   50));
        h.Aggregator.Flush(200_000);

        var ordered = h.Sink.AllSamples.OrderBy(s => s.BucketStartUnixMs).ToList();
        ordered.Should().HaveCount(2);
        ordered[0].BucketStartUnixMs.Should().Be(60_000);
        ordered[0].BytesUp.Should().Be(100);
        ordered[0].BytesDown.Should().Be(200);
        ordered[1].BucketStartUnixMs.Should().Be(120_000);
        ordered[1].BytesUp.Should().Be(50);
    }

    [Fact]
    public void Observe_LocalAndWanRemotes_AccumulatedSeparately()
    {
        var h = new Harness();
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 0));

        var local   = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345);
        var lanPeer = new IPEndPoint(IPAddress.Parse("10.0.0.10"), 445);
        var wanPeer = new IPEndPoint(IPAddress.Parse("8.8.8.8"),   443);

        h.Aggregator.Observe(Obs(60_500, 100, local, lanPeer, Direction.Up, 200));
        h.Aggregator.Observe(Obs(60_500, 100, local, wanPeer, Direction.Up, 800));
        h.Aggregator.Flush(70_000);

        h.Sink.AllSamples.Should().HaveCount(2);
        h.Sink.AllSamples.Single(s => s.RemoteClass == RemoteClass.Local).BytesUp.Should().Be(200);
        h.Sink.AllSamples.Single(s => s.RemoteClass == RemoteClass.Wan).BytesUp.Should().Be(800);
    }

    [Fact]
    public void TakeActivitySnapshot_BeforeFirstFlush_ReturnsEmptyWindow()
    {
        var h = new Harness(initialNowUnixMs: 0);
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 0));
        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);

        h.Aggregator.Observe(Obs(1_000, 100, local, remote, Direction.Up, 5_000));
        h.FakeNowUnixMs = 2_000;

        var snap = h.Aggregator.TakeActivitySnapshot();

        snap.WindowSeconds.Should().Be(0.0);
        snap.Apps.Should().BeEmpty();
    }

    [Fact]
    public void TakeActivitySnapshot_AfterFlush_AggregatesByAppAndComputesRates()
    {
        // Bucket span: [0, 5_000]. Two PIDs of the same app contribute; one
        // svchost PID hosts Dnscache. Snapshot at t=8_000 (5s bucket + 3s partial).
        var h = new Harness(initialNowUnixMs: 0);

        var chromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        h.Resolver.Set(new ProcessImageInfo(101, chromePath, "chrome.exe", 50));
        h.Resolver.Set(new ProcessImageInfo(102, chromePath, "chrome.exe", 60));
        h.Resolver.Set(new ProcessImageInfo(200,
            @"C:\Windows\System32\svchost.exe", "svchost.exe", 70));

        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);

        // First bucket: t=1000 → flush at t=5000. Chrome PID 101 + 102, svchost PID 200.
        h.Aggregator.Observe(Obs(1_000, 101, local, remote, Direction.Up,   500));
        h.Aggregator.Observe(Obs(1_500, 102, local, remote, Direction.Up, 1_500));
        h.Aggregator.Observe(Obs(2_000, 200, local, remote, Direction.Down,  250));

        h.FakeNowUnixMs = 5_000;
        h.Aggregator.Flush(5_000);

        // Partial accumulator: t=5500–7500. Only chrome PID 101.
        h.Aggregator.Observe(Obs(5_500, 101, local, remote, Direction.Down, 7_000));
        h.Aggregator.Observe(Obs(7_500, 101, local, remote, Direction.Up,   1_000));

        h.FakeNowUnixMs = 8_000;
        var snap = h.Aggregator.TakeActivitySnapshot();

        snap.WindowSeconds.Should().Be(8.0);
        snap.Apps.Should().HaveCount(2);

        var chrome = snap.Apps.Single(a => a.ImageName == "chrome.exe");
        chrome.BytesUpTotal.Should().Be(3_000);    // 500 + 1500 + 1000
        chrome.BytesDownTotal.Should().Be(7_000);
        chrome.BytesUpPerSec.Should().Be(375.0);   // 3000 / 8
        chrome.BytesDownPerSec.Should().Be(875.0); // 7000 / 8

        var svchost = snap.Apps.Single(a => a.ImageName == "svchost.exe");
        svchost.BytesUpTotal.Should().Be(0);
        svchost.BytesDownTotal.Should().Be(250);
    }

    [Fact]
    public void TakeActivitySnapshot_PreservesAcrossConsecutiveFlushes()
    {
        // After two flushes, only the SECOND bucket contributes to the window
        // (sliding semantics). Verifies the aggregator and window agree.
        var h = new Harness(initialNowUnixMs: 0);
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 0));

        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);

        h.Aggregator.Observe(Obs(1_000, 100, local, remote, Direction.Up, 99_999));
        h.FakeNowUnixMs = 5_000;
        h.Aggregator.Flush(5_000);

        h.Aggregator.Observe(Obs(7_000, 100, local, remote, Direction.Up, 50));
        h.FakeNowUnixMs = 10_000;
        h.Aggregator.Flush(10_000);

        h.FakeNowUnixMs = 10_000;
        var snap = h.Aggregator.TakeActivitySnapshot();

        snap.WindowSeconds.Should().Be(5.0);
        snap.Apps.Single().BytesUpTotal.Should().Be(50);
    }

    [Fact]
    public void Observe_UnattributablePid_Dropped()
    {
        var h = new Harness();

        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 51234);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);

        h.Aggregator.Observe(Obs(1000, pid: null, local, remote, Direction.Up, 500));
        h.Aggregator.Flush(2000);

        h.Aggregator.ObservationsUnattributed.Should().Be(1);
        h.Sink.AllSamples.Should().BeEmpty();
        h.Sink.AllNewSessions.Should().BeEmpty();
    }
}
