using System.Reflection;
using StreamJsonRpc;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Service;

/// <summary>
/// The service-side implementation of <see cref="IZenVizorIpc"/>. Composes
/// the in-memory snapshot path (Phase 3) with the SQLite-backed history
/// query path (Phase 4) and the daily-report aggregator (Phase 5) behind
/// per-RPC provider delegates that the hosted service wires from
/// <see cref="ZenVizorHostedService.StartAsync"/>. The handler itself is
/// concerned only with envelope stamping, argument validation, and the
/// negotiation gate — the providers own the data plane.
/// </summary>
internal sealed class ZenVizorIpcHandler : IZenVizorIpc
{
    // Schema versions live in ZenVizor.Ipc.Contracts.IpcSchemaVersion so the
    // server, the UI HistoryQueryClient floor-check, and zvctl all reference
    // a single source of truth — when a payload's shape bumps, only that
    // constant changes and every consumer's expectation tracks it.

    /// <summary>
    /// Default upper bound on how far back <see cref="QueryWindow.FromUnixMs"/>
    /// is allowed to reach. Set comfortably past the daily-tier retention
    /// default (365 d) so legitimate "last year" queries pass while
    /// pathological / nonsense windows (epoch, far-past) get rejected at
    /// the RPC boundary instead of triggering a full-table scan.
    /// </summary>
    private const long DefaultMaxWindowLookbackMs = 400L * 86_400_000L;

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
    private readonly Func<AlertsFilter, AlertsResult> _alertsProvider;
    private readonly Func<long, bool> _alertDismisser;
    private readonly Func<DateTimeOffset> _clock;
    private readonly long _maxWindowLookbackMs;
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
        Func<DateOnly, AnchorMode, DateOnly?, DailyReportResult>? dailyReportProvider = null,
        Func<AlertsFilter, AlertsResult>? alertsProvider = null,
        Func<long, bool>? alertDismisser = null,
        Func<DateTimeOffset>? clock = null,
        long? maxWindowLookbackMs = null)
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
        // Phase 6 Alerts — the real provider + dismisser wire to the alerts
        // repository when storage + producer ship. Until then the handler
        // returns an empty active set and treats dismiss as a no-op (idempotent
        // per the brief §3.5 contract).
        _alertsProvider = alertsProvider ?? EmptyAlerts;
        _alertDismisser = alertDismisser ?? (_ => false);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _maxWindowLookbackMs = maxWindowLookbackMs ?? DefaultMaxWindowLookbackMs;
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

    private static AlertsResult EmptyAlerts(AlertsFilter filter) => new(
        Filter: filter,
        Alerts: Array.Empty<AlertDto>(),
        HasMore: false);

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
            SchemaVersion: IpcSchemaVersion.ActivitySnapshot,
            Payload: payload));
    }

    public Task<IpcEnvelope<CaptureStats>> GetCaptureStatsAsync()
    {
        var payload = _statsProvider();
        return Task.FromResult(new IpcEnvelope<CaptureStats>(
            SchemaVersion: IpcSchemaVersion.CaptureStats,
            Payload: payload));
    }

    public Task<IpcEnvelope<AppListResult>> GetAppListAsync(QueryWindow window)
    {
        ValidateWindow(window);
        var payload = _appListProvider(window);
        return Task.FromResult(new IpcEnvelope<AppListResult>(IpcSchemaVersion.Query, payload));
    }

    public Task<IpcEnvelope<AppDetailResult>> GetAppDetailAsync(int appId, QueryWindow window, TrafficGrain grain)
    {
        ValidateAppId(appId);
        ValidateWindow(window);
        ValidateGrain(grain);
        var payload = _appDetailProvider(appId, window, grain);
        return Task.FromResult(new IpcEnvelope<AppDetailResult>(IpcSchemaVersion.Query, payload));
    }

    public Task<IpcEnvelope<ConnectionListResult>> GetConnectionsAsync(int appId, QueryWindow window)
    {
        ValidateAppId(appId);
        ValidateWindow(window);
        var payload = _connectionsProvider(appId, window);
        return Task.FromResult(new IpcEnvelope<ConnectionListResult>(IpcSchemaVersion.Query, payload));
    }

    public Task<IpcEnvelope<TrafficHistoryResult>> GetTrafficHistoryAsync(QueryWindow window, TrafficGrain grain)
    {
        ValidateWindow(window);
        ValidateGrain(grain);
        var payload = _historyProvider(window, grain);
        return Task.FromResult(new IpcEnvelope<TrafficHistoryResult>(IpcSchemaVersion.Query, payload));
    }

    public Task<IpcEnvelope<DailyReportResult>> GetDailyReportAsync(
        DateOnly date,
        AnchorMode anchor,
        DateOnly? anchorSpecificDate)
    {
        var payload = _dailyReportProvider(date, anchor, anchorSpecificDate);
        return Task.FromResult(new IpcEnvelope<DailyReportResult>(IpcSchemaVersion.DailyReport, payload));
    }

    public Task<IpcEnvelope<AlertsResult>> GetAlertsAsync(AlertsFilter filter)
    {
        ValidateAlertsFilter(filter);
        var payload = _alertsProvider(filter);
        return Task.FromResult(new IpcEnvelope<AlertsResult>(IpcSchemaVersion.Alerts, payload));
    }

    public Task DismissAlertAsync(long alertId)
    {
        ValidateAlertId(alertId);
        // Idempotent — the dismisser returns false when the row is already
        // dismissed or absent. The brief §3.5 + §8.2 contract is "one click,
        // no confirm"; surfacing a double-click as an error would leak the
        // dismissed-already state to the UI as a faulted task. Silent
        // success is the contract.
        _alertDismisser(alertId);
        return Task.CompletedTask;
    }

    // ---- Argument validation ------------------------------------------------
    //
    // Every Phase-4 query RPC validates its inputs and returns a typed
    // IpcErrorCode.InvalidArgument fault on rejection. Throwing
    // LocalRpcException keeps the failure shape consistent with the negotiation
    // gate and avoids the sanitizer collapsing a real argument problem into a
    // generic "internal error" the client can't act on.

    private static void ValidateAppId(int appId)
    {
        if (appId <= 0)
        {
            throw InvalidArgument($"appId must be positive (received {appId}).");
        }
    }

    private void ValidateWindow(QueryWindow window)
    {
        if (window is null)
        {
            throw InvalidArgument("window is required.");
        }

        if (window.ToUnixMs < window.FromUnixMs)
        {
            throw InvalidArgument(
                $"window.ToUnixMs ({window.ToUnixMs}) must be >= window.FromUnixMs ({window.FromUnixMs}).");
        }

        var nowMs = _clock().ToUnixTimeMilliseconds();
        var earliestAllowedMs = nowMs - _maxWindowLookbackMs;
        if (window.FromUnixMs < earliestAllowedMs)
        {
            throw InvalidArgument(
                $"window.FromUnixMs ({window.FromUnixMs}) precedes the retention horizon " +
                $"({earliestAllowedMs}).");
        }
    }

    private static void ValidateGrain(TrafficGrain grain)
    {
        if (!Enum.IsDefined(typeof(TrafficGrain), grain))
        {
            throw InvalidArgument($"TrafficGrain value {(int)grain} is not defined.");
        }
    }

    private static void ValidateAlertId(long alertId)
    {
        if (alertId <= 0)
        {
            throw InvalidArgument($"alertId must be positive (received {alertId}).");
        }
    }

    private static void ValidateAlertsFilter(AlertsFilter filter)
    {
        if (filter is null)
        {
            throw InvalidArgument("filter is required.");
        }
        if (!Enum.IsDefined(typeof(AlertState), filter.State))
        {
            throw InvalidArgument($"AlertsFilter.State value {(int)filter.State} is not defined.");
        }
        if (filter.MaxRows <= 0)
        {
            throw InvalidArgument($"AlertsFilter.MaxRows must be positive (received {filter.MaxRows}).");
        }
    }

    private static LocalRpcException InvalidArgument(string message) =>
        new(message) { ErrorCode = IpcErrorCode.InvalidArgument };
}
