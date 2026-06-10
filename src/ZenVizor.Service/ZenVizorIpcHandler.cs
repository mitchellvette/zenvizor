using System.Reflection;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Service;

/// <summary>
/// The service-side implementation of <see cref="IZenVizorIpc"/>.
/// Phase 0 stub: capture/DB-derived fields will be wired in later phases.
/// </summary>
internal sealed class ZenVizorIpcHandler : IZenVizorIpc
{
    /// <summary>
    /// Schema version of the <see cref="ActivitySnapshot"/> payload. Bump on
    /// incompatible changes.
    /// <para>
    /// v2 (this revision): adds <see cref="ActivitySnapshot.WanLocalBreakdown"/>
    /// as a required field. Positional ctor means an old client can't
    /// deserialize a v2 payload, hence the bump. Service+UI ship together
    /// so there is no real version skew window — the bump is a discipline
    /// marker, not a deprecation hook.
    /// </para>
    /// </summary>
    private const int ActivitySnapshotSchemaVersion = 2;

    /// <summary>Schema version of the <see cref="CaptureStats"/> payload.</summary>
    private const int CaptureStatsSchemaVersion = 1;

    /// <summary>Schema version of the Phase-4 query result payloads.</summary>
    private const int QuerySchemaVersion = 1;

    /// <summary>Schema version of the Phase-5 DailyReport payload.</summary>
    private const int DailyReportSchemaVersion = 1;

    private readonly long _startedAtUnixMs;
    private readonly string _dbPath;
    private readonly Func<bool> _isCaptureActive;
    private readonly Func<ActivitySnapshot> _snapshotProvider;
    private readonly Func<CaptureStats> _statsProvider;
    private readonly Func<QueryWindow, AppListResult> _appListProvider;
    private readonly Func<int, QueryWindow, TrafficGrain, AppDetailResult> _appDetailProvider;
    private readonly Func<int, QueryWindow, ConnectionListResult> _connectionsProvider;
    private readonly Func<QueryWindow, TrafficGrain, TrafficHistoryResult> _historyProvider;
    private readonly Func<DateOnly, AnchorMode, DateOnly?, DailyReportResult> _dailyReportProvider;
    private readonly string _serviceVersion;

