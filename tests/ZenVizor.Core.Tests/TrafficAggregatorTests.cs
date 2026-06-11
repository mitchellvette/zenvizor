using System.Net;
using FluentAssertions;
using ZenVizor.Core.Aggregation;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Observations;
using ZenVizor.Core.Storage;
using ZenVizor.Core.Tests.Fakes;
using ZenVizor.Ipc.Contracts.Dto;

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
    public void TakeActivitySnapshot_SplitsBytesByRemoteClass()
    {
        // End-to-end: observations against WAN and LAN peers contribute to the
        // ClassBreakdown on the snapshot. Bucket bytes + partial bytes both
        // flow through, and the breakdown sum matches the per-app totals.
        var h = new Harness(initialNowUnixMs: 0);
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 0));

        var local   = new IPEndPoint(IPAddress.Parse("10.0.0.5"),  12345);
        var lanPeer = new IPEndPoint(IPAddress.Parse("10.0.0.10"), 445);
        var wanPeer = new IPEndPoint(IPAddress.Parse("8.8.8.8"),   443);

        // Bucket [0, 5_000]: 800 up to WAN, 200 down from LAN.
        h.Aggregator.Observe(Obs(1_000, 100, local, wanPeer, Direction.Up,   800));
        h.Aggregator.Observe(Obs(2_000, 100, local, lanPeer, Direction.Down, 200));
        h.FakeNowUnixMs = 5_000;
        h.Aggregator.Flush(5_000);

        // Partial [5_000, 7_000]: 100 down from WAN, 50 up to LAN.
        h.Aggregator.Observe(Obs(6_000, 100, local, wanPeer, Direction.Down, 100));
        h.Aggregator.Observe(Obs(6_500, 100, local, lanPeer, Direction.Up,    50));

        h.FakeNowUnixMs = 7_000;
        var snap = h.Aggregator.TakeActivitySnapshot();

        snap.WanLocalBreakdown.WanBytesUp.Should().Be(800);
        snap.WanLocalBreakdown.WanBytesDown.Should().Be(100);
        snap.WanLocalBreakdown.LocalBytesUp.Should().Be(50);
        snap.WanLocalBreakdown.LocalBytesDown.Should().Be(200);

        // Sum across the breakdown equals the per-app totals — the rollup
        // doesn't lose or double-count bytes.
        var app = snap.Apps.Single();
        var totalUp = snap.WanLocalBreakdown.WanBytesUp + snap.WanLocalBreakdown.LocalBytesUp;
        var totalDown = snap.WanLocalBreakdown.WanBytesDown + snap.WanLocalBreakdown.LocalBytesDown;
        totalUp.Should().Be(app.BytesUpTotal);
        totalDown.Should().Be(app.BytesDownTotal);
    }

    [Fact]
    public void TakeActivitySnapshot_BeforeFirstFlush_BreakdownIsEmpty()
    {
        var h = new Harness(initialNowUnixMs: 0);
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 0));

        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 12345);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"),  443);
        h.Aggregator.Observe(Obs(1_000, 100, local, remote, Direction.Up, 5_000));

        h.FakeNowUnixMs = 2_000;
        var snap = h.Aggregator.TakeActivitySnapshot();

        // Cold-start: even though the partial accumulator has bytes, the
        // ClassBreakdown stays empty (matches Apps.Should().BeEmpty()).
        snap.WanLocalBreakdown.Should().Be(ClassBreakdown.Empty);
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

    // ---- Bug 2: sink-failure retry semantics ----
    //
    // Pre-fix, TrafficAggregator.Flush swapped _samples/_connections to fresh
    // dictionaries BEFORE calling _sink.Flush. On a sink exception, the
    // swapped accumulators were dropped on the floor — only SessionTracker
    // state survived. These tests pin the fix: snapshot is merged back into
    // the live accumulators on failure, so the next tick retries with the
    // data intact. Per the determinism rule we assert EXACT rows, not
    // approximate.

    private sealed class FailingFlushSink : IFlushSink
    {
        private readonly FakeFlushSink _underlying = new();
        private int _failuresRemaining;
        public Exception FailureException { get; set; } = new InvalidOperationException("sink boom");
        public int FailedCallCount { get; private set; }

        public FailingFlushSink(int failuresRemaining) => _failuresRemaining = failuresRemaining;

        public IReadOnlyCollection<PendingTrafficSample> AllSamples => _underlying.AllSamples;
        public IReadOnlyCollection<PendingConnection> AllConnections => _underlying.AllConnections;
        public IReadOnlyList<FlushBatch> Batches => _underlying.Batches;

        public FlushBatchResult Flush(FlushBatch batch)
        {
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                FailedCallCount++;
                throw FailureException;
            }
            return _underlying.Flush(batch);
        }
    }

    private sealed class FailingHarness
    {
        public InMemoryProcessImageResolver Resolver { get; } = new();
        public InMemoryPidTableSource SnapshotSource { get; } = new();
        public FailingFlushSink Sink { get; }
        public SessionTracker Tracker { get; }
        public TrafficAggregator Aggregator { get; }
        public long FakeNowUnixMs { get; set; }

        public FailingHarness(int failuresBeforeSuccess)
        {
            Sink = new FailingFlushSink(failuresBeforeSuccess);
            Tracker = new SessionTracker(Resolver);
            Aggregator = new TrafficAggregator(
                Tracker, new PidCorrector(), SnapshotSource, Sink,
                nowProvider: () => FakeNowUnixMs);
        }
    }

    [Fact]
    public void FailingSink_RetryAfterFailure_PreservesExactSampleBytes()
    {
        var h = new FailingHarness(failuresBeforeSuccess: 1);
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 200));

        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 51234);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);

        h.Aggregator.Observe(Obs(60_500, 100, local, remote, Direction.Up,   700));
        h.Aggregator.Observe(Obs(61_000, 100, local, remote, Direction.Down, 1_300));

        // First flush throws; sink raised but data must remain pending.
        FluentActions.Invoking(() => h.Aggregator.Flush(70_000))
            .Should().Throw<InvalidOperationException>().WithMessage("sink boom");
        h.Sink.FailedCallCount.Should().Be(1);
        h.Sink.AllSamples.Should().BeEmpty();

        // Second flush succeeds. The sink should see the SAME bytes — not
        // zero (the bug), and not doubled.
        h.Aggregator.Flush(75_000);

        h.Sink.AllSamples.Should().ContainSingle();
        var sample = h.Sink.AllSamples.Single();
        sample.Pid.Should().Be(100);
        sample.BucketStartUnixMs.Should().Be(60_000);
        sample.BytesUp.Should().Be(700);
        sample.BytesDown.Should().Be(1_300);
    }

    [Fact]
    public void FailingSink_NewObservationsBetweenFailures_AreSummedExactly()
    {
        var h = new FailingHarness(failuresBeforeSuccess: 1);
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 200));

        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 51234);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);

        // Pre-failure observations in bucket [60_000, 120_000).
        h.Aggregator.Observe(Obs(60_500, 100, local, remote, Direction.Up,   100));
        h.Aggregator.Observe(Obs(61_000, 100, local, remote, Direction.Down, 200));

        FluentActions.Invoking(() => h.Aggregator.Flush(70_000))
            .Should().Throw<InvalidOperationException>();

        // More bytes arrive in the SAME bucket between failure and retry.
        // Merge-back semantics must sum them into the surviving snapshot.
        h.Aggregator.Observe(Obs(62_000, 100, local, remote, Direction.Up,   50));
        h.Aggregator.Observe(Obs(63_000, 100, local, remote, Direction.Down, 700));

        // Retry succeeds.
        h.Aggregator.Flush(80_000);

        h.Sink.AllSamples.Should().ContainSingle();
        var sample = h.Sink.AllSamples.Single();
        sample.BucketStartUnixMs.Should().Be(60_000);
        sample.BytesUp.Should().Be(150);    // 100 + 50
        sample.BytesDown.Should().Be(900);  // 200 + 700
    }

    [Fact]
    public void FailingSink_RetryAfterFailure_PreservesConnectionsExactly()
    {
        var h = new FailingHarness(failuresBeforeSuccess: 1);
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 200));

        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 51234);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);

        h.Aggregator.Observe(Obs(60_500, 100, local, remote, Direction.Up,   400));
        h.Aggregator.Observe(Obs(61_500, 100, local, remote, Direction.Down, 1_600));

        FluentActions.Invoking(() => h.Aggregator.Flush(70_000))
            .Should().Throw<InvalidOperationException>();

        // Another byte event on the same connection between failure + retry.
        h.Aggregator.Observe(Obs(65_000, 100, local, remote, Direction.Up,   100));
        h.Aggregator.Flush(80_000);

        var conn = h.Sink.AllConnections.Should().ContainSingle().Subject;
        conn.Pid.Should().Be(100);
        conn.RemoteAddress.Should().Be("8.8.8.8");
        conn.RemotePort.Should().Be(443);
        conn.BytesUpDelta.Should().Be(500);     // 400 + 100
        conn.BytesDownDelta.Should().Be(1_600);
        conn.FirstSeenUnixMs.Should().Be(60_500);
        conn.LastSeenUnixMs.Should().Be(65_000);
    }

    [Fact]
    public void FailingSink_NewSessionRetriedOnce_NotDuplicated()
    {
        // SessionTracker was already retry-safe pre-fix: a failed flush leaves
        // the tracker in its pre-flush state. Belt-and-braces: re-verify that
        // a NewSession entry shows up exactly once after the retry succeeds,
        // not in both the failed batch's NewSessions and the retry's.
        var h = new FailingHarness(failuresBeforeSuccess: 1);
        h.Resolver.Set(new ProcessImageInfo(100, @"C:\a\a.exe", "a.exe", 200));

        var local  = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 51234);
        var remote = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443);
        h.Aggregator.Observe(Obs(60_500, 100, local, remote, Direction.Up, 500));

        FluentActions.Invoking(() => h.Aggregator.Flush(70_000))
            .Should().Throw<InvalidOperationException>();
        h.Aggregator.Flush(75_000);

        // Sink only sees the retried batch (the failed one threw before
        // anything was recorded). NewSessions appears in the surviving batch.
        h.Sink.Batches.Should().HaveCount(1);
        h.Sink.Batches[0].NewSessions.Should().ContainSingle()
            .Which.Pid.Should().Be(100);
    }
}
