// SPDX-License-Identifier: GPL-3.0-or-later

using StreamJsonRpc;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ipc.Server;

/// <summary>
/// Per-connection decorator that gates the envelope-tier RPC surface on
/// successful version negotiation. Phase-0 methods (Negotiate / Ping /
/// GetServiceStatus) stay callable before negotiation by design — see
/// <see cref="IpcEnvelope{T}"/> remarks. All other methods throw a typed
/// <see cref="IpcErrorCode.NegotiationRequired"/> fault until the client
/// has presented a compatible <see cref="ProtocolVersion"/>.
/// </summary>
/// <remarks>
/// One instance per pipe connection — the gate's "negotiated" flag is
/// connection-local, never shared across sessions. The same inner
/// <see cref="IZenVizorIpc"/> handler is reused across connections; only
/// the gate is per-connection.
/// </remarks>
internal sealed class NegotiationGate : IZenVizorIpc
{
    private readonly IZenVizorIpc _inner;
    private int _negotiated;
    private Action? _onMismatch;

    public NegotiationGate(IZenVizorIpc inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public bool IsNegotiated => Volatile.Read(ref _negotiated) != 0;

    /// <summary>
    /// Action invoked (fire-and-forget) after the gate observes a rejected
    /// negotiation. The pipe server wires this to dispose its JsonRpc session
    /// so a non-compatible client cannot linger on the connection.
    /// </summary>
    public void SetMismatchAction(Action onMismatch)
    {
        _onMismatch = onMismatch ?? throw new ArgumentNullException(nameof(onMismatch));
    }

    public async Task<NegotiateVersionResult> NegotiateVersionAsync(string clientVersion)
    {
        var result = await _inner.NegotiateVersionAsync(clientVersion).ConfigureAwait(false);
        if (result.Accepted)
        {
            Interlocked.Exchange(ref _negotiated, 1);
        }
        else
        {
            var action = _onMismatch;
            if (action is not null)
            {
                // Fire-and-forget: let the rejection result flush back to the
                // client before tearing the session down. A short delay is
                // enough because the response is queued by StreamJsonRpc as
                // soon as this method returns.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        action();
                    }
                    catch
                    {
                        // best-effort teardown
                    }
                });
            }
        }
        return result;
    }

    public Task<PingResult> PingAsync() => _inner.PingAsync();

    public Task<ServiceStatusResult> GetServiceStatusAsync() => _inner.GetServiceStatusAsync();

    public Task<IpcEnvelope<ActivitySnapshot>> GetCurrentActivitySnapshotAsync()
    {
        RequireNegotiated(nameof(GetCurrentActivitySnapshotAsync));
        return _inner.GetCurrentActivitySnapshotAsync();
    }

    public Task<IpcEnvelope<CaptureStats>> GetCaptureStatsAsync()
    {
        RequireNegotiated(nameof(GetCaptureStatsAsync));
        return _inner.GetCaptureStatsAsync();
    }

    public Task<IpcEnvelope<AppListResult>> GetAppListAsync(QueryWindow window)
    {
        RequireNegotiated(nameof(GetAppListAsync));
        return _inner.GetAppListAsync(window);
    }

    public Task<IpcEnvelope<AppDetailResult>> GetAppDetailAsync(int appId, QueryWindow window, TrafficGrain grain)
    {
        RequireNegotiated(nameof(GetAppDetailAsync));
        return _inner.GetAppDetailAsync(appId, window, grain);
    }

    public Task<IpcEnvelope<ConnectionListResult>> GetConnectionsAsync(int appId, QueryWindow window)
    {
        RequireNegotiated(nameof(GetConnectionsAsync));
        return _inner.GetConnectionsAsync(appId, window);
    }

    public Task<IpcEnvelope<TrafficHistoryResult>> GetTrafficHistoryAsync(QueryWindow window, TrafficGrain grain)
    {
        RequireNegotiated(nameof(GetTrafficHistoryAsync));
        return _inner.GetTrafficHistoryAsync(window, grain);
    }

    public Task<IpcEnvelope<DailyReportResult>> GetDailyReportAsync(
        DateOnly date,
        AnchorMode anchor,
        DateOnly? anchorSpecificDate)
    {
        RequireNegotiated(nameof(GetDailyReportAsync));
        return _inner.GetDailyReportAsync(date, anchor, anchorSpecificDate);
    }

    public Task<IpcEnvelope<AlertsResult>> GetAlertsAsync(AlertsFilter filter)
    {
        RequireNegotiated(nameof(GetAlertsAsync));
        return _inner.GetAlertsAsync(filter);
    }

    public Task DismissAlertAsync(long alertId)
    {
        RequireNegotiated(nameof(DismissAlertAsync));
        return _inner.DismissAlertAsync(alertId);
    }

    public Task RunRollupRulesNowAsync()
    {
        RequireNegotiated(nameof(RunRollupRulesNowAsync));
        return _inner.RunRollupRulesNowAsync();
    }

    public Task<IpcEnvelope<SettingsSnapshot>> GetSettingsAsync()
    {
        RequireNegotiated(nameof(GetSettingsAsync));
        return _inner.GetSettingsAsync();
    }

    public Task UpdateSettingsAsync(SettingsUpdate update)
    {
        RequireNegotiated(nameof(UpdateSettingsAsync));
        return _inner.UpdateSettingsAsync(update);
    }

    public Task<IpcEnvelope<WipeHistoryResult>> WipeHistoryAsync()
    {
        RequireNegotiated(nameof(WipeHistoryAsync));
        return _inner.WipeHistoryAsync();
    }

    private void RequireNegotiated(string methodName)
    {
        if (!IsNegotiated)
        {
            throw new LocalRpcException(
                $"Method '{methodName}' requires successful version negotiation. " +
                $"Call {nameof(NegotiateVersionAsync)} first.")
            {
                ErrorCode = IpcErrorCode.NegotiationRequired,
            };
        }
    }
}
