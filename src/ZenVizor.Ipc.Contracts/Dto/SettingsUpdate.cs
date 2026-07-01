// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Partial update — every field is nullable so the UI sends only what
/// changed. Server applies each non-null field atomically (per-key UPSERT,
/// followed by SCM <c>ChangeServiceConfig</c> if <see cref="AutostartMode"/>
/// is present). Validation rejects negative retention days and undefined
/// enum values; partial-success is NOT supported — a rejected field fails
/// the whole call so the UI never reads a "half-applied" state on the next
/// <c>GetSettingsAsync</c>.
/// <para>
/// Epic B (1.2.0) split <see cref="ToastOnAlert"/> into three per-severity
/// fields. When both the legacy master and a per-severity field are set on
/// the same update, the server applies the master first (mass-set) then
/// the per-severity overrides, so the more-specific value wins. A 1.1.x UI
/// that only knows the master keeps working: writing
/// <c>ToastOnAlert = true</c> turns on all three per-severity keys.
/// </para>
/// </summary>
public sealed record SettingsUpdate
{
    public ServiceStartMode? AutostartMode { get; init; }
    public bool? ToastOnAlert { get; init; }
    public AppTheme? Theme { get; init; }
    public int? RetentionSamplesDays { get; init; }
    public int? RetentionConnectionsDays { get; init; }
    public int? RetentionHourlyDays { get; init; }
    public int? RetentionDailyDays { get; init; }
    public int? RetentionAlertsDaysAfterAck { get; init; }
    public bool? StartMinimized { get; init; }
    public int? AlertLargeDownloadMb { get; init; }
    public int? AlertOutboundHeavyFloorMb { get; init; }
    public int? AlertUnusualDailyVolumeKTimesTen { get; init; }
    public bool? SmoothChartAnimations { get; init; }

    /// <summary>Epic B — per-severity toast preference for Critical alerts.</summary>
    public bool? ToastOnCritical { get; init; }

    /// <summary>Epic B — per-severity toast preference for Warning alerts.</summary>
    public bool? ToastOnWarning { get; init; }

    /// <summary>Epic B — per-severity toast preference for Info alerts.</summary>
    public bool? ToastOnInfo { get; init; }
}
