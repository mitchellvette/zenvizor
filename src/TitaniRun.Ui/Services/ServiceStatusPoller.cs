using System.Runtime.Versioning;
using TitaniRun.Ipc.Client;

namespace TitaniRun.Ui.Services;

/// <summary>
/// Polls the TitaniRun service over IPC at a modest cadence and raises
/// <see cref="StatusChanged"/> with the latest connectivity state.
/// Phase 0: used to drive the bottom-bar status indicator.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ServiceStatusPoller : IDisposable
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public event EventHandler<ServiceStatusUpdate>? StatusChanged;

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
                await using var client = await TitaniRunPipeClient.ConnectAsync(
                    connectTimeout: TimeSpan.FromSeconds(2),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var status = await client.Proxy.GetServiceStatusAsync().ConfigureAwait(false);

                Raise(new ServiceStatusUpdate(
                    IsConnected: true,
                    ServiceVersion: status.Version,
                    ProtocolVersion: status.ProtocolVersion,
                    Message: "connected"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Raise(new ServiceStatusUpdate(
                    IsConnected: false,
                    ServiceVersion: null,
                    ProtocolVersion: null,
                    Message: $"disconnected ({ex.GetType().Name})"));
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

    private void Raise(ServiceStatusUpdate update) =>
        StatusChanged?.Invoke(this, update);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

internal sealed record ServiceStatusUpdate(
    bool IsConnected,
    string? ServiceVersion,
    string? ProtocolVersion,
    string Message);
