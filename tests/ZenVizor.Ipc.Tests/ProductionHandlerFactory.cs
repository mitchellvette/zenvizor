using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Service;

namespace ZenVizor.Ipc.Tests;

/// <summary>
/// Helper that constructs the production <see cref="ZenVizorIpcHandler"/>
/// (the service-side <see cref="IZenVizorIpc"/> implementation) with
/// in-memory providers — no SQLite, no capture pipeline. The test-hygiene
/// principle: exercise the real handler in-process so negotiation, validation,
/// and error-sanitization tests can't drift from production behavior.
/// </summary>
internal static class ProductionHandlerFactory
{
    public static IZenVizorIpc CreateDefault(
        Func<DateTimeOffset>? clock = null,
        long? maxWindowLookbackMs = null,
        Func<ActivitySnapshot>? snapshotProvider = null,
        Func<CaptureStats>? statsProvider = null,
        Func<QueryWindow, AppListResult>? appListProvider = null,
        Func<int, QueryWindow, TrafficGrain, AppDetailResult>? appDetailProvider = null,
        Func<int, QueryWindow, ConnectionListResult>? connectionsProvider = null,
        Func<QueryWindow, TrafficGrain, TrafficHistoryResult>? historyProvider = null,
        Func<DateOnly, AnchorMode, DateOnly?, DailyReportResult>? dailyReportProvider = null,
        Func<SettingsSnapshot>? settingsProvider = null,
        Action<SettingsUpdate>? settingsApplier = null,
        Func<WipeHistoryResult>? historyWiper = null)
    {
        var startedAt = (clock ?? (() => DateTimeOffset.UtcNow))().ToUnixTimeMilliseconds();
        return new ZenVizorIpcHandler(
            startedAtUnixMs: startedAt,
            dbPath: @"C:\fake\zenvizor.db",
            snapshotProvider: snapshotProvider,
            statsProvider: statsProvider,
            appListProvider: appListProvider,
            appDetailProvider: appDetailProvider,
            connectionsProvider: connectionsProvider,
            historyProvider: historyProvider,
            dailyReportProvider: dailyReportProvider,
            settingsProvider: settingsProvider,
            settingsApplier: settingsApplier,
            historyWiper: historyWiper,
            clock: clock,
            maxWindowLookbackMs: maxWindowLookbackMs);
    }
}
