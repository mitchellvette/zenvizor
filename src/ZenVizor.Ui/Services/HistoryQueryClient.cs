// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using StreamJsonRpc;
using ZenVizor.Ipc.Client;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Wraps the Phase-4 history-query IPC behind a lazy, persistent
/// <see cref="ZenVizorPipeClient"/>. The pipe is connected on first call and
/// reused for the lifetime of this instance. On a connection-level failure
/// the inner client is dropped and the next call reconnects automatically.
/// </summary>
/// <remarks>
/// Concurrency: <see cref="StreamJsonRpc.JsonRpc"/> supports multiple in-flight
/// requests on a single connection, so the parallel calls in
/// <c>AppDetailPage.RefreshAsync</c> share one pipe. The semaphore only
/// serializes the lazy initialization, not the calls themselves.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class HistoryQueryClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private ZenVizorPipeClient? _client;

    public Task<AppListResult> GetAppListAsync(QueryWindow window, CancellationToken cancellationToken = default)
        => CallAsync(p => p.GetAppListAsync(window), nameof(AppListResult), IpcSchemaVersion.Query, cancellationToken);

    public Task<AppDetailResult> GetAppDetailAsync(int appId, QueryWindow window, TrafficGrain grain, CancellationToken cancellationToken = default)
        => CallAsync(p => p.GetAppDetailAsync(appId, window, grain), nameof(AppDetailResult), IpcSchemaVersion.Query, cancellationToken);

    public Task<ConnectionListResult> GetConnectionsAsync(int appId, QueryWindow window, CancellationToken cancellationToken = default)
        => CallAsync(p => p.GetConnectionsAsync(appId, window), nameof(ConnectionListResult), IpcSchemaVersion.Query, cancellationToken);

    public Task<TrafficHistoryResult> GetTrafficHistoryAsync(QueryWindow window, TrafficGrain grain, CancellationToken cancellationToken = default)
        => CallAsync(p => p.GetTrafficHistoryAsync(window, grain), nameof(TrafficHistoryResult), IpcSchemaVersion.Query, cancellationToken);

    public Task<DailyReportResult> GetDailyReportAsync(
        DateOnly date,
        AnchorMode anchor,
        DateOnly? anchorSpecificDate,
        CancellationToken cancellationToken = default)
        => CallAsync(p => p.GetDailyReportAsync(date, anchor, anchorSpecificDate),
            nameof(DailyReportResult), IpcSchemaVersion.DailyReport, cancellationToken);

    /// <summary>
    /// Drop the current pipe (if any) and re-establish a fresh one.
    /// Mirrors <c>AlertsClient.ForceReconnectAsync</c>: when the existing
    /// <c>_client</c> is non-null but its underlying pipe is dead,
    /// <see cref="EnsureProxyAsync"/> alone returns the stale proxy
    /// unchanged. This nukes the stale proxy first so the reconnect path
    /// actually runs.
    /// <para>
    /// Called by the four data pages
    /// (HistoryPage / ReportsPage / PerAppPage / AppDetailPage) on the
    /// MainWindow-fired <c>ServiceReconnected</c> event so their next
    /// <c>RefreshAsync</c> hits a fresh pipe rather than the stale one
    /// from before the service restart. Per the sprint plan A2 (Scope 3
    /// of Phase 6.1a), this responsibility moves to MainWindow when the
    /// query client is centralised at app scope — at which point the
    /// per-page calls here become redundant.
    /// </para>
    /// </summary>
    public async Task ForceReconnectAsync(CancellationToken cancellationToken = default)
    {
        await ResetAsync().ConfigureAwait(false);
        await EnsureProxyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> CallAsync<T>(
        Func<IZenVizorIpc, Task<IpcEnvelope<T>>> work,
        string payloadName,
        int expectedMinSchemaVersion,
        CancellationToken cancellationToken)
    {
        var proxy = await EnsureProxyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = await work(proxy).ConfigureAwait(false);
            // Floor-check the schema version: a service older than this UI
            // build would return a v1 payload the v2 record can't deserialize
            // cleanly. Surfacing IpcSchemaVersionException here lets the
            // page's catch block render a "service mismatch" banner instead
            // of a confusing "deserialization failed" stack.
            return envelope.UnwrapWithSchemaCheck(payloadName, expectedMinSchemaVersion);
        }
        catch (Exception ex) when (IsConnectionLost(ex))
        {
            // Connection died (service restart, pipe broken). Drop the inner
            // client so the next call reconnects; surface the error to the
            // page's catch block which displays a status banner.
            await ResetAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IZenVizorIpc> EnsureProxyAsync(CancellationToken cancellationToken)
    {
        var snapshot = _client;
        if (snapshot is not null) return snapshot.Proxy;

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is null)
            {
                _client = await ZenVizorPipeClient.ConnectAsync(
                    connectTimeout: TimeSpan.FromSeconds(2),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            return _client.Proxy;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ResetAsync()
    {
        ZenVizorPipeClient? toDispose;
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            toDispose = _client;
            _client = null;
        }
        finally
        {
            _connectLock.Release();
        }

        if (toDispose is not null)
        {
            try { await toDispose.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort dispose; underlying pipe may already be gone */ }
        }
    }

    internal static bool IsConnectionLost(Exception ex) =>
        ex is ConnectionLostException
        || ex is System.IO.IOException
        || ex is ObjectDisposedException
        // NamedPipeClientStream.ConnectAsync(timeoutMs, …) throws
        // System.TimeoutException when the pipe is unavailable for the
        // full timeout window — which is exactly the "service down"
        // case. Without this clause, the first failed call surfaces as
        // "Service disconnected" (the in-flight call hits an IOException
        // on the broken pipe and resets the client), but the second
        // call's 2-second reconnect attempt times out and falls through
        // to the generic catch as "Query failed (TimeoutException)".
        || ex is TimeoutException;

    public ValueTask DisposeAsync() => new(ResetAsync());
}

/// <summary>
/// Window-picker preset (Phase 4 Q9). <see cref="Label"/> is the long form
/// (History / App Detail surfaces display it directly); <see cref="Short"/>
/// is the shorthand (Per-App displays it inline with <see cref="Label"/> in
/// a per-item ToolTip). The rolling window is computed against current
/// wall-clock when <see cref="ToWindow"/> runs.
/// </summary>
internal sealed record WindowPreset(string Label, string Short, TimeSpan Span)
{
    public static readonly IReadOnlyList<WindowPreset> All = new[]
    {
        new WindowPreset("Last 1 hour",   "1h",  TimeSpan.FromHours(1)),
        new WindowPreset("Last 24 hours", "24h", TimeSpan.FromHours(24)),
        new WindowPreset("Last 7 days",   "7d",  TimeSpan.FromDays(7)),
        new WindowPreset("Last 30 days",  "30d", TimeSpan.FromDays(30)),
        new WindowPreset("Last 90 days",  "90d", TimeSpan.FromDays(90)),
    };

    public QueryWindow ToWindow()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new QueryWindow(now - (long)Span.TotalMilliseconds, now);
    }
}
