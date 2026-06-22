// SPDX-License-Identifier: GPL-3.0-or-later

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Capture;
using ZenVizor.Core.Aggregation;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Storage;
using ZenVizor.Service;

namespace ZenVizor.Integration.Tests;

/// <summary>
/// Bug 1 regression gate: when the capture source dies (ETW Process loop
/// threw, kernel logger evicted the session, etc.) the monitor must report
/// CaptureActive=false instead of leaving <c>IsRunning</c> stuck at <c>true</c>.
/// Drives the production <see cref="CaptureMonitor"/> with the synthetic
/// <see cref="SyntheticCaptureSource"/> exactly as the headless-first testing
/// rule requires.
/// </summary>
public sealed class CaptureMonitorHealthTests
{
    /// <summary>
    /// Inert sink — the health tests are about lifecycle, not flush behavior.
    /// We just need the aggregator's Flush to not throw.
    /// </summary>
    private sealed class NullFlushSink : IFlushSink
    {
        public FlushBatchResult Flush(FlushBatch batch) => new(
            NewPidToSessionId:   new Dictionary<int, int>(),
            NewSessionIdToAppId: new Dictionary<int, int>(),
            SampleRowsWritten: 0,
            ConnectionUpserts: 0,
            SessionsClosed: 0);
    }

    private static (CaptureMonitor Monitor, SyntheticCaptureSource Source) BuildMonitor()
    {
        var source = new SyntheticCaptureSource();
        var tracker = new SessionTracker(new InMemoryProcessImageResolver());
        var aggregator = new TrafficAggregator(
            tracker,
            new PidCorrector(),
            new InMemoryPidTableSource(),
            new NullFlushSink());

        // Long enough that the flush ticker won't fire during the test —
        // we drive lifecycle, not flush cadence.
        var monitor = new CaptureMonitor(
            source,
            aggregator,
            flushInterval: TimeSpan.FromMinutes(5),
            logger: NullLogger<CaptureMonitor>.Instance);

        return (monitor, source);
    }

    [Fact]
    public async Task IsRunning_BeforeStart_IsFalse()
    {
        var (monitor, _) = BuildMonitor();
        monitor.IsRunning.Should().BeFalse();
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task IsRunning_AfterStart_IsTrue()
    {
        var (monitor, _) = BuildMonitor();
        await monitor.StartAsync(CancellationToken.None);

        monitor.IsRunning.Should().BeTrue();
        await monitor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task IsRunning_AfterSourceFaults_FlipsToFalse()
    {
        // The bug: ProcessLoop catches, completes the channel, the reader loop
        // returns — and IsRunning stays true forever. This test pins the fix.
        var (monitor, source) = BuildMonitor();
        await monitor.StartAsync(CancellationToken.None);
        monitor.IsRunning.Should().BeTrue();

        source.MarkFaulted();

        // The reader loop sees the channel complete and exits naturally.
        // IsRunning's derivation should observe this and flip to false even
        // though StopAsync was never called.
        await WaitUntilFalseAsync(() => monitor.IsRunning, TimeSpan.FromSeconds(2));
        monitor.IsRunning.Should().BeFalse();

        await monitor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task IsRunning_AfterStopAsync_IsFalse()
    {
        var (monitor, _) = BuildMonitor();
        await monitor.StartAsync(CancellationToken.None);
        await monitor.StopAsync(CancellationToken.None);

        monitor.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task StopAsync_DrainsQueuedObservationsBeforeFinalFlush()
    {
        // Bug 3 gate: pre-fix, StopAsync cancelled the reader loop first, so
        // observations buffered in the channel never made it to the aggregator
        // before the final flush. Now: source disposes first → channel drains
        // → reader hands every observation off → final flush sees them.
        var source = new SyntheticCaptureSource();

        var resolver = new InMemoryProcessImageResolver();
        resolver.Set(new ProcessImageInfo(100, @"C:\a.exe", "a.exe", 0));
        var tracker = new SessionTracker(resolver);

        var observationsSeen = 0;
        var countingSink = new CountingFlushSink(b => observationsSeen += b.Samples.Count);

        var aggregator = new TrafficAggregator(
            tracker,
            new PidCorrector(),
            new InMemoryPidTableSource(),
            countingSink);

        var monitor = new CaptureMonitor(
            source,
            aggregator,
            flushInterval: TimeSpan.FromMinutes(5), // ensure no tick during the test
            logger: NullLogger<CaptureMonitor>.Instance);

        await monitor.StartAsync(CancellationToken.None);

        // Buffer observations in the synthetic channel. The reader picks
        // them up during the orderly shutdown drain.
        var local = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("10.0.0.5"), 1234);
        var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("8.8.8.8"), 443);

        for (var i = 0; i < 5; i++)
        {
            source.TryEmit(new ZenVizor.Core.Observations.NetworkObservation(
                TimestampUnixMs: 1_000 + i,
                Pid: 100,
                Protocol: ZenVizor.Core.Observations.Protocol.Tcp,
                LocalEndpoint: local,
                RemoteEndpoint: remote,
                Direction: ZenVizor.Core.Observations.Direction.Up,
                Bytes: 1_000));
        }

        await monitor.StopAsync(CancellationToken.None);

        // StopAsync's final flush should have written exactly one sample row
        // (5 observations collapse into one 60s-bucket sample). The previous
        // bug would have dropped all 5 observations on reader cancellation —
        // the sink would see ZERO sample rows from a non-empty queue.
        countingSink.BatchCount.Should().BeGreaterThan(0);
        observationsSeen.Should().Be(1,
            "the buffered observations must reach the aggregator and collapse " +
            "into one bucket sample, not be dropped by reader-loop cancellation");
    }

    private sealed class CountingFlushSink : IFlushSink
    {
        private readonly Action<FlushBatch> _onFlush;
        public int BatchCount { get; private set; }

        public CountingFlushSink(Action<FlushBatch> onFlush) => _onFlush = onFlush;

        public FlushBatchResult Flush(FlushBatch batch)
        {
            BatchCount++;
            _onFlush(batch);
            return new FlushBatchResult(
                NewPidToSessionId:   new Dictionary<int, int>(),
                NewSessionIdToAppId: new Dictionary<int, int>(),
                SampleRowsWritten: batch.Samples.Count,
                ConnectionUpserts: batch.Connections.Count,
                SessionsClosed: batch.ClosedSessionIds.Count);
        }
    }

    private static async Task WaitUntilFalseAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!condition()) return;
            await Task.Delay(20);
        }
    }
}
