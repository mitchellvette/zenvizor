// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Pipelines;
using Nerdbank.Streams;
using StreamJsonRpc;
using ZenVizor.Ipc.Client;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Server;

namespace ZenVizor.Ipc.Tests;

/// <summary>
/// Like <c>TestRpcSession</c> in <c>InProcessRpcTests</c>, but the server side
/// wraps the handler in <see cref="NegotiationGate"/> — matching what the
/// real <c>ZenVizorPipeServer</c> stamps onto every accepted connection. Used
/// by the negotiation-gating / sanitization / validation tests that exercise
/// the production hardening surface in-process.
/// </summary>
internal sealed class GatedRpcSession : IAsyncDisposable
{
    private readonly IDuplexPipe _clientPipe;
    private readonly IDuplexPipe _serverPipe;
    private readonly JsonRpc _serverRpc;
    private readonly JsonRpc _clientRpc;

    private GatedRpcSession(
        IDuplexPipe clientPipe,
        IDuplexPipe serverPipe,
        JsonRpc serverRpc,
        JsonRpc clientRpc,
        IZenVizorIpc proxy)
    {
        _clientPipe = clientPipe;
        _serverPipe = serverPipe;
        _serverRpc = serverRpc;
        _clientRpc = clientRpc;
        Proxy = proxy;
    }

    public IZenVizorIpc Proxy { get; }
    public JsonRpc ServerRpc => _serverRpc;
    public JsonRpc ClientRpc => _clientRpc;

    public static GatedRpcSession Create(IZenVizorIpc handler)
    {
        var (clientPipe, serverPipe) = FullDuplexStream.CreatePipePair();
        var serverStream = serverPipe.AsStream();
        var clientStream = clientPipe.AsStream();

        var gate = new NegotiationGate(handler);
        var serverRpc = ZenVizorRpcHost.Host(serverStream, gate);
        gate.SetMismatchAction(() =>
        {
            try { serverRpc.Dispose(); } catch { }
        });

        var (proxy, clientRpc) = ZenVizorRpcClient.Attach(clientStream);
        return new GatedRpcSession(clientPipe, serverPipe, serverRpc, clientRpc, proxy);
    }

    public ValueTask DisposeAsync()
    {
        try { _clientRpc.Dispose(); } catch { }
        try { _serverRpc.Dispose(); } catch { }
        try { _clientPipe.Input.Complete(); } catch { }
        try { _clientPipe.Output.Complete(); } catch { }
        try { _serverPipe.Input.Complete(); } catch { }
        try { _serverPipe.Output.Complete(); } catch { }
        return ValueTask.CompletedTask;
    }
}
