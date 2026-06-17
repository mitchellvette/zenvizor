using System.Runtime.Versioning;
using StreamJsonRpc;
using ZenVizor.Ipc.Client;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Wraps the Phase-6.2 settings IPC behind a lazy, persistent
/// <see cref="ZenVizorPipeClient"/>. Mirrors the
/// <see cref="HistoryQueryClient"/> connection pattern — one pipe per
/// instance, reset on connection-lost so the next call reconnects.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SettingsClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private ZenVizorPipeClient? _client;

    public Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken = default)
        => CallAsync(p => p.GetSettingsAsync(),
                     nameof(SettingsSnapshot), IpcSchemaVersion.Settings, cancellationToken);

    public Task UpdateSettingsAsync(SettingsUpdate update, CancellationToken cancellationToken = default)
        => CallVoidAsync(p => p.UpdateSettingsAsync(update), cancellationToken);

    public Task<WipeHistoryResult> WipeHistoryAsync(CancellationToken cancellationToken = default)
        => CallAsync(p => p.WipeHistoryAsync(),
                     nameof(WipeHistoryResult), IpcSchemaVersion.Settings, cancellationToken);

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
            return envelope.UnwrapWithSchemaCheck(payloadName, expectedMinSchemaVersion);
        }
        catch (Exception ex) when (HistoryQueryClient.IsConnectionLost(ex))
        {
            await ResetAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task CallVoidAsync(
        Func<IZenVizorIpc, Task> work,
        CancellationToken cancellationToken)
    {
        var proxy = await EnsureProxyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await work(proxy).ConfigureAwait(false);
        }
        catch (Exception ex) when (HistoryQueryClient.IsConnectionLost(ex))
        {
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
            catch { /* best-effort dispose */ }
        }
    }

    public ValueTask DisposeAsync() => new(ResetAsync());

    /// <summary>
    /// True when an exception came back from the named pipe because the
    /// service binary doesn't expose the requested RPC method — i.e. the
    /// service was built before this UI's Phase 6.2 contract. Callers
    /// surface this as a calm "service is older than UI" banner rather
    /// than a generic save failure.
    /// </summary>
    public static bool IsMethodNotFound(Exception ex) =>
        ex is RemoteMethodNotFoundException;
}
