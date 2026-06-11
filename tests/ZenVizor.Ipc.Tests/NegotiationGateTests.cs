using FluentAssertions;
using StreamJsonRpc;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ipc.Tests;

/// <summary>
/// The IZenVizorIpc contract documents "clients MUST call NegotiateVersionAsync
/// first" — but until the server enforced it, a non-compliant client could
/// skip the handshake and call every envelope-tier method. These tests gate
/// that enforcement, using the production <see cref="Service.ZenVizorIpcHandler"/>
/// wrapped in the <see cref="Server.NegotiationGate"/> exactly as the pipe
/// server stamps onto every accepted connection.
/// </summary>
public sealed class NegotiationGateTests
{
    [Fact]
    public async Task EnvelopeMethod_BeforeNegotiation_ReturnsTypedNegotiationRequiredFault()
    {
        await using var session = GatedRpcSession.Create(ProductionHandlerFactory.CreateDefault());

        var act = async () => await session.Proxy.GetCurrentActivitySnapshotAsync();

        var ex = await act.Should().ThrowAsync<RemoteInvocationException>();
        ex.Which.ErrorCode.Should().Be(IpcErrorCode.NegotiationRequired);
    }

    [Fact]
    public async Task QueryMethod_BeforeNegotiation_ReturnsTypedNegotiationRequiredFault()
    {
        await using var session = GatedRpcSession.Create(ProductionHandlerFactory.CreateDefault());

        var act = async () => await session.Proxy.GetAppListAsync(new QueryWindow(0, 1_000));

        var ex = await act.Should().ThrowAsync<RemoteInvocationException>();
        ex.Which.ErrorCode.Should().Be(IpcErrorCode.NegotiationRequired);
    }

    [Fact]
    public async Task PhaseZeroMethods_AreCallableBeforeNegotiation()
    {
        // Negotiate / Ping / GetServiceStatus stay open before negotiation by
        // design (IpcEnvelope.cs commentary). The gate must not break this.
        await using var session = GatedRpcSession.Create(ProductionHandlerFactory.CreateDefault());

        var ping = await session.Proxy.PingAsync();
        ping.Pong.Should().Be("pong");

        var status = await session.Proxy.GetServiceStatusAsync();
        status.ProtocolVersion.Should().Be(ProtocolVersion.Current);
    }

    [Fact]
    public async Task AfterAcceptedNegotiation_EnvelopeMethodsSucceed()
    {
        await using var session = GatedRpcSession.Create(ProductionHandlerFactory.CreateDefault());

        var negotiate = await session.Proxy.NegotiateVersionAsync(ProtocolVersion.Current);
        negotiate.Accepted.Should().BeTrue();

        // Activity snapshot is the Phase-3 envelope path — unchanged by gating
        // once negotiation has cleared.
        var envelope = await session.Proxy.GetCurrentActivitySnapshotAsync();
        envelope.Should().NotBeNull();
        envelope.Payload.Should().NotBeNull();

        // A second envelope call also goes through.
        var stats = await session.Proxy.GetCaptureStatsAsync();
        stats.Payload.Should().NotBeNull();
    }

    [Fact]
    public async Task RejectedNegotiation_DoesNotOpenTheGate()
    {
        await using var session = GatedRpcSession.Create(ProductionHandlerFactory.CreateDefault());

        var negotiate = await session.Proxy.NegotiateVersionAsync("2.0");
        negotiate.Accepted.Should().BeFalse();

        // Even though we got a response back, the gate stays closed.
        // The mismatch action ALSO tears down the session shortly after — but
        // either way (gate-blocked OR connection-closed), an envelope call
        // must NOT succeed.
        var act = async () => await session.Proxy.GetAppListAsync(new QueryWindow(0, 1_000));
        await act.Should().ThrowAsync<Exception>();
    }
}
