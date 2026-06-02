using System.IO;
using StreamJsonRpc;
using ZenVizor.Ipc.Contracts;

namespace ZenVizor.Ipc.Client;

/// <summary>
/// Attaches a JsonRpc proxy implementing <see cref="IZenVizorIpc"/> to a stream.
/// Used by the named-pipe client in production and by in-process duplex-stream tests.
/// </summary>
public static class ZenVizorRpcClient
{
    public static (IZenVizorIpc Proxy, JsonRpc Rpc) Attach(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream));
        var proxy = rpc.Attach<IZenVizorIpc>();
        rpc.StartListening();
        return (proxy, rpc);
    }
}
