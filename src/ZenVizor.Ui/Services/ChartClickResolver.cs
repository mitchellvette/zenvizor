// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.WPF;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Epic A (1.1.0) Phase 2 — resolves a chart pixel click into the
/// (popover window, visual anchor bucket, rendered bucket span, grain)
/// tuple consumed by the History page's click-to-attribute popover.
///
/// See <c>docs/roadmap/epic-a-history-click-to-attribute.md</c> §Phase 2
/// for the design and <c>docs/epic-a-phase-2-gate-0.md</c> for the
/// LiveCharts2 API surface this depends on (pixel→data confirmed on
/// LiveChartsCore.SkiaSharpView.WPF 2.0.4).
///
/// Split into:
///   * <see cref="TryResolveClick"/> — WPF/LiveCharts-coupled wrapper.
///     Reads <c>ScalePixelsToData</c>, walks <c>Series[0].Values</c>,
///     reads the X axis <c>UnitWidth</c>. Not headlessly testable.
///   * Pure-math helpers — <see cref="TryFindContainingBucketIndex"/>,
///     <see cref="ComputePopoverWindow"/>. Primitive inputs, deterministic;
///     tested from <c>ZenVizor.Integration.Tests</c> via the existing
///     <c>InternalsVisibleTo</c> grant.
/// </summary>
internal static class ChartClickResolver
{
    /// <summary>
    /// Minimum useful attribution window — 6 minutes. On the 1h preset
    /// (Samples grain, 1-minute rendered buckets) a single-minute popover
    /// is too narrow to reliably represent who's contributing to a clicked
    /// spike: a 30-second quiet moment in a noisy 5-minute talker's run
    /// would attribute to the wrong app. Widening to 6 minutes spans 6
    /// rendered buckets centered on the click, matching the natural
    /// granularity of the 24h preset (also 6-min rendered buckets). Larger
    /// presets (7d Hourly = 2 hr, 90d Daily = 2 day) already exceed this
    /// floor and pass through unchanged.
    /// </summary>
    internal const long MinPopoverWindowMs = 6L * 60 * 1000;

    /// <summary>
    /// Resolve a click pixel on a HistoryPage-style CartesianChart into a
    /// popover query window + visual anchor bucket. Returns <c>false</c> if
    /// the click misses every rendered bucket (axis-label band, inter-bar
    /// gap, legend strip — per Gate 0 edge probes) or if the chart has no
    /// axis / no series data set.
    /// </summary>
    internal static bool TryResolveClick(
        Point clickPx,
        CartesianChart chart,
        QueryWindow visibleChartWindow,
        TrafficGrain grain,
        out ResolvedClick? result)
    {
        result = null;

        if (!TryReadUnitWidthTicks(chart, out var unitWidthTicks)) return false;

        var bucketStartTicks = ExtractBucketStartTicks(chart);
        if (bucketStartTicks.Count == 0) return false;

        var data = chart.ScalePixelsToData(new LvcPointD(clickPx.X, clickPx.Y));
        var clickTicks = (long)data.X;

        if (!TryFindContainingBucketIndex(bucketStartTicks, unitWidthTicks, clickTicks, out var idx))
            return false;

        var visualBucketStartTicks = bucketStartTicks[idx];
        var visualBucketStartUnixMs = LocalTicksToUnixMs(visualBucketStartTicks);
        var visualBucketSpanMs = unitWidthTicks / TimeSpan.TicksPerMillisecond;

        var popoverWindow = ComputePopoverWindow(
            visualBucketStartUnixMs, visualBucketSpanMs, visibleChartWindow);

        result = new ResolvedClick(
            PopoverWindow: popoverWindow,
            VisualBucketStartTicks: visualBucketStartTicks,
            VisualBucketSpanTicks: unitWidthTicks,
            Grain: grain);
        return true;
    }

    /// <summary>
    /// Pure-math: locate the rendered bucket whose extent
    /// <c>[bucketStart, bucketStart + UnitWidth)</c> contains <c>clickTicks</c>.
    /// Returns <c>false</c> for clicks before the first bucket, after the
    /// last bucket, or inside an inter-bucket gap (if the chart series has
    /// sparse data).
    /// </summary>
    /// <remarks>
    /// Extent-containment over nearest-by-distance because nearest-by-start
    /// fails for clicks past a bucket's midpoint: clicking at
    /// <c>bucketStart_K + 0.6·UnitWidth</c> (firmly inside bucket K) is
    /// distance 0.4·UnitWidth from K+1's start and 0.6·UnitWidth from K's
    /// start — nearest-by-start would mis-attribute to K+1. Extent
    /// containment is unambiguous.
    /// </remarks>
    internal static bool TryFindContainingBucketIndex(
        IReadOnlyList<long> sortedBucketStartTicks,
        long unitWidthTicks,
        long clickTicks,
        out int index)
    {
        index = -1;
        if (sortedBucketStartTicks.Count == 0 || unitWidthTicks <= 0) return false;

        // Binary search: find the last bucketStart <= clickTicks.
        int lo = 0;
        int hi = sortedBucketStartTicks.Count - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2; // upper-mid bias for last-le search
            if (sortedBucketStartTicks[mid] <= clickTicks) lo = mid;
            else hi = mid - 1;
        }

