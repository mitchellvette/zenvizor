// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Anchor mode for the report's delta-vs-baseline figures. The user picks
/// one in the chrome row; the service computes deltas accordingly.
/// </summary>
public enum AnchorMode
{
    /// <summary>Compare to a single historical date (the request carries it).</summary>
    SpecificDate = 0,
    /// <summary>Compare to the 7 days prior to the report date.</summary>
    Avg7d = 1,
    /// <summary>Compare to the 30 days prior to the report date.</summary>
    Avg30d = 2,
    /// <summary>Compare to the 90 days prior to the report date.</summary>
    Avg90d = 3,
}

/// <summary>
/// Severity tag for Notable entries — inherits the Alerts entity's
/// severity vocabulary (mockup Q7a lock; severity_tokens annotation on
/// mockup page 9). Critical → status.critical; Warning → status.caution;
/// Info → status.neutral.
/// </summary>
public enum NotableSeverity
{
    Critical = 0,
    Warning  = 1,
    Info     = 2,
}

/// <summary>
/// Category tag for Uncommon Talker entries (mockup Q5b lock):
/// <list type="bullet">
///   <item><description><see cref="NewToday"/> — first-seen publisher / image on this machine.</description></item>
///   <item><description><see cref="UnusualVolume"/> — bytes deviate from the app's rolling median.</description></item>
///   <item><description><see cref="RiskyPaths"/> — running from a user-writable path.</description></item>
/// </list>
/// Hand-off page 12 lock: the category color is on the column-header
/// glyph only — individual mini-cards stay neutral.
/// </summary>
public enum UncommonCategory
{
    NewToday      = 0,
    UnusualVolume = 1,
    RiskyPaths    = 2,
}

/// <summary>
/// Full daily-report payload. Returned by
/// <c>IZenVizorIpc.GetDailyReportAsync</c>; consumed by the Reports
/// page. Schema version is carried on the <see cref="IpcEnvelope{T}"/>
/// wrapper, not here.
/// </summary>
/// <remarks>
/// The shape mirrors the four mockup-locked surfaces (Hero, Top Apps,
/// Uncommon Talkers, Notable) plus the 24-hour sparkline series.
/// Phase 5a ships this contract with a server-side stub that returns
/// the same shape the Phase 3 mock used; Phase 5b replaces the stub
/// with a SQLite-driven aggregator.
/// </remarks>
public sealed record DailyReportResult(
    DateOnly Date,
    AnchorMode Anchor,
    DateOnly? AnchorSpecificDate,
    DailyReportHero Hero,
    IReadOnlyList<DailyReportHourPoint> HourlyTraffic,
    IReadOnlyList<DailyReportAppRow> TopApps,
    IReadOnlyList<DailyReportTalker> UncommonTalkers,
    IReadOnlyList<DailyReportNotable> Notable);

/// <summary>
/// Hero card numerics + deltas vs the chosen anchor.
/// <para>
/// Ratios are unit-interval doubles (0.0-1.0); the UI multiplies by 100
/// for the % display. Delta percentages are signed — negative means
/// "less than anchor" (UI paints with status.success in the green delta
/// chip). The brief locks the delta-color semantics on mockup page 12:
/// "status.neutral for more-traffic, status.success for less."
/// </para>
/// <para>
/// <see cref="BaselineDaysAvailable"/> is the number of pre-report days
/// of history the service has observed, capped at the anchor's nominal
/// size (7 / 30 / 90). The UI uses it to gate the deltas: &lt; 3 days
/// hides the chips entirely and surfaces a "Comparisons unlock on N"
/// caption; 3..anchor-1 keeps the chips with a partial-baseline caution
/// note; &gt;= anchor uses the deltas as-is. Closes the fresh-install
/// honesty gap where partial baselines produced misleading
/// percentages.
/// </para>
/// </summary>
public sealed record DailyReportHero(
    long TotalUpBytes,
    long TotalDownBytes,
    double WanRatio,
    double LocalRatio,
    double TotalDeltaPct,
    double UpDeltaPct,
    double DownDeltaPct,
    int BaselineDaysAvailable);

/// <summary>
/// One hour-bucket of the daily sparkline series. <see cref="Hour"/> is
/// the local-time hour index (0-23) relative to the report date — the
/// server is responsible for bucketing UTC observations into the user's
/// local hours so the report reads truthfully against "what happened on
/// my machine that day".
/// </summary>
public sealed record DailyReportHourPoint(
    int Hour,
    long BytesUp,
    long BytesDown);

/// <summary>
/// One row of the Top Apps DataGrid. Locked Q4a vocabulary (Per-App
/// shape). <see cref="HasOverlap"/> is the server-side overlap flag —
/// true when this app also surfaces in <see cref="DailyReportResult.UncommonTalkers"/>
/// or <see cref="DailyReportResult.Notable"/>. The UI paints a small
/// accent.text dot next to the image name when set (mockup page 8 Q4
/// audit: "· marks the overlap").
/// </summary>
public sealed record DailyReportAppRow(
    int AppId,
    string ImageName,
    string ImagePath,
    string? Publisher,
    string SignatureStatus,
    bool IsUserWritablePath,
    long BytesUp,
    long BytesDown,
    bool HasOverlap);

/// <summary>
/// One Uncommon Talker mini-card. <see cref="Reason"/> is server-composed
/// prose — the surface's whole job (mockup page 9 token annotation).
/// Reason text bans em-dashes per <c>feedback_no_emdash_in_ui_copy</c>;
/// the aggregator must use period / semicolon when composing.
/// </summary>
public sealed record DailyReportTalker(
    UncommonCategory Category,
    int AppId,
    string ImageName,
    string? Publisher,
    string SignatureStatus,
    string Reason,
    bool HasOverlap);

/// <summary>
/// One Notable incident card. MVP only emits Critical entries from the
/// <c>UnsignedFromUserPath</c> rule (CLAUDE.md "MVP knows ONE rule");
/// Warning + Info severities exist in the enum for forward compat but
/// stay empty until sprint Phase 6 widens the rule set.
/// <para>
/// <see cref="AlertId"/> is the cross-page reference into the Alerts
/// feed. Phase 6.4 wires the deep-link end-to-end: the repository
/// LEFT JOINs to the <c>alerts</c> table on
/// <c>(type, entity_kind, entity_ref)</c> so each Notable row carries
/// the matching <c>alerts.alert_id</c> when one exists. The Reports UI
/// renders the chip as <c>Alerts · #{AlertId}</c>; clicking it
/// navigates to the Alerts page filtered to State=All and scrolled to
/// the matching row. A sentinel value of <c>0</c> means no matching
/// alert row was found and the chip remains visible-but-inert (the
/// producer should have inserted by report time, but the LEFT JOIN
/// keeps Reports honest if it hasn't).
/// </para>
/// </summary>
public sealed record DailyReportNotable(
    NotableSeverity Severity,
    string Title,
    string Detail,
    int AppId,
    string ImageName,
    int Pid,
    long EventTimeUnixMs,
    int AlertId);
