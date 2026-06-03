using System.Runtime.Versioning;
using ZenVizor.Ipc.Client;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Polls the ZenVizor service over IPC at a modest cadence and raises
/// <see cref="StatusChanged"/> with the latest connectivity state.
/// Phase 0: used to drive the bottom-bar status indicator.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ServiceStatusPoller : IDisposable
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event EventHandler<ServiceStatusUpdate>? StatusChanged;

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }
        // Fresh CTS per Start() so a Stop()/Start() sequence resumes polling
        // — mirrors ActivitySnapshotPoller. Currently MainWindow only starts /
        // disposes once, but the latent bug surfaces if the lifetime ever changes.
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        _loop = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // Persistent pipe across ticks; reconnects on any error. See
        // ActivitySnapshotPoller for the same rationale.
        ZenVizorPipeClient? client = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    client ??= await ZenVizorPipeClient.ConnectAsync(
                        connectTimeout: TimeSpan.FromSeconds(2),
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    var status = await client.Proxy.GetServiceStatusAsync().ConfigureAwait(false);

                    Raise(new ServiceStatusUpdate(
                        IsConnected: true,
                        ServiceVersion: status.Version,
                        ProtocolVersion: status.ProtocolVersion,
                        Message: "connected"));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Raise(new ServiceStatusUpdate(
                        IsConnected: false,
                        ServiceVersion: null,
                        ProtocolVersion: null,
                        Message: $"disconnected ({ex.GetType().Name})"));

                    if (client is not null)
                    {
                        try { await client.DisposeAsync().ConfigureAwait(false); }
                        catch { }
                        client = null;
                    }
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
        finally
        {
            if (client is not null)
            {
                try { await client.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
        }
    }

    private void Raise(ServiceStatusUpdate update) =>
        StatusChanged?.Invoke(this, update);

    public void Dispose() => Stop();
}

internal sealed record ServiceStatusUpdate(
    bool IsConnected,
    string? ServiceVersion,
    string? ProtocolVersion,
    string Message);
