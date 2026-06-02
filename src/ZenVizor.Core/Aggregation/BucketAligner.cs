namespace ZenVizor.Core.Aggregation;

/// <summary>
/// Aligns Unix-millisecond timestamps to bucket boundaries. Buckets are
/// epoch-aligned (UTC) so two services rolling the same data agree on boundaries.
/// </summary>
public static class BucketAligner
{
    /// <summary>Default <c>traffic_samples</c> bucket width per PRD §7.3.</summary>
    public const int DefaultBucketSeconds = 60;

    /// <summary>
    /// Snap <paramref name="timestampUnixMs"/> down to the start of its bucket.
    /// </summary>
    public static long AlignToBucket(long timestampUnixMs, int bucketSeconds = DefaultBucketSeconds)
    {
        if (bucketSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketSeconds), "Bucket width must be positive.");
        }

        var bucketMs = (long)bucketSeconds * 1000L;
        // Floor-toward-negative-infinity to handle pre-epoch timestamps cleanly,
        // even though we don't expect to see them in practice.
        var remainder = ((timestampUnixMs % bucketMs) + bucketMs) % bucketMs;
        return timestampUnixMs - remainder;
    }
}
