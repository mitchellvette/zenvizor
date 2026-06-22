// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using ZenVizor.Ipc.Client;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Wraps the Phase-6 Alerts IPC behind a lazy, persistent
/// <see cref="ZenVizorPipeClient"/>. Owns the server-push subscription —
/// implements <see cref="IAlertNotifications"/> directly and registers
/// itself as the pipe client's notification target so the service's
/// <c>NotifyAsync</c> push of <c>OnAlertRaisedAsync</c> dispatches into
/// this object's interface method, which raises the public
/// <see cref="AlertRaised"/> event.
/// <para>
/// Single-instance lifetime: one <see cref="AlertsClient"/> per UI process,
/// owned by <see cref="ZenVizor.Ui.MainWindow"/>. Both the nav-rail badge
/// (MainWindow) and the AlertsPage view-model subscribe to the same
/// instance's events so they see the same alerts at the same moment.
/// </para>
/// <para>
/// Notification thread: <see cref="AlertRaised"/> fires on whatever thread
/// StreamJsonRpc invokes the callback on (typically a thread-pool thread).
/// Consumers MUST marshal to the UI dispatcher before touching visual-tree
/// state. Mirrors the
/// <see cref="ActivitySnapshotPoller.SnapshotReceived"/> pattern that
/// MainWindow already uses.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AlertsClient : IAlertNotifications, IAsyncDisposable
{
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private ZenVizorPipeClient? _client;

    /// <summary>
    /// Fires when the service pushes an <c>AlertRaised</c> notification.
    /// Not marshalled — consumers are responsible for Dispatcher.Invoke.
    /// </summary>
    public event EventHandler<AlertDto>? AlertRaised;

    /// <summary>
    /// Pre-connect to the service so the push subscription is in place
    /// before the first user-driven query. MainWindow calls this from
    /// OnLoaded so the nav-rail badge can receive AlertRaised pushes
    /// even if the user never opens the Alerts page.
    /// </summary>
    public Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
        => EnsureProxyAsync(cancellationToken);

    /// <summary>
    /// Drop the current pipe (if any) and re-establish a fresh one,
    /// including re-registering the notification target so the
    /// <see cref="AlertRaised"/> push subscription survives a service
    /// restart. <see cref="EnsureConnectedAsync"/> alone is NOT
    /// sufficient — when the existing <c>_client</c> is non-null but its
    /// underlying pipe is dead, EnsureProxyAsync returns the stale proxy
    /// unchanged. ForceReconnectAsync nukes the stale proxy first so the
    /// reconnect path actually runs.
    /// <para>
    /// Called by <c>MainWindow.OnStatusChanged</c> on the
    /// disconnected→connected transition detected by
    /// <c>ServiceStatusPoller</c>. Between service start and this
    /// reconnect, alerts raised on the service are broadcast to zero
    /// subscribers and effectively lost from the UI's push stream;
    /// MainWindow follows the reconnect with a
    /// <c>ServiceReconnected</c> event so subscribing pages can run a
    /// fresh RefreshAsync to pick them up from the DB.
    /// </para>
    /// </summary>
    public async Task ForceReconnectAsync(CancellationToken cancellationToken = default)
    {
        await ResetAsync().ConfigureAwait(false);
        await EnsureProxyAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<AlertsResult> GetAlertsAsync(AlertsFilter filter, CancellationToken cancellationToken = default)
        => CallAsync(p => p.GetAlertsAsync(filter),
                     nameof(AlertsResult), IpcSchemaVersion.Alerts, cancellationToken);

    public Task DismissAlertAsync(long alertId, CancellationToken cancellationToken = default)
        => CallVoidAsync(p => p.DismissAlertAsync(alertId), cancellationToken);

    // ---- IAlertNotifications --------------------------------------------------
    //
    // StreamJsonRpc dispatches the server's NotifyAsync("OnAlertRaisedAsync",
    // alert) call into this method on the client side. Explicit interface
    // implementation so the method isn't a tempting "regular" surface on the
    // client class; consumers raise their interest via the AlertRaised event.

    Task IAlertNotifications.OnAlertRaisedAsync(AlertDto alert)
    {
        AlertRaised?.Invoke(this, alert);
        return Task.CompletedTask;
    }

    // ---- Plumbing -------------------------------------------------------------

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
                // Pass `this` as the notification target so OnAlertRaisedAsync
                // pushes from the server dispatch into our explicit-interface
                // implementation; the AlertRaised event fires from there.
                _client = await ZenVizorPipeClient.ConnectAsync(
                    connectTimeout: TimeSpan.FromSeconds(2),
                    notificationTarget: this,
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

    public ValueTask DisposeAsync() => new(ResetAsync());
}
