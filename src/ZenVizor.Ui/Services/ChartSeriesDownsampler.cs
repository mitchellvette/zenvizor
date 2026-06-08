using System.Runtime.Versioning;
using LiveChartsCore.Defaults;
using ZenVizor.Core.Aggregation;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Thin facade over <see cref="ZenVizor.Core.Aggregation.SeriesDownsampler"/>
/// that handles the <see cref="DateTimePoint"/> conversion at the chart edge.
/// The actual sum-based math (bucket summing, paired-factor alignment) lives
/// in Core so it can be unit-tested without a LiveCharts2 dependency.
///
/// <para>This facade ALSO carries chart-edge-only <i>average-based</i>
/// reducers (<see cref="DownsampleAverage"/> and <see cref="Coalesce"/>) for
/// presentations where the Y axis represents a <i>rate</i> (bytes per the
/// grain's time unit) rather than a per-bucket sum. The average-based
/// reducers do NOT round-trip through Core because Core's contract is
/// sum-preservation — switching that contract would break every other
/// consumer (e.g. History's current "/bucket" Y label).</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ChartSeriesDownsampler
{
    public const int MaxBuckets = Core.Aggregation.SeriesDownsampler.DefaultMaxBuckets;

    /// <summary>
    /// Sum-preserving downsample (delegates to Core). Use for charts whose Y
    /// axis is labeled "/bucket" — i.e. each plotted value represents the
    /// total over the (possibly coalesced) bucket.
    /// </summary>
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

    /// <summary>
    /// Average-based downsample for rate-labeled charts. When either series
    /// exceeds <see cref="MaxBuckets"/>, both are coalesced with one shared
    /// factor (preserving timestamp alignment) and each group's value is
    /// reduced to the <i>average</i> of its members — so the result
    /// represents an average rate per original bucket, not a per-group sum.
    /// Use for charts whose Y axis is labeled "/min", "/hr", "/day", etc.
    /// </summary>
    public static (List<DateTimePoint> Up, List<DateTimePoint> Down) DownsampleAverage(
        List<DateTimePoint> up, List<DateTimePoint> down)
    {
        var maxCount = Math.Max(up.Count, down.Count);
        if (maxCount <= MaxBuckets) return (up, down);

        var factor = (int)Math.Ceiling((double)maxCount / MaxBuckets);
        return (CoalesceAverage(up, factor), CoalesceAverage(down, factor));
    }

    /// <summary>
    /// Fixed-factor average coalesce — groups every <paramref name="factor"/>
    /// adjacent points into one bucket whose Value is the average of its
    /// members and whose timestamp is the first member's timestamp. Use for
    /// visual-density control on rate-labeled charts (e.g. 168 hourly buckets
    /// over 7 days coalesced 2× → 84 two-hour buckets, each carrying the
    /// average per-hour rate over its 2-hour span).
    /// </summary>
    public static (List<DateTimePoint> Up, List<DateTimePoint> Down) Coalesce(
        List<DateTimePoint> up, List<DateTimePoint> down, int factor)
    {
        if (factor <= 1) return (up, down);
        return (CoalesceAverage(up, factor), CoalesceAverage(down, factor));
    }

    private static List<DateTimePoint> CoalesceAverage(List<DateTimePoint> points, int factor)
    {
        if (factor <= 1 || points.Count <= 1) return points;

        var result = new List<DateTimePoint>((points.Count / factor) + 1);
        for (int i = 0; i < points.Count; i += factor)
        {
            var end = Math.Min(i + factor, points.Count);
            double sum = 0;
            for (int j = i; j < end; j++)
            {
                sum += points[j].Value ?? 0.0;
            }
            // Average across the group — the resulting value is a rate per
            // original bucket (e.g. avg bytes/hr across this 2-hour group),
            // so the Y axis "/hr" / "/day" suffix stays honest after the
            // coalesce.
            var avg = sum / (end - i);
            result.Add(new DateTimePoint(points[i].DateTime, avg));
        }
        return result;
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
