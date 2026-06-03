using System.Runtime.Versioning;
using LiveChartsCore.Defaults;
using ZenVizor.Core.Aggregation;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Thin facade over <see cref="ZenVizor.Core.Aggregation.SeriesDownsampler"/>
/// that handles the <see cref="DateTimePoint"/> conversion at the chart edge.
/// The actual math (bucket summing, paired-factor alignment) lives in Core so
/// it can be unit-tested without a LiveCharts2 dependency.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ChartSeriesDownsampler
{
    public const int MaxBuckets = Core.Aggregation.SeriesDownsampler.DefaultMaxBuckets;

    public static (List<DateTimePoint> Up, List<DateTimePoint> Down) Downsample(
        List<DateTimePoint> up, List<DateTimePoint> down)
    {
        if (Math.Max(up.Count, down.Count) <= MaxBuckets) return (up, down);

        var upBuckets = ToSeriesBuckets(up);
        var downBuckets = ToSeriesBuckets(down);
        var (dsUp, dsDown) = Core.Aggregation.SeriesDownsampler.DownsamplePair(
            upBuckets, downBuckets, MaxBuckets);

        return (ToDateTimePoints(dsUp), ToDateTimePoints(dsDown));
    }

    private static SeriesBucket[] ToSeriesBuckets(List<DateTimePoint> points)
    {
        var result = new SeriesBucket[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var dt = p.DateTime;
            var ms = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt)).ToUnixTimeMilliseconds();
            result[i] = new SeriesBucket(ms, (long)(p.Value ?? 0));
        }
        return result;
    }

    private static List<DateTimePoint> ToDateTimePoints(IReadOnlyList<SeriesBucket> buckets)
    {
        var result = new List<DateTimePoint>(buckets.Count);
        for (int i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            result.Add(new DateTimePoint(
                DateTimeOffset.FromUnixTimeMilliseconds(b.TimestampUnixMs).LocalDateTime,
                b.Value));
        }
        return result;
    }
}
