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
    /// <summary>
    /// Attach a JsonRpc proxy + RPC instance to <paramref name="stream"/> and
    /// start listening. When <paramref name="notificationTarget"/> is supplied,
    /// it's registered as a local RPC target BEFORE listening starts so the
    /// server's <c>NotifyAsync</c> push notifications (e.g.
    /// <see cref="IAlertNotifications.OnAlertRaisedAsync"/>) dispatch into
    /// the supplied callback handler. Registration order matters —
    /// AddLocalRpcTarget after StartListening can race with the first
    /// incoming notification.
    /// </summary>
    public static (IZenVizorIpc Proxy, JsonRpc Rpc) Attach(
        Stream stream,
        IAlertNotifications? notificationTarget = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream));
        var proxy = rpc.Attach<IZenVizorIpc>();
        if (notificationTarget is not null)
        {
            rpc.AddLocalRpcTarget(notificationTarget);
        }
        rpc.StartListening();
        return (proxy, rpc);
    }
}
