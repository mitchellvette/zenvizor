namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Aggregate (all apps) traffic series over the window at the resolved grain.
/// </summary>
public sealed record TrafficHistoryResult(
    QueryWindow Window,
    TrafficGrain GrainUsed,
    IReadOnlyList<TrafficPoint> Series);
