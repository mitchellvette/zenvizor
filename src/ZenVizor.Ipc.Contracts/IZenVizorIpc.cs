using System.Threading.Tasks;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ipc.Contracts;

/// <summary>
/// The IPC surface exposed by the ZenVizor service to the UI and CLI.
/// Versioned via <see cref="ProtocolVersion"/>; clients MUST call
/// <see cref="NegotiateVersionAsync"/> first before any other method.
/// </summary>
public interface IZenVizorIpc
{
    /// <summary>
    /// First call after pipe connect. Server validates the client's wire-protocol
    /// version against its own. On mismatch the server returns
    /// <see cref="NegotiateVersionResult.Accepted"/> = false and may close the connection.
    /// </summary>
    Task<NegotiateVersionResult> NegotiateVersionAsync(string clientVersion);

    /// <summary>
    /// Liveness probe. Returns the server's current timestamp.
    /// </summary>
    Task<PingResult> PingAsync();

    /// <summary>
    /// Returns identifying / status information about the running service:
    /// service name, version, protocol version, start time, uptime, DB
    /// path, and a live capture-active flag the bottom-bar indicator and
    /// <c>zvctl status</c> render.
    /// </summary>
    Task<ServiceStatusResult> GetServiceStatusAsync();

    /// <summary>
    /// Returns a point-in-time view of per-app network activity for the dashboard
    /// and <c>zvctl snapshot</c>. Served from the in-memory aggregate; the service
    /// MUST NOT read SQLite on this path (architectural guard, mirrors the
    /// "Observe must not write to disk" invariant).
    /// </summary>
    Task<IpcEnvelope<ActivitySnapshot>> GetCurrentActivitySnapshotAsync();

    /// <summary>
    /// Returns the hot-path observation counters (seen / unattributed) so QA
    /// can verify attribution reliability for short-lived processes — the
    /// Phase-3 lifecycle-resolver gate.
    /// </summary>
    Task<IpcEnvelope<CaptureStats>> GetCaptureStatsAsync();

    // ---- Phase 4 query surface (history tiers, per-app drill-down) ----

    /// <summary>Apps ranked by total bytes over the window. Empty filter; filters land in the UI polish phase.</summary>
    Task<IpcEnvelope<AppListResult>> GetAppListAsync(QueryWindow window);

    /// <summary>Drill into one app: summary, time series at chosen grain, recent sessions.</summary>
    Task<IpcEnvelope<AppDetailResult>> GetAppDetailAsync(int appId, QueryWindow window, TrafficGrain grain = TrafficGrain.Auto);

    /// <summary>Endpoints an app talked to during the window. Server-aggregated per (protocol, remote_addr, remote_port).</summary>
    Task<IpcEnvelope<ConnectionListResult>> GetConnectionsAsync(int appId, QueryWindow window);

    /// <summary>Aggregate (all apps) traffic series at chosen grain. Auto-grain by default.</summary>
    Task<IpcEnvelope<TrafficHistoryResult>> GetTrafficHistoryAsync(QueryWindow window, TrafficGrain grain = TrafficGrain.Auto);

    // ---- Phase 5 Reports surface ----

    /// <summary>
    /// Returns the daily report for <paramref name="date"/> with deltas computed
    /// against the chosen <paramref name="anchor"/>. Hero numerics, the 24-hour
    /// sparkline series, Top Apps, Uncommon Talkers, and Notable items all flow
    /// in a single payload — UI uses one round-trip per refresh.
    /// </summary>
    /// <param name="date">The report's calendar date (user-local).</param>
    /// <param name="anchor">The comparison baseline mode.</param>
    /// <param name="anchorSpecificDate">Required only when <paramref name="anchor"/>
    /// is <see cref="AnchorMode.SpecificDate"/>; otherwise ignored.</param>
    Task<IpcEnvelope<DailyReportResult>> GetDailyReportAsync(
        DateOnly date,
        AnchorMode anchor,
        DateOnly? anchorSpecificDate);

    // ---- Phase 6 Alerts surface ----

    /// <summary>
    /// Returns alerts matching the given filter. Server-side filtering covers
    /// the <see cref="AlertsFilter.State"/> axis only — Severity and Type axes
    /// are applied client-side per Alerts brief §14 (the entire matched set
    /// returns in a single envelope so the client can pivot without round-trips).
    /// Results are ordered reverse-chronological (newest first) per brief §11
    /// (discovery-over-ranking). The <see cref="AlertsFilter.MaxRows"/> bound
    /// is a transport safety net, not a top-N rank cap.
    /// </summary>
    Task<IpcEnvelope<AlertsResult>> GetAlertsAsync(AlertsFilter filter);

    /// <summary>
    /// Marks an active alert as dismissed. One-click action with no
    /// confirmation per brief §3.5 + §8.2 lock — dismissed rows remain
    /// queryable via the <c>Dismissed</c> and <c>All</c> filters until
    /// retention purges them (dismissed + 90 days, configurable per PRD §7.9).
    /// The internal column name stays <c>acknowledged_at</c> (no schema
    /// migration) but the IPC method, the CLI subcommand, and every visible
    /// string use "dismiss" per the catalog §1.2 vocabulary lock. No-op if
    /// the alert is already dismissed or does not exist (idempotent —
    /// double-clicks must not throw).
    /// </summary>
    Task DismissAlertAsync(long alertId);
}
