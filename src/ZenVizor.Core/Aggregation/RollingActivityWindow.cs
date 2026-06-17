using ZenVizor.Core.Storage;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Aggregation;

/// <summary>
/// Holds the most-recently-sealed flush bucket of per-app byte totals plus the
/// timestamps that bracket it. <see cref="TakeSnapshot"/> combines that frozen
/// bucket with whatever has accumulated in the current partial since the last
/// flush, producing an <see cref="ActivitySnapshot"/> whose rate denominator is
/// the full span from the bucket's start through <c>now</c>.
/// <para>
/// Pure in-memory state — no SQLite I/O on any code path.
/// </para>
/// <para>
/// Before the first flush completes the window has no data; <see cref="TakeSnapshot"/>
/// returns an empty <see cref="ActivitySnapshot"/> with <c>WindowSeconds = 0</c>
/// rather than partial-only rates, per the Phase-3 cold-start preference.
/// </para>
/// </summary>
public sealed class RollingActivityWindow
{
    private IReadOnlyDictionary<ActivityKey, ActivityBytes>? _lastBucket;
    private ClassBreakdown _lastBucketBreakdown = ClassBreakdown.Empty;
    private long _lastBucketStartUnixMs;
    private long _lastBucketEndUnixMs;

    /// <summary>True once <see cref="OnFlush"/> has been called at least once.</summary>
    public bool HasData => _lastBucket is not null;

    /// <summary>
    /// Drops the sealed previous-bucket state. Called by
    /// <see cref="TrafficAggregator.ResetInMemoryState"/> on the
    /// service-side wipe path so the Dashboard's live activity surface
    /// reads as "no data" immediately after Reset history, not the
    /// previously-cached bucket rolled forward.
    /// </summary>
    public void Reset()
    {
        _lastBucket = null;
        _lastBucketBreakdown = ClassBreakdown.Empty;
        _lastBucketStartUnixMs = 0;
        _lastBucketEndUnixMs = 0;
    }

    /// <summary>
    /// Seal a completed flush bucket. <paramref name="bucketStartUnixMs"/> is when
    /// accumulation into that bucket began (the previous flush timestamp, or the
    /// aggregator's construction time for the very first bucket).
    /// <paramref name="bucketBreakdown"/> carries the WAN/Local byte totals
    /// for the same bucket — kept alongside the per-app rollup so the
    /// next snapshot can merge it with the partial without re-walking the
    /// per-app rows.
    /// </summary>
    public void OnFlush(
        IReadOnlyDictionary<ActivityKey, ActivityBytes> bucketPerApp,
        ClassBreakdown bucketBreakdown,
        long bucketStartUnixMs,
        long bucketEndUnixMs)
    {
        ArgumentNullException.ThrowIfNull(bucketPerApp);
        ArgumentNullException.ThrowIfNull(bucketBreakdown);
        _lastBucket = bucketPerApp;
        _lastBucketBreakdown = bucketBreakdown;
        _lastBucketStartUnixMs = bucketStartUnixMs;
        _lastBucketEndUnixMs = bucketEndUnixMs;
    }

    /// <summary>
    /// Combine the frozen previous bucket with the current partial accumulator
    /// (rolled up to the same per-app key) and return the snapshot. Rates are
    /// computed as <c>(bucket_bytes + partial_bytes) / windowSeconds</c> where
    /// <c>windowSeconds = (now − bucketStart)</c>.
    /// <paramref name="currentPartialBreakdown"/> is summed with the sealed
    /// bucket's breakdown to populate <see cref="ActivitySnapshot.WanLocalBreakdown"/>.
    /// </summary>
    public ActivitySnapshot TakeSnapshot(
        IReadOnlyDictionary<ActivityKey, ActivityBytes> currentPartial,
        ClassBreakdown currentPartialBreakdown,
        long nowUnixMs)
    {
        ArgumentNullException.ThrowIfNull(currentPartial);
        ArgumentNullException.ThrowIfNull(currentPartialBreakdown);

        if (_lastBucket is null)
        {
            return new ActivitySnapshot(
                CapturedAtUnixMs: nowUnixMs,
                WindowSeconds: 0.0,
                Apps: Array.Empty<AppActivity>(),
                WanLocalBreakdown: ClassBreakdown.Empty);
        }

        var windowMs = nowUnixMs - _lastBucketStartUnixMs;
        if (windowMs < 1)
        {
            // Clock skew or a snapshot taken in the same ms as the seal.
            windowMs = 1;
        }
        var windowSeconds = windowMs / 1000.0;

        var merged = new Dictionary<ActivityKey, ActivityBytes>(
            _lastBucket.Count + currentPartial.Count);

        foreach (var (key, bytes) in _lastBucket)
        {
            merged[key] = bytes;
        }
        foreach (var (key, bytes) in currentPartial)
        {
            if (merged.TryGetValue(key, out var existing))
            {
                merged[key] = new ActivityBytes(
                    existing.BytesUp + bytes.BytesUp,
                    existing.BytesDown + bytes.BytesDown);
            }
            else
            {
                merged[key] = bytes;
            }
        }

        var apps = new List<AppActivity>(merged.Count);
        foreach (var (key, bytes) in merged)
        {
            if (bytes.BytesUp == 0 && bytes.BytesDown == 0)
            {
                continue;
            }

            apps.Add(new AppActivity(
                ImageName: key.AppIdentity.ImageName,
                ImagePath: key.AppIdentity.ImagePath,
                Publisher: key.AppIdentity.Publisher,
                SignatureStatus: key.AppIdentity.SignatureStatus,
                IsUserWritablePath: key.AppIdentity.IsUserWritablePath,
                HostedServices: key.HostedServices,
                BytesUpTotal: bytes.BytesUp,
                BytesDownTotal: bytes.BytesDown,
                BytesUpPerSec: bytes.BytesUp / windowSeconds,
                BytesDownPerSec: bytes.BytesDown / windowSeconds));
        }

        var breakdown = new ClassBreakdown(
            WanBytesUp: _lastBucketBreakdown.WanBytesUp + currentPartialBreakdown.WanBytesUp,
            WanBytesDown: _lastBucketBreakdown.WanBytesDown + currentPartialBreakdown.WanBytesDown,
            LocalBytesUp: _lastBucketBreakdown.LocalBytesUp + currentPartialBreakdown.LocalBytesUp,
            LocalBytesDown: _lastBucketBreakdown.LocalBytesDown + currentPartialBreakdown.LocalBytesDown);

        return new ActivitySnapshot(
            CapturedAtUnixMs: nowUnixMs,
            WindowSeconds: windowSeconds,
            Apps: apps,
            WanLocalBreakdown: breakdown);
    }
}

/// <summary>
/// Rollup key for <see cref="RollingActivityWindow"/>. Distinct svchost PIDs
/// hosting different service sets get distinct keys (per CLAUDE.md invariant
/// #5: don't split bytes across co-hosted services). Multiple PIDs sharing the
/// same <see cref="AppIdentity"/> and (null) hosted services collapse into one
/// row.
/// </summary>
public readonly record struct ActivityKey(AppIdentity AppIdentity, string? HostedServices);

/// <summary>Mutable-style byte totals as a value record.</summary>
public readonly record struct ActivityBytes(long BytesUp, long BytesDown);
