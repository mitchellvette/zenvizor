// SPDX-License-Identifier: GPL-3.0-or-later

using FluentAssertions;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Services;

namespace ZenVizor.Integration.Tests;

/// <summary>
/// Pure-math tests for the Phase 2 chart-click resolver. The WPF/LiveCharts
/// coupling in <see cref="ChartClickResolver.TryResolveClick"/> itself
/// requires a live CartesianChart and is verified at the manual gate; the
/// math helpers it delegates to are testable in isolation.
///
/// Coverage:
///   * <c>TryFindContainingBucketIndex</c> — extent-containment over
///     nearest-by-distance, sparse/empty lists, edge ticks.
///   * <c>ComputePopoverWindow</c> — 6-minute floor widening, click-
///     centered anchoring, visible-chart clamping (the "reconciliation
///     guard" of the popover window itself; the rate-reconciliation invariant
///     against actual GetAppListAsync output lives in a separate Phase 2
///     slice).
/// </summary>
public sealed class ChartClickResolverTests
{
    // -- TryFindContainingBucketIndex ----------------------------------------

    private const long Min = TimeSpan.TicksPerMinute;

    [Fact]
    public void TryFindContainingBucketIndex_EmptyList_ReturnsFalse()
    {
        var ok = ChartClickResolver.TryFindContainingBucketIndex(
            Array.Empty<long>(), Min, 12345, out var idx);

        ok.Should().BeFalse();
        idx.Should().Be(-1);
    }

    [Fact]
    public void TryFindContainingBucketIndex_ClickBeforeFirstBucket_ReturnsFalse()
    {
        var buckets = new long[] { 100, 200, 300 }.Select(m => m * Min).ToArray();

        var ok = ChartClickResolver.TryFindContainingBucketIndex(
            buckets, Min, 50 * Min, out var idx);

        ok.Should().BeFalse();
        idx.Should().Be(-1);
    }

    [Fact]
    public void TryFindContainingBucketIndex_ClickPastLastBucketExtent_ReturnsFalse()
    {
        var buckets = new long[] { 100, 200, 300 }.Select(m => m * Min).ToArray();
        // 301 is past bucket 300's extent [300, 301).
        var clickTicks = 302L * Min;

        var ok = ChartClickResolver.TryFindContainingBucketIndex(
            buckets, Min, clickTicks, out var idx);

        ok.Should().BeFalse();
    }

    [Fact]
    public void TryFindContainingBucketIndex_ClickOnBucketStartExactly_ReturnsThatBucket()
    {
        var buckets = new long[] { 100, 200, 300 }.Select(m => m * Min).ToArray();

        ChartClickResolver.TryFindContainingBucketIndex(
            buckets, Min, 200 * Min, out var idx).Should().BeTrue();
        idx.Should().Be(1);
    }

    [Fact]
    public void TryFindContainingBucketIndex_ClickPastMidpointOfBucket_StillReturnsContainingBucket()
    {
        // Bucket K covers [200·Min, 201·Min). Click at 200.6·Min is past
        // the midpoint of K but firmly within K's extent. A naive
        // nearest-by-start algorithm would mis-attribute to bucket K+1
        // (distance 0.4·Min) over K (distance 0.6·Min). Extent containment
        // gets it right.
        var buckets = new long[] { 100, 200, 300 }.Select(m => m * Min).ToArray();
        var clickTicks = 200 * Min + (6 * Min / 10);

        ChartClickResolver.TryFindContainingBucketIndex(
            buckets, Min, clickTicks, out var idx).Should().BeTrue();
        idx.Should().Be(1);
    }

    [Fact]
    public void TryFindContainingBucketIndex_ClickInSparseGap_ReturnsFalse()
    {
        // Buckets 100 and 300 exist; 200 is missing (sparse). Click at 220
        // is more than UnitWidth past bucket 100's extent and before
        // bucket 300's start.
        var buckets = new long[] { 100, 300 }.Select(m => m * Min).ToArray();
        var clickTicks = 220 * Min;

        var ok = ChartClickResolver.TryFindContainingBucketIndex(
            buckets, Min, clickTicks, out var idx);

        ok.Should().BeFalse();
    }

