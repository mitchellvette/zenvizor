namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Drill-down into one app: summary, a time series at the chosen grain, and
/// the recent sessions (svchost service breakdown lives on the session rows).
/// </summary>
public sealed record AppDetailResult(
    QueryWindow Window,
    TrafficGrain GrainUsed,
    AppListEntry Summary,
    IReadOnlyList<TrafficPoint> Series,
    IReadOnlyList<SessionInfo> RecentSessions);

public sealed record TrafficPoint(
    long BucketStartUnixMs,
    string RemoteClass,
    long BytesUp,
    long BytesDown);

public sealed record SessionInfo(
    long SessionId,
    int Pid,
    long StartTimeUnixMs,
    long? EndTimeUnixMs,
    string? HostedServices);
