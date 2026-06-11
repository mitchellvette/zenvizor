using System.IO.Pipes;
using System.Runtime.Versioning;
using StreamJsonRpc;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ipc.Client;

/// <summary>
/// Convenience client that opens the ZenVizor named pipe, performs version
/// negotiation, and returns a ready-to-use <see cref="IZenVizorIpc"/> proxy.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ZenVizorPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly JsonRpc _rpc;

    private ZenVizorPipeClient(NamedPipeClientStream pipe, JsonRpc rpc, IZenVizorIpc proxy)
    {
        _pipe = pipe;
        _rpc = rpc;
        Proxy = proxy;
    }

    public IZenVizorIpc Proxy { get; }

    /// <summary>
    /// Connect to the ZenVizor service named pipe and negotiate the wire-protocol
    /// version. Throws <see cref="IpcVersionMismatchException"/> if the server
    /// rejects the client's <see cref="ProtocolVersion.Current"/>.
    /// </summary>
    public static async Task<ZenVizorPipeClient> ConnectAsync(
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

        var (proxy, rpc) = ZenVizorRpcClient.Attach(pipe);

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

        return new ZenVizorPipeClient(pipe, rpc, proxy);
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

/// <summary>
/// Thrown when an <see cref="IpcEnvelope{T}"/>'s <c>SchemaVersion</c> falls
/// below the floor the client was compiled against — i.e. the service is
/// older than this client and would return a payload shape the client can't
/// safely consume.
/// </summary>
/// <remarks>
/// Floor semantics, not exact match: a newer server returning a higher
/// schema version (additive changes only) is accepted. Removing or
/// renaming a field bumps the version and the floor — older clients that
/// don't know the bumped value see this exception instead of trying to
/// deserialize a payload missing fields they expect.
/// </remarks>
public sealed class IpcSchemaVersionException : Exception
{
    public IpcSchemaVersionException(string payloadName, int expectedMinSchemaVersion, int actualSchemaVersion)
        : base(
            $"IPC schema version too old for {payloadName}: " +
            $"client expects >= v{expectedMinSchemaVersion}, server returned v{actualSchemaVersion}. " +
            "The ZenVizor service is older than this client; reinstall the matching service build.")
    {
        PayloadName = payloadName;
        ExpectedMinSchemaVersion = expectedMinSchemaVersion;
        ActualSchemaVersion = actualSchemaVersion;
    }

    public string PayloadName { get; }
    public int ExpectedMinSchemaVersion { get; }
    public int ActualSchemaVersion { get; }
}

/// <summary>
/// Floor-check helpers for <see cref="IpcEnvelope{T}"/>. Centralizes the
/// "is the server old enough to break this payload shape?" question so
/// every call site applies the same policy.
/// </summary>
public static class IpcEnvelopeExtensions
{
    /// <summary>
    /// Validate the envelope's schema version against the client's expected
    /// floor, then return the payload. Throws <see cref="IpcSchemaVersionException"/>
    /// when the server is older than the client knows how to read.
    /// </summary>
    public static T UnwrapWithSchemaCheck<T>(
        this IpcEnvelope<T> envelope,
        string payloadName,
        int expectedMinSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.SchemaVersion < expectedMinSchemaVersion)
        {
            throw new IpcSchemaVersionException(
                payloadName, expectedMinSchemaVersion, envelope.SchemaVersion);
        }
        return envelope.Payload;
    }
}
