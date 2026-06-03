namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Time grain a history query is served at. <see cref="Auto"/> lets the
/// service pick based on the window span (Phase 4 Q3 decision).
/// </summary>
public enum TrafficGrain
{
    /// <summary>Server picks based on window length (default).</summary>
    Auto = 0,

    /// <summary>60 s buckets from <c>traffic_samples</c>; 30-day retention.</summary>
    Samples = 1,

    /// <summary>1 h buckets from <c>traffic_hourly</c>; 90-day retention.</summary>
    Hourly = 2,

    /// <summary>1 d buckets from <c>traffic_daily</c>; 365-day retention.</summary>
    Daily = 3,
}

/// <summary>
/// Auto-grain rule: <c>≤24h → Samples</c> (per-minute detail, good for
/// spike-tracing), <c>≤30d → Hourly</c> (bucketed aggregates), <c>&gt;30d → Daily</c>.
/// User-facing UI never surfaces this — it's an implementation choice that
/// determines chart resolution and which storage tier serves the query.
/// </summary>
public static class TrafficGrainResolver
{
    private const long TwentyFourHoursMs = 24L * 3_600_000L;
    private const long ThirtyDaysMs      = 30L * 86_400_000L;

    public static TrafficGrain Resolve(QueryWindow window, TrafficGrain requested)
    {
        if (requested != TrafficGrain.Auto) return requested;
        var span = window.SpanMs;
        if (span <= TwentyFourHoursMs) return TrafficGrain.Samples;
        if (span <= ThirtyDaysMs)      return TrafficGrain.Hourly;
        return TrafficGrain.Daily;
    }
}
