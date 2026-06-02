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
    private long _lastBucketStartUnixMs;
    private long _lastBucketEndUnixMs;

    /// <summary>True once <see cref="OnFlush"/> has been called at least once.</summary>
    public bool HasData => _lastBucket is not null;

    /// <summary>
    /// Seal a completed flush bucket. <paramref name="bucketStartUnixMs"/> is when
    /// accumulation into that bucket began (the previous flush timestamp, or the
    /// aggregator's construction time for the very first bucket).
    /// </summary>
    public void OnFlush(
        IReadOnlyDictionary<ActivityKey, ActivityBytes> bucketPerApp,
        long bucketStartUnixMs,
        long bucketEndUnixMs)
    {
        ArgumentNullException.ThrowIfNull(bucketPerApp);
        _lastBucket = bucketPerApp;
        _lastBucketStartUnixMs = bucketStartUnixMs;
        _lastBucketEndUnixMs = bucketEndUnixMs;
    }

    /// <summary>
    /// Combine the frozen previous bucket with the current partial accumulator
    /// (rolled up to the same per-app key) and return the snapshot. Rates are
    /// computed as <c>(bucket_bytes + partial_bytes) / windowSeconds</c> where
    /// <c>windowSeconds = (now − bucketStart)</c>.
    /// </summary>
    public ActivitySnapshot TakeSnapshot(
        IReadOnlyDictionary<ActivityKey, ActivityBytes> currentPartial,
        long nowUnixMs)
    {
        ArgumentNullException.ThrowIfNull(currentPartial);

        if (_lastBucket is null)
        {
            return new ActivitySnapshot(
                CapturedAtUnixMs: nowUnixMs,
                WindowSeconds: 0.0,
                Apps: Array.Empty<AppActivity>());
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

        return new ActivitySnapshot(
            CapturedAtUnixMs: nowUnixMs,
            WindowSeconds: windowSeconds,
            Apps: apps);
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
