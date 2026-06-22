// SPDX-License-Identifier: GPL-3.0-or-later

using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ipc.Tests;

/// <summary>
/// In-memory IPC handler used by contract tests. The version-negotiation policy
/// is configurable so we can drive both the accepted and the rejected paths.
/// </summary>
internal sealed class FakeIpcHandler : IZenVizorIpc
{
    private readonly Func<string, NegotiateVersionResult> _versionPolicy;
    private Func<ActivitySnapshot> _snapshotProvider;

    public FakeIpcHandler(Func<string, NegotiateVersionResult>? versionPolicy = null)
    {
        _versionPolicy = versionPolicy ?? DefaultPolicy;
        _snapshotProvider = () => new ActivitySnapshot(0, 0.0, Array.Empty<AppActivity>(), ClassBreakdown.Empty);
    }

    public int PingCount { get; private set; }
    public int ActivitySnapshotCount { get; private set; }
    public string? LastNegotiatedClientVersion { get; private set; }

    // Default to the shared schema-version constant so a future server bump
    // doesn't leave this fake stamping a stale version (which is exactly the
    // drift the previous "Be(1)" assertion let through).
    public int SnapshotSchemaVersion { get; set; } = IpcSchemaVersion.ActivitySnapshot;

    public void SetSnapshot(ActivitySnapshot snapshot) => _snapshotProvider = () => snapshot;
    public void SetSnapshotProvider(Func<ActivitySnapshot> provider) => _snapshotProvider = provider;

    public Task<NegotiateVersionResult> NegotiateVersionAsync(string clientVersion)
    {
        LastNegotiatedClientVersion = clientVersion;
        return Task.FromResult(_versionPolicy(clientVersion));
    }

    public Task<PingResult> PingAsync()
    {
        PingCount++;
        return Task.FromResult(new PingResult(
            Pong: "pong",
            ServerTimestampUnixMs: 1_700_000_000_000L));
    }

    public Task<ServiceStatusResult> GetServiceStatusAsync()
    {
        return Task.FromResult(new ServiceStatusResult(
            ServiceName: "ZenVizor.Service",
            Version: "0.1.0",
            ProtocolVersion: ProtocolVersion.Current,
            StartedAtUnixMs: 1_700_000_000_000L,
            UptimeMs: 0,
            DbPath: @"C:\fake\zenvizor.db",
            CaptureActive: false));
    }

    public Task<IpcEnvelope<ActivitySnapshot>> GetCurrentActivitySnapshotAsync()
    {
        ActivitySnapshotCount++;
        return Task.FromResult(new IpcEnvelope<ActivitySnapshot>(
            SchemaVersion: SnapshotSchemaVersion,
            Payload: _snapshotProvider()));
    }

    public CaptureStats Stats { get; set; } = new(
        CapturedAtUnixMs: 1_700_000_000_000L,
        ObservationsSeen: 0,
        ObservationsUnattributed: 0);

    public Task<IpcEnvelope<CaptureStats>> GetCaptureStatsAsync()
    {
        return Task.FromResult(new IpcEnvelope<CaptureStats>(
            SchemaVersion: IpcSchemaVersion.CaptureStats,
            Payload: Stats));
    }

    // ---- Phase 4 query surface: scriptable stubs for contract tests ----

    public AppListResult AppList { get; set; } =
        new(new QueryWindow(0, 0), Array.Empty<AppListEntry>());
    public AppDetailResult AppDetail { get; set; } =
        new(new QueryWindow(0, 0), TrafficGrain.Samples,
            new AppListEntry(0, "", "", null, "Unchecked", false, 0, 0, 0, 0),
            Array.Empty<TrafficPoint>(), Array.Empty<SessionInfo>());
    public ConnectionListResult Connections { get; set; } =
        new(new QueryWindow(0, 0), Array.Empty<ConnectionRow>());
    public TrafficHistoryResult History { get; set; } =
        new(new QueryWindow(0, 0), TrafficGrain.Samples, Array.Empty<TrafficPoint>());

    public Task<IpcEnvelope<AppListResult>> GetAppListAsync(QueryWindow window) =>
        Task.FromResult(new IpcEnvelope<AppListResult>(IpcSchemaVersion.Query, AppList));

    public Task<IpcEnvelope<AppDetailResult>> GetAppDetailAsync(int appId, QueryWindow window, TrafficGrain grain) =>
        Task.FromResult(new IpcEnvelope<AppDetailResult>(IpcSchemaVersion.Query, AppDetail));

    public Task<IpcEnvelope<ConnectionListResult>> GetConnectionsAsync(int appId, QueryWindow window) =>
        Task.FromResult(new IpcEnvelope<ConnectionListResult>(IpcSchemaVersion.Query, Connections));

