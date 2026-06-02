using System.Runtime.Versioning;
using ZenVizor.Ipc.Client;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Polls <see cref="IZenVizorIpc.GetCurrentActivitySnapshotAsync"/> at a fast
/// cadence and raises <see cref="SnapshotReceived"/> with the latest payload.
/// Sibling of <see cref="ServiceStatusPoller"/>; intentionally has no backoff
/// — the dashboard banner indicates disconnect, and the 2 s cadence keeps
/// reconnect attempts cheap.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ActivitySnapshotPoller : IDisposable
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public event EventHandler<ActivitySnapshotUpdate>? SnapshotReceived;

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var client = await ZenVizorPipeClient.ConnectAsync(
                    connectTimeout: TimeSpan.FromSeconds(2),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var envelope = await client.Proxy.GetCurrentActivitySnapshotAsync()
                    .ConfigureAwait(false);

                Raise(new ActivitySnapshotUpdate(
                    IsConnected: true,
                    Envelope: envelope,
                    FailureReason: null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Raise(new ActivitySnapshotUpdate(
                    IsConnected: false,
                    Envelope: null,
                    FailureReason: ex.GetType().Name));
            }

            try
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Raise(ActivitySnapshotUpdate update) =>
        SnapshotReceived?.Invoke(this, update);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

internal sealed record ActivitySnapshotUpdate(
    bool IsConnected,
    IpcEnvelope<ActivitySnapshot>? Envelope,
    string? FailureReason);
