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
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event EventHandler<ActivitySnapshotUpdate>? SnapshotReceived;

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }
        // Fresh CTS each Start() so that a Stop()/Start() sequence (the cached
        // DashboardPage Unload→Load cycle) resumes polling instead of hitting
        // the disposed token from the previous run.
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
        // One persistent pipe across ticks; reconnects opportunistically after
        // any error. Cuts the steady-state cost of a fresh handshake per 2 s
        // tick — the dashboard banner is what surfaces a disconnect, so a
        // brief delay before next reconnect is fine.
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

                    var envelope = await client.Proxy.GetCurrentActivitySnapshotAsync()
                        .ConfigureAwait(false);

                    Raise(new ActivitySnapshotUpdate(
                        IsConnected: true,
                        Envelope: envelope,
                        FailureReason: null));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Raise(new ActivitySnapshotUpdate(
                        IsConnected: false,
                        Envelope: null,
                        FailureReason: ex.GetType().Name));

                    // Any error invalidates the connection. Drop the client
                    // so the next tick reconnects from scratch.
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

    private void Raise(ActivitySnapshotUpdate update) =>
        SnapshotReceived?.Invoke(this, update);

    public void Dispose() => Stop();
}

internal sealed record ActivitySnapshotUpdate(
    bool IsConnected,
    IpcEnvelope<ActivitySnapshot>? Envelope,
    string? FailureReason);
