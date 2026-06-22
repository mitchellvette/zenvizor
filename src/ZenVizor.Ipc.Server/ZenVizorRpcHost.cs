// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using ZenVizor.Ipc.Contracts;

namespace ZenVizor.Ipc.Server;

/// <summary>
/// Shared helper that attaches a <see cref="JsonRpc"/> instance to a stream,
/// dispatching to the supplied <see cref="IZenVizorIpc"/> handler. Used by the
/// named-pipe server in production and by in-process duplex-stream tests.
/// <para>
/// All sessions are wrapped in <see cref="SanitizingJsonRpc"/> so unhandled
/// exceptions never leak their type/message/stack to the client.
/// </para>
/// </summary>
public static class ZenVizorRpcHost
{
    /// <summary>
    /// Attach JsonRpc to <paramref name="stream"/> and begin listening. The returned
    /// <see cref="JsonRpc"/> instance owns the lifetime; dispose it to tear down.
    /// </summary>
    public static JsonRpc Host(Stream stream, IZenVizorIpc handler, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(handler);

        var messageHandler = new HeaderDelimitedMessageHandler(stream);
        var rpc = new SanitizingJsonRpc(messageHandler, logger);
        rpc.AddLocalRpcTarget(handler);
        rpc.StartListening();
        return rpc;
    }
}
