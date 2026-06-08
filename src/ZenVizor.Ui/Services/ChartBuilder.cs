using System.Globalization;
using System.Runtime.Versioning;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ui.Views;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Picks chart style + subtitle text from the resolved grain. Centralizes the
/// "how do I draw this tier" decision so AppDetail and History stay in sync.
/// </summary>
/// <remarks>
/// Style mapping (per the UX call):
/// <list type="bullet">
///   <item><b>Samples</b> (≤24h windows): two free-floating <c>LineSeries</c>
///   with forgiving hover hit areas — geometry markers invisible but 20 px wide
///   so X-snap tooltip detection registers anywhere along the line.</item>
///   <item><b>Hourly</b> / <b>Daily</b> (>24h): <c>StackedColumnSeries</c> so
///   each discrete bucket reads as a single bar with Up + Down stacked. Bar
///   width is capped uniformly via <see cref="StackedBarMaxWidth"/> so all bar
///   grains render at the same visual density regardless of bucket count.</item>
/// </list>
///
/// Every Y value the chart plots is a <i>rate</i> (bytes per the grain's time
/// unit), not a sum. The Y axis labeler in <see cref="AppDetailPage"/> appends
/// "/min" / "/hr" / "/day" accordingly, and the per-series tooltip formatters
/// here echo the same unit — this keeps the displayed numbers honest after
/// <see cref="ChartSeriesDownsampler"/> coalesces buckets (averaging, not
/// summing).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ChartBuilder
{
    public static ISeries[] BuildSeries(
        TrafficGrain grainUsed,
        IReadOnlyCollection<DateTimePoint> upPoints,
        IReadOnlyCollection<DateTimePoint> downPoints)
    {
        var unitSuffix = YUnitSuffix(grainUsed);
        string FormatX(ChartPoint pt) => FormatTooltipTime((long)pt.Coordinate.SecondaryValue, grainUsed);
        string FormatY(ChartPoint pt) => PerAppPage.FormatBytes((long)pt.Coordinate.PrimaryValue) + unitSuffix;

        return grainUsed switch
        {
            TrafficGrain.Samples => new ISeries[]
            {
                new LineSeries<DateTimePoint>
                {
                    Name = "Up",
                    Values = upPoints,
                    // GeometrySize=20 with null geometry paints widens the hit
                    // area for X-snap tooltip detection without painting any
                    // visible markers on the line. Mirrors the Dashboard
                    // chart's forgiving-hover convention (DashboardPage.xaml.cs
                    // :96-108); centralized here so AppDetail and (future)
                    // History inherit it without duplicating page-side config.
                    GeometrySize = 20,
                    GeometryFill = null,
                    GeometryStroke = null,
                    LineSmoothness = 0,
                    XToolTipLabelFormatter = FormatX,
                    YToolTipLabelFormatter = FormatY,
                },
                new LineSeries<DateTimePoint>
                {
                    Name = "Down",
                    Values = downPoints,
                    GeometrySize = 20,
                    GeometryFill = null,
                    GeometryStroke = null,
                    LineSmoothness = 0,
                    XToolTipLabelFormatter = FormatX,
                    YToolTipLabelFormatter = FormatY,
                },
            },
            // Hourly + Daily: stacked columns. Both series share the default
            // stack group so Up sits at the bottom of the bar and Down stacks
            // on top — total bar height = Up + Down for the bucket. Bar
            // width is controlled by the X axis's UnitWidth (set per grain
            // via UnitWidthFor) — LC2 v2 needs an explicit UnitWidth on
            // DateTime axes or it computes bar width from the 1-Tick default
            // and bars become sub-pixel. Padding / MaxBarWidth left at LC2
            // defaults so the chart renders predictably; bar appearance can
            // be tuned later if needed.
            _ => new ISeries[]
            {
                new StackedColumnSeries<DateTimePoint>
                {
                    Name = "Up",
                    Values = upPoints,
                    XToolTipLabelFormatter = FormatX,
                    YToolTipLabelFormatter = FormatY,
                },
                new StackedColumnSeries<DateTimePoint>
                {
                    Name = "Down",
                    Values = downPoints,
                    XToolTipLabelFormatter = FormatX,
                    YToolTipLabelFormatter = FormatY,
                },
            },
        };
    }

    /// <summary>
    /// Per-grain / per-window X-axis label-density floor. LiveCharts2 picks
    /// an actual step ≥ MinStep, so these values are the minimum spacing —
    /// chosen to land ~6–8 X labels across each window without crowding.
    /// </summary>
    public static double MinStepFor(TrafficGrain grain, WindowPreset? preset) =>
        (grain, preset?.Label) switch
        {
            (TrafficGrain.Samples, "Last 1 hour")  => TimeSpan.FromMinutes(10).Ticks,
            (TrafficGrain.Samples, _)              => TimeSpan.FromHours(3).Ticks,    // 24h
            (TrafficGrain.Hourly,  _)              => TimeSpan.FromDays(1).Ticks,     // 7d
            (TrafficGrain.Daily,   "Last 30 days") => TimeSpan.FromDays(5).Ticks,
            (TrafficGrain.Daily,   _)              => TimeSpan.FromDays(15).Ticks,    // 90d
            _                                      => TimeSpan.FromHours(1).Ticks,
        };

    /// <summary>
    /// X-axis <c>UnitWidth</c> per grain / window — the natural bar width
    /// in chart units (DateTime.Ticks). LiveCharts2 v2 docs explicitly call
    /// this out as required for DateTime-scale bar series: without it,
    /// <c>UnitWidth</c> defaults to 1 Tick (100 ns) and bars render at
    /// sub-pixel width or fail to render entirely. Values mirror the
    /// post-coalesce bucket cadence in <see cref="AppDetailPage.ApplyDetail"/>
    /// so each bar's chart-unit width matches the data it represents.
    ///
    /// Line series ignore <c>UnitWidth</c>, so setting it for Samples grain
    /// is harmless — keeps the per-grain axis config symmetric.
    /// </summary>
    public static double UnitWidthFor(TrafficGrain grain, WindowPreset? preset) =>
        (grain, preset?.Label) switch
        {
            (TrafficGrain.Samples, "Last 1 hour")  => TimeSpan.FromMinutes(1).Ticks,
            (TrafficGrain.Samples, _)              => TimeSpan.FromMinutes(6).Ticks,  // 24h post-DownsampleAverage
            (TrafficGrain.Hourly,  _)              => TimeSpan.FromHours(2).Ticks,    // 7d post-Coalesce 2×
            (TrafficGrain.Daily,   "Last 30 days") => TimeSpan.FromDays(1).Ticks,
            (TrafficGrain.Daily,   _)              => TimeSpan.FromDays(2).Ticks,     // 90d post-Coalesce 2×
            _                                      => TimeSpan.FromMinutes(1).Ticks,
        };

    /// <summary>
    /// Rate-unit suffix paired with the grain. The Y axis labeler in
    /// <see cref="AppDetailPage"/> appends this; the per-series tooltip
    /// formatter here also appends it so the legend and tooltip stay aligned.
    /// </summary>
    public static string YUnitSuffix(TrafficGrain grain) => grain switch
    {
        TrafficGrain.Hourly => "/hr",
        TrafficGrain.Daily => "/day",
        _ => "/min",  // Samples + unknown
    };

    /// <summary>
    /// X-axis label format paired with the grain (used by
    /// <see cref="AppDetailPage"/>'s axis Labeler closure).
    /// </summary>
    public static string FormatXAxisLabel(long ticks, TrafficGrain grain)
    {
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            return string.Empty;
        var dt = new DateTime(ticks);
        return grain switch
        {
            TrafficGrain.Hourly => dt.ToString("MM-dd HH", CultureInfo.InvariantCulture),
            TrafficGrain.Daily => dt.ToString("MM-dd", CultureInfo.InvariantCulture),
            _ => dt.ToString("HH:mm", CultureInfo.InvariantCulture),
        };
    }

    // Tooltip header format — like the axis label but Hourly carries minutes
    // too (MM-dd HH:mm) so the user can read the exact bucket start time when
    // hovering, even though the axis only renders MM-dd HH for label density.
    private static string FormatTooltipTime(long ticks, TrafficGrain grain)
    {
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            return string.Empty;
        var dt = new DateTime(ticks);
        return grain switch
        {
            TrafficGrain.Hourly => dt.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
            TrafficGrain.Daily => dt.ToString("MM-dd", CultureInfo.InvariantCulture),
            _ => dt.ToString("HH:mm", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Shorthand subtitle in the form "{bucket} · {window}" — no "Showing"
    /// prefix, no trailing period. Bucket descriptors honor the coalesce
    /// policy in <see cref="AppDetailPage"/>: 7d Hourly is coalesced 2× to
    /// 2-hour buckets; 90d Daily is coalesced 2× to 2-day buckets. Keep this
    /// lookup in sync with the coalesce policy.
    /// </summary>
    public static string DescribeView(TrafficGrain grainUsed, WindowPreset? preset)
    {
        var bucket = (grainUsed, preset?.Label) switch
        {
            (TrafficGrain.Samples, _) => "per-minute detail",
            (TrafficGrain.Hourly, "Last 7 days") => "2-hour buckets",
            (TrafficGrain.Hourly, _) => "hourly buckets",
            (TrafficGrain.Daily, "Last 90 days") => "2-day buckets",
            (TrafficGrain.Daily, _) => "daily buckets",
            _ => "data",
        };

        var span = preset?.Label switch
        {
            "Last 1 hour" => "last 1 hour",
            "Last 24 hours" => "last 24 hours",
            "Last 7 days" => "last 7 days",
            "Last 30 days" => "last 30 days",
            "Last 90 days" => "last 90 days",
            _ => "the selected window",
        };

        return $"{bucket} · {span}";
    }
}
