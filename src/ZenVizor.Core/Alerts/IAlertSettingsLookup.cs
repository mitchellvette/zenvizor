namespace ZenVizor.Core.Alerts;

/// <summary>
/// Producer-side read interface for the user-tunable alert thresholds
/// (Phase 6.7). Rules call this on every evaluation — implementations
/// must be cheap (typically an atomic read of a cached int) and
/// thread-safe (flush-thread + UI-thread can both touch it).
/// <para>
/// The host service backs this with a small lookup that re-reads from
/// SQLite on <c>UpdateSettingsAsync</c>; the test path supplies a
/// <see cref="StaticAlertSettingsLookup"/> with constants. The interface
/// keeps the rule implementations pure — they don't see SQLite, they
/// just see "current threshold."
/// </para>
/// </summary>
public interface IAlertSettingsLookup
{
    /// <summary>LargeDownload byte threshold in megabytes.</summary>
    int LargeDownloadMb { get; }

    /// <summary>OutboundHeavy minimum outbound MB over the 15-minute rolling window.</summary>
    int OutboundHeavyFloorMb { get; }

    /// <summary>UnusualDailyVolume sensitivity multiplier × 10 (default 25 = k of 2.5).</summary>
    int UnusualDailyVolumeKTimesTen { get; }
}

/// <summary>
/// Constant-valued <see cref="IAlertSettingsLookup"/> for test paths and
/// any caller that wants threshold control without a settings cache.
/// </summary>
public sealed class StaticAlertSettingsLookup : IAlertSettingsLookup
{
    public int LargeDownloadMb { get; }
    public int OutboundHeavyFloorMb { get; }
    public int UnusualDailyVolumeKTimesTen { get; }

    public StaticAlertSettingsLookup(
        int largeDownloadMb = 50,
        int outboundHeavyFloorMb = 10,
        int unusualDailyVolumeKTimesTen = 25)
    {
        LargeDownloadMb = largeDownloadMb;
        OutboundHeavyFloorMb = outboundHeavyFloorMb;
        UnusualDailyVolumeKTimesTen = unusualDailyVolumeKTimesTen;
    }
}
