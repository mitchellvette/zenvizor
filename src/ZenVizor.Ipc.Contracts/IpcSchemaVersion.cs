namespace ZenVizor.Ipc.Contracts;

/// <summary>
/// Single source of truth for <see cref="IpcEnvelope{T}.SchemaVersion"/> values.
/// Both the server (when stamping envelopes) and clients (when validating them)
/// reference these constants — defining the version in only one place prevents
/// the kind of drift the audit caught when an in-process test asserted v1
/// while the production handler had silently bumped the snapshot envelope to v2.
/// </summary>
public static class IpcSchemaVersion
{
    /// <summary>Schema version of <see cref="Dto.ActivitySnapshot"/> payloads.</summary>
    /// <remarks>
    /// v2: <c>WanLocalBreakdown</c> became a required field. The positional
    /// record ctor means a v1-shape payload can't deserialize as v2, hence
    /// the floor check on the client side.
    /// </remarks>
    public const int ActivitySnapshot = 2;

    /// <summary>Schema version of <see cref="Dto.CaptureStats"/> payloads.</summary>
    public const int CaptureStats = 1;

    /// <summary>
    /// Shared schema version of the Phase-4 query result payloads
    /// (AppList / AppDetail / ConnectionList / TrafficHistory).
    /// </summary>
    /// <remarks>
    /// v2 (Phase 8): <c>ConnectionRow.ResolvedHost</c> added as a trailing
    /// optional positional field. The four query payloads move together
    /// under one constant — see Phase 8 design decision D2 in
    /// <c>docs/zenvizor-sprint-plan.md</c> — so AppList / AppDetail /
    /// TrafficHistory step to v2 alongside ConnectionList even though
    /// their own shape is unchanged. A v1 client receiving a v2 envelope
    /// will deserialize fine (STJ ignores unknown fields) but a v2 client
    /// against a v1 server faults via the client-side schema floor check;
    /// the check is load-bearing for the new field.
    /// v1 (Phase 4): initial.
    /// </remarks>
    public const int Query = 2;

    /// <summary>Schema version of <see cref="Dto.DailyReportResult"/> payloads.</summary>
    /// <remarks>
    /// v3 (Phase 9.3): <c>DailyReportHero.BaselineDaysAvailable</c> added as
    /// a required trailing positional field. Drives the fresh-install
    /// hero-deltas guard. A v2 payload won't deserialize as v3 (positional
    /// record); floor check on the client is load-bearing.
    /// v2 (Phase 6.4): <c>DailyReportNotable.AlertId</c> now carries the real
    /// <c>alerts.alert_id</c> projected via LEFT JOIN, not the always-zero
    /// sentinel Phase 5b emitted. Shape is unchanged (the field already
    /// existed) but value semantics flip — bumping makes the upgrade
    /// traceable in client-side diagnostics and the floor check.
    /// v1 (Phase 5b): initial.
    /// </remarks>
    public const int DailyReport = 3;

    /// <summary>
    /// Schema version of <see cref="Dto.AlertsResult"/> payloads and the
    /// <c>AlertDto</c> rows it carries. Phase 6 — initial.
    /// </summary>
    public const int Alerts = 1;

    /// <summary>
    /// Schema version of <see cref="Dto.SettingsSnapshot"/> /
    /// <see cref="Dto.SettingsUpdate"/> / <see cref="Dto.WipeHistoryResult"/>
    /// payloads.
    /// </summary>
    /// <remarks>
    /// v4 (Phase 9.a): <c>SmoothChartAnimations</c> added as a required
    /// trailing positional field on <see cref="Dto.SettingsSnapshot"/>.
    /// Surfaces the previously code-only Dashboard chart-animation flag
    /// as a user setting. A v3 payload won't deserialize as v4 (positional
    /// record); the client-side schema floor check is load-bearing.
    /// v3 (Phase 6.7): three alert-threshold fields added as required
    /// positional fields on <see cref="Dto.SettingsSnapshot"/> —
    /// <c>AlertLargeDownloadMb</c>, <c>AlertOutboundHeavyFloorMb</c>,
    /// <c>AlertUnusualDailyVolumeKTimesTen</c>. A v2 payload won't
    /// deserialize as v3; floor check is load-bearing.
    /// v2 (Phase 6.3): <c>StartMinimized</c> added as a required positional
    /// field on <see cref="Dto.SettingsSnapshot"/>. A v1 payload won't
    /// deserialize as v2, so the floor check on the client side is
    /// load-bearing.
    /// v1 (Phase 6.2): initial.
    /// </remarks>
    public const int Settings = 4;
}
