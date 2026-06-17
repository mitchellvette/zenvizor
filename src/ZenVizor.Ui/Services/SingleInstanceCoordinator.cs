using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Owns the per-user named <see cref="Mutex"/> and the UI-to-UI named-pipe
/// listener that together enforce a single ZenVizor.Ui process per logged-in
/// user. <see cref="App.OnStartup"/> calls <see cref="TryClaimPrimary"/>;
/// the second launch falls through to <see cref="SignalExisting"/>, which
/// asks the primary to surface its window and then exits cleanly.
/// </summary>
/// <remarks>
/// <para>
/// The mutex uses a per-session local name (no <c>Global\</c> prefix) so the
/// UI process stays inside its own user session. Multi-user terminal-server
/// scenarios each get their own primary, which is the intended behaviour —
/// the UI is per-user state, not machine-wide.
/// </para>
/// <para>
/// Signalling rides a dedicated pipe (<c>ZenVizor.Ui.SingleInstance.v1</c>),
/// explicitly distinct from the service pipe (<c>ZenVizor.Ipc.v1</c>). The
/// service pipe is owned by SYSTEM with INTERACTIVE ACL grants; this one is
/// owned by the current user only. Wire shape: client connects, sends a
/// single byte (<c>0x01</c> = "show"), server raises
/// <see cref="ShowRequested"/> on its registered dispatcher.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class SingleInstanceCoordinator : IDisposable
{
    // "Local\" mutex namespace; one primary UI per logged-in user session.
    private const string MutexName = "Local\\ZenVizor.UI.SingleInstance";
    internal const string PipeName = "ZenVizor.Ui.SingleInstance.v1";
    private const byte ShowCommand = 0x01;

    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;

    /// <summary>
    /// Raised on the listener task's thread when a second instance asks the
    /// primary to surface. Subscribers must marshal to the UI dispatcher.
    /// </summary>
    public event EventHandler? ShowRequested;

    /// <summary>
    /// Attempts to claim the singleton mutex. Returns true if this process
    /// is now the primary (it should continue startup and call
    /// <see cref="StartListener"/>); false if another instance already holds
    /// it (the caller should signal that instance and shut down).
    /// </summary>
    public bool TryClaimPrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Begins listening on the UI-to-UI named pipe for "show" signals from
    /// secondary launches. Idempotent — repeated calls are a no-op.
    /// </summary>
    public void StartListener()
    {
        if (_listenerTask is not null) return;

        _listenerCts = new CancellationTokenSource();
        _listenerTask = Task.Run(() => RunListenerLoopAsync(_listenerCts.Token));
    }

    private async Task RunListenerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var buffer = new byte[1];
                var read = await server.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);
                if (read == 1 && buffer[0] == ShowCommand)
                {
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Pipe error (client hung up mid-handshake, etc.) — restart
                // the listener so the next instance can still find us.
            }
        }
    }

    /// <summary>
    /// Connects to the primary instance and asks it to surface. Called by
    /// the secondary launch after <see cref="TryClaimPrimary"/> returned
    /// false. Best-effort: if the primary's pipe isn't responsive within
    /// the timeout the secondary still exits — leaving a stranded second
    /// instance running would be worse than leaving the primary hidden.
    /// </summary>
    public static void SignalExisting(TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                serverName: ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.None);
            client.Connect((int)timeout.TotalMilliseconds);
            client.WriteByte(ShowCommand);
            client.Flush();
        }
        catch
        {
            // Primary unreachable — secondary still exits to honour the
            // single-instance contract.
        }
    }

    public void Dispose()
    {
        try
        {
            _listenerCts?.Cancel();
        }
        catch { }
        try
        {
            _listenerCts?.Dispose();
        }
        catch { }
        _listenerCts = null;
        _listenerTask = null;

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch { }
        try
        {
            _mutex?.Dispose();
        }
        catch { }
        _mutex = null;
    }
}