    [Fact]
    public void TryFindContainingBucketIndex_SingleBucketContainsClick_ReturnsZero()
    {
        var buckets = new long[] { 100 * Min };

        ChartClickResolver.TryFindContainingBucketIndex(
            buckets, Min, 100 * Min + Min / 4, out var idx).Should().BeTrue();
        idx.Should().Be(0);
    }

    [Fact]
    public void TryFindContainingBucketIndex_ZeroUnitWidth_ReturnsFalse()
    {
        var buckets = new long[] { 100 * Min };

        var ok = ChartClickResolver.TryFindContainingBucketIndex(
            buckets, 0, 100 * Min, out var idx);

        ok.Should().BeFalse();
    }

    // -- ComputePopoverWindow ------------------------------------------------

    private const long OneMin = 60_000L;
    private const long OneHour = 3_600_000L;
    private const long OneDay = 86_400_000L;

    [Fact]
    public void ComputePopoverWindow_1HourPreset_WidensTo6Minutes_CenteredOnBucketMidpoint()
    {
        // 1h preset: chart bucket span = 1 min. Click on the bucket
        // starting at minute 30 of a fictional "now". Visible chart =
        // [0min, 60min). Popover should be [30 min - 3 min + bucket_midpoint,
        // 30 min + 3 min + bucket_midpoint] = ... click-centered on the
        // bucket midpoint (30·OneMin + 0.5·OneMin = 30.5·OneMin), widened
        // to 6 min: [27.5·OneMin, 33.5·OneMin].
        var visibleChart = new QueryWindow(0, 60 * OneMin);
        var bucketStartMs = 30 * OneMin;
        var bucketSpanMs = OneMin;

        var pop = ChartClickResolver.ComputePopoverWindow(
            bucketStartMs, bucketSpanMs, visibleChart);

        pop.SpanMs.Should().Be(6 * OneMin);
        pop.FromUnixMs.Should().Be(bucketStartMs + bucketSpanMs / 2 - 3 * OneMin);
        pop.ToUnixMs.Should().Be(bucketStartMs + bucketSpanMs / 2 + 3 * OneMin);
    }

    [Fact]
    public void ComputePopoverWindow_24HourPreset_UnchangedAt6Minutes()
    {
        // 24h preset: chart bucket span = 6 min (already at floor).
        // Popover = the bucket itself.
        var visibleChart = new QueryWindow(0, 24 * 60 * OneMin);
        var bucketStartMs = 60 * OneMin; // 1 hour in
        var bucketSpanMs = 6 * OneMin;

        var pop = ChartClickResolver.ComputePopoverWindow(
            bucketStartMs, bucketSpanMs, visibleChart);

        pop.SpanMs.Should().Be(6 * OneMin);
        pop.FromUnixMs.Should().Be(bucketStartMs);
        pop.ToUnixMs.Should().Be(bucketStartMs + bucketSpanMs);
    }

    [Fact]
    public void ComputePopoverWindow_7DayPreset_UnchangedAt2Hours()
    {
        var visibleChart = new QueryWindow(0, 7 * 24 * OneHour);
        var bucketStartMs = 12 * OneHour;
        var bucketSpanMs = 2 * OneHour;

        var pop = ChartClickResolver.ComputePopoverWindow(
            bucketStartMs, bucketSpanMs, visibleChart);

        pop.SpanMs.Should().Be(2 * OneHour);
        pop.FromUnixMs.Should().Be(bucketStartMs);
        pop.ToUnixMs.Should().Be(bucketStartMs + bucketSpanMs);
    }

