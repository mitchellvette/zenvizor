using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TitaniRun.Ipc.Contracts;

namespace TitaniRun.Ipc.Server;

/// <summary>
/// Listens on the TitaniRun named pipe. Each accepted connection gets its own
/// JsonRpc session that dispatches to the supplied <see cref="ITitaniRunIpc"/>.
/// The pipe is ACL'd to SYSTEM + Administrators full and Interactive users
/// read/write — anonymous and remote/network principals are denied by Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TitaniRunPipeServer : IAsyncDisposable
{
    private readonly ITitaniRunIpc _handler;
    private readonly ILogger _logger;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public TitaniRunPipeServer(
        ITitaniRunIpc handler,
        ILogger<TitaniRunPipeServer>? logger = null,
        string? pipeName = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _pipeName = pipeName ?? IpcConstants.PipeName;
    }

    public void Start()
    {
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("Pipe server is already running.");
        }

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _logger.LogInformation("Pipe server listening on \\\\.\\pipe\\{PipeName}.", _pipeName);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateListenerInstance();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipe server accept failed; will retry.");
                pipe?.Dispose();
                continue;
            }

            // Hand off the connected pipe to a JsonRpc session. The accept loop
            // creates a fresh listener instance immediately on the next iteration.
            _ = HandleConnectionAsync(pipe, cancellationToken);
        }
    }

    private NamedPipeServerStream CreateListenerInstance()
    {
        var security = BuildPipeSecurity();

        return NamedPipeServerStreamAcl.Create(
            pipeName: _pipeName,
            direction: PipeDirection.InOut,
            maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Pipe client connected.");
            using var rpc = TitaniRunRpcHost.Host(pipe, _handler);
            await rpc.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pipe session ended abnormally.");
        }
        finally
        {
            pipe.Dispose();
            _logger.LogDebug("Pipe client disconnected.");
        }
    }

    private static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();

        // SYSTEM — full control
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // BUILTIN\Administrators — full control
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // INTERACTIVE — connect/read/write so the desktop UI and trctl work,
        // but remote/network/anonymous principals get no rule and so no access.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        return security;
    }

    public async ValueTask DisposeAsync()
    {
        if (_acceptLoop is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch
        {
            // best-effort shutdown
        }
        _cts.Dispose();
        _acceptLoop = null;
    }
}
