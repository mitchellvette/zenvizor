// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Core.Aggregation;

/// <summary>
/// One bucket from a downsampled time series. <see cref="TimestampUnixMs"/>
/// is the first bucket's start in the group (not the centroid); <see cref="Value"/>
/// is the sum across the group.
/// </summary>
public readonly record struct SeriesBucket(long TimestampUnixMs, long Value);

/// <summary>
/// Caps time-series point counts by summing adjacent buckets. Preserves the
/// total of <c>Value</c> across the series — only sub-bucket time resolution
/// is lost. The same grouping <c>factor</c> is used across paired series (Up /
/// Down) so they stay bucket-aligned after downsampling.
/// </summary>
public static class SeriesDownsampler
{
    /// <summary>
    /// Visual-fidelity ceiling. Above this point count, charts at typical
    /// desktop widths (≤ ~2k px) read no better than the downsampled version
    /// — every extra point becomes a sub-pixel and costs render time for no
    /// visual gain.
    /// </summary>
    public const int DefaultMaxBuckets = 240;

    /// <summary>
    /// Downsample a pair of parallel series with one shared grouping factor.
    /// Pass-through if both series fit under <paramref name="maxBuckets"/>.
    /// </summary>
    public static (IReadOnlyList<SeriesBucket> Up, IReadOnlyList<SeriesBucket> Down) DownsamplePair(
        IReadOnlyList<SeriesBucket> up,
        IReadOnlyList<SeriesBucket> down,
        int maxBuckets = DefaultMaxBuckets)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBuckets, 1);

        var maxCount = Math.Max(up.Count, down.Count);
        if (maxCount <= maxBuckets) return (up, down);

        var factor = (int)Math.Ceiling((double)maxCount / maxBuckets);
        return (DownsampleOne(up, factor), DownsampleOne(down, factor));
    }

    /// <summary>
    /// Downsample a single series using the supplied grouping <paramref name="factor"/>.
    /// Public so callers driving paired series with a shared factor can use it.
    /// </summary>
    public static IReadOnlyList<SeriesBucket> DownsampleOne(
        IReadOnlyList<SeriesBucket> points, int factor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);

        if (factor == 1 || points.Count <= factor)
        {
            return points;
        }

        var result = new List<SeriesBucket>((points.Count / factor) + 1);
        for (int i = 0; i < points.Count; i += factor)
        {
            var groupStart = points[i].TimestampUnixMs;
            long sum = 0;
            var end = Math.Min(i + factor, points.Count);
            for (int j = i; j < end; j++)
            {
                sum += points[j].Value;
            }
            result.Add(new SeriesBucket(groupStart, sum));
        }
        return result;
    }
}
