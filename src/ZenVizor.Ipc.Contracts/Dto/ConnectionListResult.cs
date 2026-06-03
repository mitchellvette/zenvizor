namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Endpoints an app talked to during the window. Per Phase 4 Q8 decision:
/// rows are aggregated by <c>(protocol, remote_addr, remote_port)</c> across
/// all of the app's sessions in window — one row per endpoint regardless of
/// session count. Temporal/per-session detail is not surfaced here; spike
/// shape lives in <c>traffic_samples</c> (by app) and is the alert pipeline's
/// concern.
/// </summary>
public sealed record ConnectionListResult(
    QueryWindow Window,
    IReadOnlyList<ConnectionRow> Connections);

public sealed record ConnectionRow(
    string Protocol,
    string RemoteAddress,
    int RemotePort,
    string RemoteClass,
    long BytesUp,
    long BytesDown,
    long FirstSeenUnixMs,
    long LastSeenUnixMs);
