// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ipc.Server;

/// <summary>
/// Tracks every connected <see cref="JsonRpc"/> session and broadcasts alert
/// notifications to all of them. The Phase-6 alert producer calls
/// <see cref="BroadcastAlertRaisedAsync"/> once per raised alert; this class
/// fans the payload out to every UI + <c>zvctl</c> client currently connected
/// to the named pipe.
/// <para>
/// This is the first server-to-client push surface in ZenVizor. The wire
/// pattern is StreamJsonRpc's canonical "client-as-target":
/// </para>
/// <list type="number">
///   <item><description>The pipe server hands each new connection's
///   <see cref="JsonRpc"/> to <see cref="Register"/> after the handler is
///   attached and listening.</description></item>
///   <item><description>The producer raises an alert and invokes
///   <see cref="BroadcastAlertRaisedAsync"/>, which calls
///   <c>JsonRpc.NotifyAsync</c> on every registered subscriber with the
///   method name <see cref="IAlertNotifications.OnAlertRaisedAsync"/>.
///   StreamJsonRpc dispatches the notification to the client-side local
///   RPC target the UI registered when it constructed its pipe
///   client.</description></item>
/// </list>
/// <para>
/// Thread safety: the subscriber set is guarded by a single lock; broadcasts
/// snapshot the set under the lock and then send notifications outside it
/// so a slow / disconnected subscriber cannot stall others. The notify
/// call itself is wrapped in try/catch — a broken pipe surfaces here as
/// an exception we log and swallow; the <c>Disconnected</c> event will
/// remove the dead subscriber once StreamJsonRpc observes the broken
/// connection.
/// </para>
/// </summary>
public sealed class AlertBroadcaster
{
    private readonly object _lock = new();
    private readonly HashSet<JsonRpc> _subscribers = new();
    private readonly ILogger _logger;

    public AlertBroadcaster(ILogger<AlertBroadcaster>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Adds a connected JsonRpc session to the broadcast set. Auto-unregisters
    /// when StreamJsonRpc raises <see cref="JsonRpc.Disconnected"/>.
    /// </summary>
    public void Register(JsonRpc rpc)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        lock (_lock)
        {
            _subscribers.Add(rpc);
        }
        rpc.Disconnected += (_, _) => Unregister(rpc);
    }

    /// <summary>
    /// Removes a session from the broadcast set. Idempotent — safe to call
    /// even if the session is already gone.
    /// </summary>
    public void Unregister(JsonRpc rpc)
    {
        if (rpc is null) return;
        lock (_lock)
        {
            _subscribers.Remove(rpc);
        }
    }

    /// <summary>
    /// Number of subscribers currently registered. Exposed for diagnostics
    /// and tests; not used on the hot path.
    /// </summary>
    public int SubscriberCount
    {
        get
        {
            lock (_lock) return _subscribers.Count;
        }
    }

    /// <summary>
    /// Fan an AlertRaised notification out to every connected subscriber.
    /// Non-fatal per-subscriber send failures are logged at Warning and
    /// the broadcast continues — a slow or broken pipe must not stall the
    /// alert pipeline or block other clients from receiving the same alert.
    /// </summary>
    public async Task BroadcastAlertRaisedAsync(AlertDto alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        JsonRpc[] snapshot;
        lock (_lock)
        {
            snapshot = _subscribers.ToArray();
        }

        if (snapshot.Length == 0)
        {
            return;
        }

        foreach (var rpc in snapshot)
        {
            try
            {
                await rpc.NotifyAsync(
                    nameof(IAlertNotifications.OnAlertRaisedAsync),
                    alert).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to broadcast AlertRaised (alert {AlertId}) to a subscriber; " +
                    "the Disconnected event will clean it up if the pipe is broken.",
                    alert.AlertId);
            }
        }
    }
}
