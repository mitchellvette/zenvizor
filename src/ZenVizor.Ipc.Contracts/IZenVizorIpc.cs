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
    /// Returns identifying / status information about the running service.
    /// Phase 0 stub: capture/DB fields will be wired in later phases.
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
}
