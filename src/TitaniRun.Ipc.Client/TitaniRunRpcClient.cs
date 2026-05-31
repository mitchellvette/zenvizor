using System.IO;
using StreamJsonRpc;
using TitaniRun.Ipc.Contracts;

namespace TitaniRun.Ipc.Client;

/// <summary>
/// Attaches a JsonRpc proxy implementing <see cref="ITitaniRunIpc"/> to a stream.
/// Used by the named-pipe client in production and by in-process duplex-stream tests.
/// </summary>
public static class TitaniRunRpcClient
{
    public static (ITitaniRunIpc Proxy, JsonRpc Rpc) Attach(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream));
        var proxy = rpc.Attach<ITitaniRunIpc>();
        rpc.StartListening();
        return (proxy, rpc);
    }
}
