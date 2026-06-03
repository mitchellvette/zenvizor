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
        => CallAsync(p => p.GetAppListAsync(window), cancellationToken);

    public Task<AppDetailResult> GetAppDetailAsync(int appId, QueryWindow window, TrafficGrain grain, CancellationToken cancellationToken = default)
        => CallAsync(p => p.GetAppDetailAsync(appId, window, grain), cancellationToken);

    public Task<ConnectionListResult> GetConnectionsAsync(int appId, QueryWindow window, CancellationToken cancellationToken = default)
        => CallAsync(p => p.GetConnectionsAsync(appId, window), cancellationToken);

    public Task<TrafficHistoryResult> GetTrafficHistoryAsync(QueryWindow window, TrafficGrain grain, CancellationToken cancellationToken = default)
        => CallAsync(p => p.GetTrafficHistoryAsync(window, grain), cancellationToken);

    private async Task<T> CallAsync<T>(
        Func<IZenVizorIpc, Task<IpcEnvelope<T>>> work,
        CancellationToken cancellationToken)
    {
        var proxy = await EnsureProxyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = await work(proxy).ConfigureAwait(false);
            return envelope.Payload;
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

    private static bool IsConnectionLost(Exception ex) =>
        ex is ConnectionLostException
        || ex is System.IO.IOException
        || ex is ObjectDisposedException;

    public ValueTask DisposeAsync() => new(ResetAsync());
}

/// <summary>
/// Window-picker preset (Phase 4 Q9). The UI displays <see cref="Label"/>; the
/// rolling window is computed against current wall-clock when <see cref="ToWindow"/> runs.
/// </summary>
internal sealed record WindowPreset(string Label, TimeSpan Span)
{
    public static readonly IReadOnlyList<WindowPreset> All = new[]
    {
        new WindowPreset("Last 1 hour",  TimeSpan.FromHours(1)),
        new WindowPreset("Last 24 hours", TimeSpan.FromHours(24)),
        new WindowPreset("Last 7 days",   TimeSpan.FromDays(7)),
        new WindowPreset("Last 30 days",  TimeSpan.FromDays(30)),
        new WindowPreset("Last 90 days",  TimeSpan.FromDays(90)),
    };

    public QueryWindow ToWindow()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new QueryWindow(now - (long)Span.TotalMilliseconds, now);
    }
}
