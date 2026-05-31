using System.IO.Pipes;
using System.Runtime.Versioning;
using StreamJsonRpc;
using TitaniRun.Ipc.Contracts;
using TitaniRun.Ipc.Contracts.Dto;

namespace TitaniRun.Ipc.Client;

/// <summary>
/// Convenience client that opens the TitaniRun named pipe, performs version
/// negotiation, and returns a ready-to-use <see cref="ITitaniRunIpc"/> proxy.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TitaniRunPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly JsonRpc _rpc;

    private TitaniRunPipeClient(NamedPipeClientStream pipe, JsonRpc rpc, ITitaniRunIpc proxy)
    {
        _pipe = pipe;
        _rpc = rpc;
        Proxy = proxy;
    }

    public ITitaniRunIpc Proxy { get; }

    /// <summary>
    /// Connect to the TitaniRun service named pipe and negotiate the wire-protocol
    /// version. Throws <see cref="IpcVersionMismatchException"/> if the server
    /// rejects the client's <see cref="ProtocolVersion.Current"/>.
    /// </summary>
    public static async Task<TitaniRunPipeClient> ConnectAsync(
        string? pipeName = null,
        TimeSpan? connectTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var name = pipeName ?? IpcConstants.PipeName;
        var timeoutMs = (int)(connectTimeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;

        var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: name,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }

        var (proxy, rpc) = TitaniRunRpcClient.Attach(pipe);

        NegotiateVersionResult negotiation;
        try
        {
            negotiation = await proxy.NegotiateVersionAsync(ProtocolVersion.Current)
                .ConfigureAwait(false);
        }
        catch
        {
            rpc.Dispose();
            pipe.Dispose();
            throw;
        }

        if (!negotiation.Accepted)
        {
            var reason = negotiation.Reason ?? "version rejected by server";
            rpc.Dispose();
            pipe.Dispose();
            throw new IpcVersionMismatchException(
                ProtocolVersion.Current,
                negotiation.ServerVersion,
                reason);
        }

        return new TitaniRunPipeClient(pipe, rpc, proxy);
    }

    public async ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class IpcVersionMismatchException : Exception
{
    public IpcVersionMismatchException(string clientVersion, string serverVersion, string reason)
        : base($"IPC version mismatch: client {clientVersion}, server {serverVersion}. {reason}")
    {
        ClientVersion = clientVersion;
        ServerVersion = serverVersion;
    }

    public string ClientVersion { get; }
    public string ServerVersion { get; }
}