    [Fact]
    public void ComputePopoverWindow_90DayPreset_UnchangedAt2Days()
    {
        var visibleChart = new QueryWindow(0, 90 * OneDay);
        var bucketStartMs = 10 * OneDay;
        var bucketSpanMs = 2 * OneDay;

        var pop = ChartClickResolver.ComputePopoverWindow(
            bucketStartMs, bucketSpanMs, visibleChart);

        pop.SpanMs.Should().Be(2 * OneDay);
    }

    [Fact]
    public void ComputePopoverWindow_ClickNearLeftEdge_ClampsStartToChartWindow()
    {
        // 1h preset, click on bucket at minute 1 of 60. Widening to 6 min
        // would push popover start to ~minute -1.5. Should clamp to 0.
        var visibleChart = new QueryWindow(0, 60 * OneMin);
        var bucketStartMs = 1 * OneMin;
        var bucketSpanMs = OneMin;

        var pop = ChartClickResolver.ComputePopoverWindow(
            bucketStartMs, bucketSpanMs, visibleChart);

        pop.FromUnixMs.Should().Be(0); // clamped
        pop.ToUnixMs.Should().Be(bucketStartMs + bucketSpanMs / 2 + 3 * OneMin);
    }

    [Fact]
    public void ComputePopoverWindow_ClickNearRightEdge_ClampsEndToChartWindow()
    {
        // 1h preset, click on bucket at minute 58 of 60. Widening would
        // push popover end to ~minute 61.5. Should clamp to 60·OneMin.
        var visibleChart = new QueryWindow(0, 60 * OneMin);
        var bucketStartMs = 58 * OneMin;
        var bucketSpanMs = OneMin;

        var pop = ChartClickResolver.ComputePopoverWindow(
            bucketStartMs, bucketSpanMs, visibleChart);

        pop.FromUnixMs.Should().Be(bucketStartMs + bucketSpanMs / 2 - 3 * OneMin);
        pop.ToUnixMs.Should().Be(60 * OneMin); // clamped
    }

    [Fact]
    public void ComputePopoverWindow_ChartWindowNarrowerThanFloor_ClampsBothSides()
    {
        // Defensive: a hypothetical visible chart narrower than the 6-min
        // floor (not actually reachable today, but the math should still
        // produce a sane non-negative window).
        var visibleChart = new QueryWindow(0, 2 * OneMin);
        var bucketStartMs = 0;
        var bucketSpanMs = OneMin;

        var pop = ChartClickResolver.ComputePopoverWindow(
            bucketStartMs, bucketSpanMs, visibleChart);

        pop.FromUnixMs.Should().Be(0);
        pop.ToUnixMs.Should().Be(2 * OneMin);
        pop.SpanMs.Should().Be(2 * OneMin);
    }

    [Fact]
    public void MinPopoverWindowMs_Is6Minutes()
    {
        // Pin the floor so a "let's make it 10 minutes" drive-by edit
        // trips this test rather than silently shifting attribution
        // semantics across the app.
        ChartClickResolver.MinPopoverWindowMs.Should().Be(6 * OneMin);
    }

    // -- ResolvedClick.BytesPerGrainUnit (rate reconciliation) -------------
    //
    // Guards the divisor math in the popover's per-app rate display: the
    // sum of per-app rates rendered in the popover should reconcile to the
    // mean of the chart's plotted values over the popover window. An
    // accidental N× bug here (e.g. failing to divide by the 6× widening on
    // the 1h preset) would make a quiet talker appear to be a screaming
    // hog. Spec §Phase 2 manual gate names this as a critical guard.

    private static ResolvedClick MakeResolved(TrafficGrain grain, long fromMs, long toMs)
        => new(
            PopoverWindow: new QueryWindow(fromMs, toMs),
            VisualBucketStartTicks: 0,           // unused by BytesPerGrainUnit
            VisualBucketSpanTicks: 0,            // unused by BytesPerGrainUnit
            Grain: grain);

