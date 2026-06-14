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
    public const int Query = 1;

    /// <summary>Schema version of <see cref="Dto.DailyReportResult"/> payloads.</summary>
    public const int DailyReport = 1;

    /// <summary>
    /// Schema version of <see cref="Dto.AlertsResult"/> payloads and the
    /// <c>AlertDto</c> rows it carries. Phase 6 — initial.
    /// </summary>
    public const int Alerts = 1;
}