    public ZenVizorIpcHandler(
        long startedAtUnixMs,
        string dbPath,
        Func<bool>? isCaptureActive = null,
        Func<ActivitySnapshot>? snapshotProvider = null,
        Func<CaptureStats>? statsProvider = null,
        Func<QueryWindow, AppListResult>? appListProvider = null,
        Func<int, QueryWindow, TrafficGrain, AppDetailResult>? appDetailProvider = null,
        Func<int, QueryWindow, ConnectionListResult>? connectionsProvider = null,
        Func<QueryWindow, TrafficGrain, TrafficHistoryResult>? historyProvider = null,
        Func<DateOnly, AnchorMode, DateOnly?, DailyReportResult>? dailyReportProvider = null)
    {
        _startedAtUnixMs = startedAtUnixMs;
        _dbPath = dbPath;
        _isCaptureActive = isCaptureActive ?? (() => false);
        _snapshotProvider = snapshotProvider ?? EmptySnapshot;
        _statsProvider = statsProvider ?? EmptyStats;
        _appListProvider = appListProvider ?? (w => new AppListResult(w, Array.Empty<AppListEntry>()));
        _appDetailProvider = appDetailProvider ?? ((_, w, g) => new AppDetailResult(
            w, g, new AppListEntry(0, "", "", null, "Unchecked", false, 0, 0, 0, 0),
            Array.Empty<TrafficPoint>(), Array.Empty<SessionInfo>()));
        _connectionsProvider = connectionsProvider ?? ((_, w) => new ConnectionListResult(w, Array.Empty<ConnectionRow>()));
        _historyProvider = historyProvider ?? ((w, g) => new TrafficHistoryResult(w, g, Array.Empty<TrafficPoint>()));
        // Phase 5b — composition root in ZenVizorHostedService now always
        // supplies the real DailyReportRepository-backed provider. When no
        // provider is wired (legacy test harnesses), fall back to an empty
        // result rather than mock data.
        _dailyReportProvider = dailyReportProvider ?? EmptyDailyReport;
        _serviceVersion = typeof(ZenVizorIpcHandler).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? typeof(ZenVizorIpcHandler).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    private static ActivitySnapshot EmptySnapshot() => new(
        CapturedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        WindowSeconds: 0.0,
        Apps: Array.Empty<AppActivity>(),
        WanLocalBreakdown: ClassBreakdown.Empty);

    private static CaptureStats EmptyStats() => new(
        CapturedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ObservationsSeen: 0,
        ObservationsUnattributed: 0);

    private static DailyReportResult EmptyDailyReport(DateOnly date, AnchorMode anchor, DateOnly? specificDate) => new(
        Date: date,
        Anchor: anchor,
        AnchorSpecificDate: specificDate,
        Hero: new DailyReportHero(0, 0, 0, 0, 0, 0, 0),
        HourlyTraffic: Array.Empty<DailyReportHourPoint>(),
        TopApps: Array.Empty<DailyReportAppRow>(),
        UncommonTalkers: Array.Empty<DailyReportTalker>(),
        Notable: Array.Empty<DailyReportNotable>());

    public Task<NegotiateVersionResult> NegotiateVersionAsync(string clientVersion)
    {
        if (ProtocolVersion.IsCompatible(clientVersion))
        {
            return Task.FromResult(new NegotiateVersionResult(
                Accepted: true,
                ServerVersion: ProtocolVersion.Current,
                Reason: null));
        }

        return Task.FromResult(new NegotiateVersionResult(
            Accepted: false,
            ServerVersion: ProtocolVersion.Current,
            Reason: $"Client version '{clientVersion}' is not compatible with server major {ProtocolVersion.Major}."));
    }

    public Task<PingResult> PingAsync()
    {
        return Task.FromResult(new PingResult(
            Pong: "pong",
            ServerTimestampUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    public Task<ServiceStatusResult> GetServiceStatusAsync()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Task.FromResult(new ServiceStatusResult(
            ServiceName: ServiceConstants.ServiceName,
            Version: _serviceVersion,
            ProtocolVersion: ProtocolVersion.Current,
            StartedAtUnixMs: _startedAtUnixMs,
            UptimeMs: now - _startedAtUnixMs,
            DbPath: _dbPath,
            CaptureActive: _isCaptureActive()));
    }

    public Task<IpcEnvelope<ActivitySnapshot>> GetCurrentActivitySnapshotAsync()
    {
        var payload = _snapshotProvider();
        return Task.FromResult(new IpcEnvelope<ActivitySnapshot>(
            SchemaVersion: ActivitySnapshotSchemaVersion,
            Payload: payload));
    }

    public Task<IpcEnvelope<CaptureStats>> GetCaptureStatsAsync()
    {
        var payload = _statsProvider();
        return Task.FromResult(new IpcEnvelope<CaptureStats>(
            SchemaVersion: CaptureStatsSchemaVersion,
            Payload: payload));
    }

    public Task<IpcEnvelope<AppListResult>> GetAppListAsync(QueryWindow window)
    {
        var payload = _appListProvider(window);
        return Task.FromResult(new IpcEnvelope<AppListResult>(QuerySchemaVersion, payload));
    }

    public Task<IpcEnvelope<AppDetailResult>> GetAppDetailAsync(int appId, QueryWindow window, TrafficGrain grain)
    {
        var payload = _appDetailProvider(appId, window, grain);
        return Task.FromResult(new IpcEnvelope<AppDetailResult>(QuerySchemaVersion, payload));
    }

    public Task<IpcEnvelope<ConnectionListResult>> GetConnectionsAsync(int appId, QueryWindow window)
    {
        var payload = _connectionsProvider(appId, window);
        return Task.FromResult(new IpcEnvelope<ConnectionListResult>(QuerySchemaVersion, payload));
    }

    public Task<IpcEnvelope<TrafficHistoryResult>> GetTrafficHistoryAsync(QueryWindow window, TrafficGrain grain)
    {
        var payload = _historyProvider(window, grain);
        return Task.FromResult(new IpcEnvelope<TrafficHistoryResult>(QuerySchemaVersion, payload));
    }

    public Task<IpcEnvelope<DailyReportResult>> GetDailyReportAsync(
        DateOnly date,
        AnchorMode anchor,
        DateOnly? anchorSpecificDate)
    {
        var payload = _dailyReportProvider(date, anchor, anchorSpecificDate);
        return Task.FromResult(new IpcEnvelope<DailyReportResult>(DailyReportSchemaVersion, payload));
    }
}
