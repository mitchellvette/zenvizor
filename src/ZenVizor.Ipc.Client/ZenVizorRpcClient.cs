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
            // AddLocalRpcTarget<TInterface> — NOT the non-generic
            // AddLocalRpcTarget(object) — because production
            // implementations (notably AlertsClient) use EXPLICIT
            // interface implementations on IAlertNotifications to keep
            // OnAlertRaisedAsync off the class's public surface
            // (consumers raise interest via the AlertRaised event).
            // Explicit-impl methods are NOT publicly accessible via
            // target.GetType().GetMethods(), so AddLocalRpcTarget(object)
            // silently fails to wire them — production push
            // notifications dropped on the floor and the only end-to-end
            // signal (the AlertRaised event firing) never raised.
            // Generic dispatch uses typeof(TInterface)'s method table,
            // which DOES include explicit impls. Phase 6.1a fix.
            rpc.AddLocalRpcTarget<IAlertNotifications>(notificationTarget, options: null);
        }
        rpc.StartListening();
        return (proxy, rpc);
    }
}
