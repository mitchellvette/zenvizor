namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// Identifies the service-side component that raised an alert. The UI
/// renders the user-facing label via the catalog §1.3 lookup
/// (<see cref="Capture"/> → "Capture", <see cref="Rollup"/> → "Daily check");
/// the raw enum value never appears in user-facing strings.
/// <para>
/// Future producers (HostsFile, ProxyWatcher) extend this enum at the same
/// time their catalog entry + UI label land.
/// </para>
/// </summary>
public enum SourceMonitor
{
    /// <summary>The capture pipeline raised it (per-event or near-real-time).</summary>
    Capture = 0,

    /// <summary>The daily-rollup tick raised it (end-of-day summary anomaly).</summary>
    Rollup = 1,
}