        if (sortedBucketStartTicks[lo] > clickTicks) return false; // click is before first bucket

        var bucketStart = sortedBucketStartTicks[lo];
        if (clickTicks < bucketStart + unitWidthTicks)
        {
            index = lo;
            return true;
        }

        // Click is past this bucket's extent (gap or past last bucket).
        return false;
    }

    /// <summary>
    /// Pure-math: compute the popover query window from a visual anchor
    /// bucket. Applies the <see cref="MinPopoverWindowMs"/> floor (widens
    /// narrow rendered buckets), centers on the bucket's midpoint, and
    /// clamps to the visible chart window so the popover discloses a slice
    /// of what the user can actually see.
    /// </summary>
    internal static QueryWindow ComputePopoverWindow(
        long visualBucketStartUnixMs,
        long visualBucketSpanMs,
        QueryWindow visibleChartWindow)
    {
        var spanMs = Math.Max(visualBucketSpanMs, MinPopoverWindowMs);
        var halfSpanMs = spanMs / 2;
        var centerMs = visualBucketStartUnixMs + visualBucketSpanMs / 2;
        var rawStartMs = centerMs - halfSpanMs;
        var rawEndMs = centerMs + halfSpanMs;
        var clampedStartMs = Math.Max(rawStartMs, visibleChartWindow.FromUnixMs);
        var clampedEndMs = Math.Min(rawEndMs, visibleChartWindow.ToUnixMs);
        return new QueryWindow(clampedStartMs, clampedEndMs);
    }

    // -- WPF / LiveCharts coupling helpers (not unit-tested) ---------------

    private static bool TryReadUnitWidthTicks(CartesianChart chart, out long unitWidthTicks)
    {
        unitWidthTicks = 0;
        if (chart.XAxes is not { } xs) return false;
        var first = xs.OfType<Axis>().FirstOrDefault();
        if (first is null) return false;
        var width = first.UnitWidth;
        if (width <= 0) return false;
        unitWidthTicks = (long)width;
        return true;
    }

    private static IReadOnlyList<long> ExtractBucketStartTicks(CartesianChart chart)
    {
        if (chart.Series is not { } seriesEnum) return Array.Empty<long>();
        var firstSeries = seriesEnum.FirstOrDefault();
        if (firstSeries?.Values is not { } valuesEnum) return Array.Empty<long>();

        var ticks = new List<long>();
        foreach (var v in valuesEnum)
        {
            if (v is DateTimePoint dp) ticks.Add(dp.DateTime.Ticks);
        }
        return ticks;
    }

    private static long LocalTicksToUnixMs(long localTicks)
    {
        var dt = new DateTime(localTicks, DateTimeKind.Unspecified);
        var offset = TimeZoneInfo.Local.GetUtcOffset(dt);
        return new DateTimeOffset(dt, offset).ToUnixTimeMilliseconds();
    }
}

/// <summary>
/// Phase 2 click resolution result. Consumed by HistoryPage's click handler
/// (calls <see cref="ZenVizor.Ipc.Contracts.IZenVizorIpc.GetAppListAsync"/>
/// with <see cref="PopoverWindow"/> + uses <see cref="VisualBucketStartTicks"/>
/// for popover anchor positioning).
/// </summary>
internal sealed record ResolvedClick(
    QueryWindow PopoverWindow,
    long VisualBucketStartTicks,
    long VisualBucketSpanTicks,
    TrafficGrain Grain)
{
    /// <summary>
    /// Convert raw bytes (per-app totals over the popover window) to a rate
    /// in the chart's per-grain unit (<c>/min</c> for Samples, <c>/hr</c>
    /// for Hourly, <c>/day</c> for Daily). The sum of per-app rates from
    /// this method reconciles to the mean of the chart's plotted values
    /// over the popover window — the reconciliation invariant guarded by
    /// <c>ChartClickResolverTests.BytesPerGrainUnit_*</c>.
    ///
    /// Critical for the 1h preset where <see cref="MinPopoverWindowMs"/>
    /// widens the popover from 1 min (single chart bucket) to 6 min (six
    /// chart buckets). The divisor is the count of per-grain units IN the
    /// popover window (not the chart's bucket span), so the rate reflects
    /// the wider attribution context the popover actually queries.
    /// </summary>
    internal long BytesPerGrainUnit(long bytes)
    {
        var unitMs = Grain switch
        {
            TrafficGrain.Hourly => 3_600_000L,
            TrafficGrain.Daily => 86_400_000L,
            _ => 60_000L,
        };
        var divisor = PopoverWindow.SpanMs / (double)unitMs;
        return divisor <= 0 ? 0 : (long)(bytes / divisor);
    }
}
