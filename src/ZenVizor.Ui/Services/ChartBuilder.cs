using System.Runtime.Versioning;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Picks chart style + subtitle text from the resolved grain. Centralizes the
/// "how do I draw this tier" decision so AppDetail and History stay in sync.
/// </summary>
/// <remarks>
/// Style mapping (per the UX call):
/// <list type="bullet">
///   <item><b>Samples</b> (≤24h windows): two free-floating <c>LineSeries</c>
///   so spikes are readable point-by-point.</item>
///   <item><b>Hourly</b> / <b>Daily</b> (>24h): <c>StackedColumnSeries</c> so
///   each discrete bucket reads as a single bar with Up + Down stacked, ideal
///   for comparing periods ("higher on Tuesday").</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ChartBuilder
{
    public static ISeries[] BuildSeries(
        TrafficGrain grainUsed,
        IReadOnlyCollection<DateTimePoint> upPoints,
        IReadOnlyCollection<DateTimePoint> downPoints)
    {
        return grainUsed switch
        {
            TrafficGrain.Samples => new ISeries[]
            {
                new LineSeries<DateTimePoint>
                {
                    Name = "Up",
                    Values = upPoints,
                    GeometrySize = 0,
                    LineSmoothness = 0,
                },
                new LineSeries<DateTimePoint>
                {
                    Name = "Down",
                    Values = downPoints,
                    GeometrySize = 0,
                    LineSmoothness = 0,
                },
            },
            // Hourly + Daily: stacked columns. Both series share the default
            // stack group, so Up bars sit at the bottom and Down stacks on top
            // — total bar height = Up + Down for the bucket.
            _ => new ISeries[]
            {
                new StackedColumnSeries<DateTimePoint>
                {
                    Name = "Up",
                    Values = upPoints,
                },
                new StackedColumnSeries<DateTimePoint>
                {
                    Name = "Down",
                    Values = downPoints,
                },
            },
        };
    }

    public static string DescribeView(TrafficGrain grainUsed, WindowPreset? preset)
    {
        var grainText = grainUsed switch
        {
            TrafficGrain.Samples => "per-minute detail",
            TrafficGrain.Hourly  => "hourly totals",
            TrafficGrain.Daily   => "daily totals",
            _                    => "data",
        };

        var windowText = preset?.Label switch
        {
            "Last 1 hour"   => "the last hour",
            "Last 24 hours" => "the last 24 hours",
            "Last 7 days"   => "the last 7 days",
            "Last 30 days"  => "the last 30 days",
            "Last 90 days"  => "the last 90 days",
            _               => "the selected window",
        };

        return $"Showing {grainText} over {windowText}.";
    }
}
