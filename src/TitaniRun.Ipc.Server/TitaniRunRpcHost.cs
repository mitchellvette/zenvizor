using System.IO;
using StreamJsonRpc;
using TitaniRun.Ipc.Contracts;

namespace TitaniRun.Ipc.Server;

/// <summary>
/// Shared helper that attaches a <see cref="JsonRpc"/> instance to a stream,
/// dispatching to the supplied <see cref="ITitaniRunIpc"/> handler. Used by the
/// named-pipe server in production and by in-process duplex-stream tests.
/// </summary>
public static class TitaniRunRpcHost
{
    /// <summary>
    /// Attach JsonRpc to <paramref name="stream"/> and begin listening. The returned
    /// <see cref="JsonRpc"/> instance owns the lifetime; dispose it to tear down.
    /// </summary>
    public static JsonRpc Host(Stream stream, ITitaniRunIpc handler)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(handler);

        var rpc = JsonRpc.Attach(stream, handler);
        return rpc;
    }
}
