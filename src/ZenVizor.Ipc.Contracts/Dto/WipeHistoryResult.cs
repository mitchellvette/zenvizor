namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Per-table row counts deleted by a "Reset history" wipe. Returned so the
/// UI can surface a confirmation toast like "Deleted 1.2M rows". A wipe
/// covering zero rows is still a success (idempotent) — the UI should not
/// treat all-zeros as an error.
/// </summary>
public sealed record WipeHistoryResult(
    int SamplesDeleted,
    int ConnectionsDeleted,
    int HourlyDeleted,
    int DailyDeleted,
    int AlertsDeleted,
    int SessionsDeleted)
{
    public int TotalDeleted =>
        SamplesDeleted + ConnectionsDeleted + HourlyDeleted +
        DailyDeleted + AlertsDeleted + SessionsDeleted;
}