    public Task<IpcEnvelope<TrafficHistoryResult>> GetTrafficHistoryAsync(QueryWindow window, TrafficGrain grain) =>
        Task.FromResult(new IpcEnvelope<TrafficHistoryResult>(IpcSchemaVersion.Query, History));

    // ---- Phase 5 Reports surface: scriptable stub for contract tests ----
    public DailyReportResult DailyReport { get; set; } = new(
        Date:               new DateOnly(2026, 6, 8),
        Anchor:             AnchorMode.Avg7d,
        AnchorSpecificDate: null,
        Hero:               new DailyReportHero(0, 0, 0, 0, 0, 0, 0, 0),
        HourlyTraffic:      Array.Empty<DailyReportHourPoint>(),
        TopApps:            Array.Empty<DailyReportAppRow>(),
        UncommonTalkers:    Array.Empty<DailyReportTalker>(),
        Notable:            Array.Empty<DailyReportNotable>());

    public Task<IpcEnvelope<DailyReportResult>> GetDailyReportAsync(
        DateOnly date,
        AnchorMode anchor,
        DateOnly? anchorSpecificDate) =>
        Task.FromResult(new IpcEnvelope<DailyReportResult>(IpcSchemaVersion.DailyReport, DailyReport));

    // ---- Phase 6 Alerts surface: scriptable stubs for contract tests ----

    public AlertsResult Alerts { get; set; } =
        new(new AlertsFilter(AlertState.Active), Array.Empty<AlertDto>(), HasMore: false);

    public List<long> DismissedAlertIds { get; } = new();

    public Task<IpcEnvelope<AlertsResult>> GetAlertsAsync(AlertsFilter filter) =>
        Task.FromResult(new IpcEnvelope<AlertsResult>(IpcSchemaVersion.Alerts, Alerts));

    public Task DismissAlertAsync(long alertId)
    {
        DismissedAlertIds.Add(alertId);
        return Task.CompletedTask;
    }

    /// <summary>Phase 6.7 — counts how many times the QA hook fired.</summary>
    public int RunRollupRulesNowCallCount { get; private set; }

    public Task RunRollupRulesNowAsync()
    {
        RunRollupRulesNowCallCount++;
        return Task.CompletedTask;
    }

    // ---- Phase 6.2 Settings surface: scriptable stubs for contract tests ----

    public SettingsSnapshot Settings { get; set; } = new(
        AutostartMode:               ServiceStartMode.Automatic,
        ToastOnAlert:                true,
        Theme:                       AppTheme.System,
        FlushIntervalMs:             5000,
        FlushBucketSeconds:          60,
        RetentionSamplesDays:        30,
        RetentionConnectionsDays:    30,
        RetentionHourlyDays:         90,
        RetentionDailyDays:          365,
        RetentionAlertsDaysAfterAck: 90,
        StartMinimized:              false,
        AlertLargeDownloadMb:        50,
        AlertOutboundHeavyFloorMb:   10,
        AlertUnusualDailyVolumeKTimesTen: 25,
        SmoothChartAnimations:       false);

    public List<SettingsUpdate> AppliedUpdates { get; } = new();

    public WipeHistoryResult WipeResult { get; set; } =
        new(SamplesDeleted: 0, ConnectionsDeleted: 0, HourlyDeleted: 0,
            DailyDeleted: 0, AlertsDeleted: 0, SessionsDeleted: 0);

    public int WipeCount { get; private set; }

    public Task<IpcEnvelope<SettingsSnapshot>> GetSettingsAsync() =>
        Task.FromResult(new IpcEnvelope<SettingsSnapshot>(IpcSchemaVersion.Settings, Settings));

    public Task UpdateSettingsAsync(SettingsUpdate update)
    {
        AppliedUpdates.Add(update);
        return Task.CompletedTask;
    }

    public Task<IpcEnvelope<WipeHistoryResult>> WipeHistoryAsync()
    {
        WipeCount++;
        return Task.FromResult(new IpcEnvelope<WipeHistoryResult>(IpcSchemaVersion.Settings, WipeResult));
    }

    private static NegotiateVersionResult DefaultPolicy(string clientVersion) =>
        ProtocolVersion.IsCompatible(clientVersion)
            ? new NegotiateVersionResult(Accepted: true, ServerVersion: ProtocolVersion.Current, Reason: null)
            : new NegotiateVersionResult(
                Accepted: false,
                ServerVersion: ProtocolVersion.Current,
                Reason: $"Client major version {clientVersion} is not compatible with server major {ProtocolVersion.Major}.");
}
