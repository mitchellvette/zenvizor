// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Snapshot of every runtime knob the Settings page reads. The wire shape is
/// flat (no nesting) so the UI VM can bind 1:1 to its INPC fields without
/// projection. Server reconciles SCM state into <see cref="AutostartMode"/>
/// at send-time — the settings-table mirror row is the cache, not the source
/// of truth.
/// </summary>
/// <param name="AutostartMode">
/// Authoritative SCM start mode for the ZenVizor service. The settings
/// table row is updated to match before this snapshot is built.
/// </param>
/// <param name="ToastOnAlert">
/// Master toast preference. On a snapshot from a 1.2.0+ service this
/// value is <b>computed</b> as
/// <c>ToastOnCritical || ToastOnWarning || ToastOnInfo</c> — retained
/// so older UIs (pre-Epic-B) that read only this field still see a
/// coherent "any-severity-toasts-on" answer. On an update from an
/// older UI, writing this field mass-sets the three per-severity
/// keys (see <see cref="SettingsUpdate.ToastOnAlert"/>). Phase 6.2
/// wired the original single-boolean emission inline; Epic B (1.2.0)
/// split that into per-severity fields below.
/// </param>
/// <param name="Theme">
/// UI theme override. <see cref="AppTheme.System"/> defers to
/// <c>SystemThemeWatcher</c>; Light / Dark unwire the watcher and pin the
/// theme. Mirrored to <c>%LocalAppData%\ZenVizor\ui.theme</c> for cold-start
/// resolution.
/// </param>
/// <param name="FlushIntervalMs">
/// Read-only diagnostic. Locked to <c>5000</c> per PRD §15 until a
/// post-v1 brief flips the lock. Surfaced so users can see what they
/// have today.
/// </param>
/// <param name="FlushBucketSeconds">
/// Read-only diagnostic. Locked to <c>60</c> per PRD §15.
/// </param>
/// <param name="RetentionSamplesDays">Retention window for traffic_samples.</param>
/// <param name="RetentionConnectionsDays">Retention window for connections.</param>
/// <param name="RetentionHourlyDays">Retention window for traffic_hourly.</param>
/// <param name="RetentionDailyDays">Retention window for traffic_daily.</param>
/// <param name="RetentionAlertsDaysAfterAck">
/// Days after dismiss before an alert is purged. The internal column name
/// is <c>acknowledged_at</c> (no schema migration) but the IPC field and
/// every visible string use "dismiss" per the catalog §1.2 vocabulary lock.
/// </param>
/// <param name="StartMinimized">
/// When true, App.OnStartup hides the window so the UI launches straight to
/// tray. Phase 6.3 added the toggle; the value is mirrored to
/// <c>%LocalAppData%\ZenVizor\ui.start-minimized</c> so the boot-time launch
/// can read it synchronously before any IPC.
/// </param>
/// <param name="AlertLargeDownloadMb">
/// LargeDownload rule threshold (Phase 6.7). Single-connection bytes-down
/// total within a 60 s sliding window that qualifies as "large". Default
/// 50 MB; range 1-1024.
/// </param>
/// <param name="AlertOutboundHeavyFloorMb">
/// OutboundHeavy rule minimum outbound bytes over the 15-minute rolling
/// window for an app to qualify (Phase 6.7). Default 10 MB; range 1-1024.
/// The 3:1 outbound/inbound ratio is locked separately.
/// </param>
/// <param name="AlertUnusualDailyVolumeKTimesTen">
/// UnusualDailyVolume sensitivity multiplier × 10 (so the wire format
/// stays integer, Phase 6.7). Default 25 (= k of 2.5); range 10-100
/// (k of 1.0 to 10.0). Formula: alert when day total ≥ k × median(last
/// 14 days) AND day delta over median ≥ 50 MB hard-coded floor.
/// Documented divergence from the original brief: this is k × median,
/// not median + k × MAD — chose intuitive slider semantics over robust
/// statistics. Revisit if low-variance apps generate noise.
/// </param>
/// <param name="SmoothChartAnimations">
/// When true, Dashboard charts animate transitions (2200 ms linear
/// scroll easing). Default false — animating the live-rates / sparkline
/// chart pair adds ~8% idle CPU while the Dashboard is open. No effect
/// when the UI is in the tray (the page isn't rendering). Phase 9.a
/// exposed the previously code-only flag. Effect applies on next nav to
/// Dashboard, not live to a currently-open Dashboard.
/// </param>
/// <param name="ToastOnCritical">
/// Epic B (1.2.0). When true, Critical alerts fire a Windows toast.
/// Fresh-install default true. Legacy upgrade honours
/// <c>toast.on_alert</c>: pre-1.2.0 users with <c>toast.on_alert=1</c>
/// keep toasts on for every severity, so a passive user who relied on
/// the master toggle never silently loses notifications after the
/// upgrade.
/// </param>
/// <param name="ToastOnWarning">
/// Epic B (1.2.0). When true, Warning alerts fire a Windows toast.
/// Fresh-install default false — killed the day-one FirstRunWanTalker
/// flood together with <see cref="ToastOnInfo"/>. Same legacy-honour
/// rule as <see cref="ToastOnCritical"/>.
/// </param>
/// <param name="ToastOnInfo">
/// Epic B (1.2.0). When true, Info alerts fire a Windows toast.
/// Fresh-install default false. Same legacy-honour rule as
/// <see cref="ToastOnCritical"/>.
/// </param>
public sealed record SettingsSnapshot(
    ServiceStartMode AutostartMode,
    bool ToastOnAlert,
    AppTheme Theme,
    int FlushIntervalMs,
    int FlushBucketSeconds,
    int RetentionSamplesDays,
    int RetentionConnectionsDays,
    int RetentionHourlyDays,
    int RetentionDailyDays,
    int RetentionAlertsDaysAfterAck,
    bool StartMinimized,
    int AlertLargeDownloadMb,
    int AlertOutboundHeavyFloorMb,
    int AlertUnusualDailyVolumeKTimesTen,
    bool SmoothChartAnimations,
    bool ToastOnCritical = true,
    bool ToastOnWarning = false,
    bool ToastOnInfo = false);
