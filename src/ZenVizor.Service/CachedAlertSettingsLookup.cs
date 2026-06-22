// SPDX-License-Identifier: GPL-3.0-or-later

using System.Threading;
using ZenVizor.Core.Alerts;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Service;

/// <summary>
/// Service-side <see cref="IAlertSettingsLookup"/>. Caches the three
/// alert thresholds as <see cref="Interlocked"/>-managed ints so the
/// flush-thread reads are lock-free; <see cref="Refresh"/> re-reads from
/// the <c>settings</c> table and lands new values atomically. Wired into
/// <c>ApplySettingsUpdate</c> so a UI write surfaces on the next flush.
/// </summary>
public sealed class CachedAlertSettingsLookup : IAlertSettingsLookup
{
    private readonly SettingsRepository _settings;
    private int _largeDownloadMb;
    private int _outboundHeavyFloorMb;
    private int _unusualDailyVolumeKTimesTen;

    public CachedAlertSettingsLookup(SettingsRepository settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Refresh();
    }

    public int LargeDownloadMb => Volatile.Read(ref _largeDownloadMb);
    public int OutboundHeavyFloorMb => Volatile.Read(ref _outboundHeavyFloorMb);
    public int UnusualDailyVolumeKTimesTen => Volatile.Read(ref _unusualDailyVolumeKTimesTen);

    /// <summary>
    /// Re-read the three thresholds from the settings table and stash
    /// them in the cached fields. Called once at construction and again
    /// from ApplySettingsUpdate after a UI write so rules pick up the
    /// new values on the next flush.
    /// </summary>
    public void Refresh()
    {
        Volatile.Write(ref _largeDownloadMb,
            _settings.GetInt(SettingsRepository.Keys.AlertLargeDownloadMb, 50));
        Volatile.Write(ref _outboundHeavyFloorMb,
            _settings.GetInt(SettingsRepository.Keys.AlertOutboundHeavyFloorMb, 10));
        Volatile.Write(ref _unusualDailyVolumeKTimesTen,
            _settings.GetInt(SettingsRepository.Keys.AlertUnusualDailyVolumeKTimesTen, 25));
    }
}
