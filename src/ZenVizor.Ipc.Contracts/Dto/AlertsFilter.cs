namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Server-side filter shape for <c>GetAlertsAsync</c>. Only the
/// <see cref="State"/> axis is server-applied; the brief §14 lock places
/// Severity and Type axes on the client (in-memory after a single
/// round-trip), and §15 declares Source-monitor and per-entity filtering
/// out of scope. <see cref="MaxRows"/> bounds query cost — server-side
/// safety net per the discovery-over-ranking principle (memory
/// <c>project_discovery_principle.md</c>): no top-N cap on the active
/// set is allowed, but a hard "do not stream more than N rows in a
/// single envelope" is a transport concern and lives here.
/// </summary>
public sealed record AlertsFilter(
    AlertState State,
    int MaxRows = 500);