    [Fact]
    public void BytesPerGrainUnit_1HourPreset_WidenedTo6Min_DividesBy6()
    {
        // 1h preset uses Samples grain. Popover window widened to 6 min
        // (the floor). 6,000,000 bytes spread over 6 minutes = 1,000,000 bytes/min.
        var resolved = MakeResolved(TrafficGrain.Samples, 0, 6 * OneMin);

        resolved.BytesPerGrainUnit(6_000_000).Should().Be(1_000_000);
    }

    [Fact]
    public void BytesPerGrainUnit_24HourPreset_6MinBucket_DividesBy6()
    {
        // 24h preset uses Samples grain. Popover window = single 6-min
        // bucket. Same math as 1h widened case — both produce bytes/min.
        var resolved = MakeResolved(TrafficGrain.Samples, 0, 6 * OneMin);

        resolved.BytesPerGrainUnit(6_000_000).Should().Be(1_000_000);
    }

    [Fact]
    public void BytesPerGrainUnit_7DayPreset_2HourBucket_DividesBy2_AsHourly()
    {
        // 7d preset uses Hourly grain. Popover = single 2-hour bucket
        // (2× coalesce). 4 GB over 2 hours = 2 GB/hr.
        var resolved = MakeResolved(TrafficGrain.Hourly, 0, 2 * OneHour);

        resolved.BytesPerGrainUnit(4L * 1024 * 1024 * 1024)
            .Should().Be(2L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void BytesPerGrainUnit_90DayPreset_2DayBucket_DividesBy2_AsDaily()
    {
        // 90d preset uses Daily grain. Popover = single 2-day bucket.
        // 10 GB over 2 days = 5 GB/day.
        var resolved = MakeResolved(TrafficGrain.Daily, 0, 2 * OneDay);

        resolved.BytesPerGrainUnit(10L * 1024 * 1024 * 1024)
            .Should().Be(5L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void BytesPerGrainUnit_SumOfPerAppRates_ReconcilesToChartMean()
    {
        // The actual reconciliation invariant the manual gate visually
        // confirms: given a known total of bytes in a popover window,
        // splitting them across apps and summing the per-grain rates must
        // equal a single-app rate for the full total. (Linearity check —
        // catches accidental per-app scaling.)
        var resolved = MakeResolved(TrafficGrain.Samples, 0, 6 * OneMin);

        var allInOneApp = resolved.BytesPerGrainUnit(6_000_000);
        var splitFour =
              resolved.BytesPerGrainUnit(1_500_000)
            + resolved.BytesPerGrainUnit(1_500_000)
            + resolved.BytesPerGrainUnit(1_500_000)
            + resolved.BytesPerGrainUnit(1_500_000);

        splitFour.Should().Be(allInOneApp);
    }

    [Fact]
    public void BytesPerGrainUnit_ChartMeanReconciliation_OverWidenedWindow()
    {
        // The concrete N× guard the spec warns about: 1h preset widens
        // the popover to 6 min spanning 6 one-minute chart buckets. The
        // chart's plotted values at those 6 buckets are bytes/min. The
        // popover's per-app rate sum must equal the MEAN of those 6
        // chart values (not the sum — that would be the 6× bug).
        //
        // Simulated chart: 6 buckets with values [100, 200, 300, 400, 500, 600]
        // bytes/min. Mean = 350 bytes/min. Total storage bytes over the
        // 6-min window = 100+200+300+400+500+600 = 2100 bytes. One app
        // owns all of it.
        var chartValuesBytesPerMin = new long[] { 100, 200, 300, 400, 500, 600 };
        var meanChartValue = chartValuesBytesPerMin.Sum() / chartValuesBytesPerMin.Length;
        var totalBytesInWindow = chartValuesBytesPerMin.Sum();

        var resolved = MakeResolved(TrafficGrain.Samples, 0, 6 * OneMin);
        var popoverRate = resolved.BytesPerGrainUnit(totalBytesInWindow);

        popoverRate.Should().Be(meanChartValue,
            "popover-displayed rate must equal the MEAN of chart values across the popover window; equaling the SUM would be the 6× widening bug");
    }
}
