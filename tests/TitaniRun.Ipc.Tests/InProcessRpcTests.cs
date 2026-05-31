using System.IO.Pipelines;
using FluentAssertions;
using Nerdbank.Streams;
using TitaniRun.Ipc.Client;
using TitaniRun.Ipc.Contracts;
using TitaniRun.Ipc.Contracts.Dto;
using TitaniRun.Ipc.Server;

namespace TitaniRun.Ipc.Tests;

/// <summary>
/// CI gate (Sprint Plan Phase 0): "IPC contract test: client and server negotiate
/// version in-process." These tests use a duplex in-memory stream pair so they
/// do not depend on real named-pipe permissions or the OS.
/// </summary>
public sealed class InProcessRpcTests
{
    [Fact]
    public async Task NegotiateVersion_MatchingClient_IsAccepted()
    {
        await using var session = TestRpcSession.Create(new FakeIpcHandler());

        var result = await session.Proxy.NegotiateVersionAsync(ProtocolVersion.Current);

        result.Accepted.Should().BeTrue();
        result.ServerVersion.Should().Be(ProtocolVersion.Current);
        result.Reason.Should().BeNull();
    }

    [Fact]
    public async Task NegotiateVersion_MismatchedMajor_IsRejectedWithReason()
    {
        await using var session = TestRpcSession.Create(new FakeIpcHandler());

        var result = await session.Proxy.NegotiateVersionAsync("2.0");

        result.Accepted.Should().BeFalse();
        result.ServerVersion.Should().Be(ProtocolVersion.Current);
        result.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Ping_RoundTripsThroughJsonRpc()
    {
        var handler = new FakeIpcHandler();
        await using var session = TestRpcSession.Create(handler);

        var ping = await session.Proxy.PingAsync();

        ping.Pong.Should().Be("pong");
        ping.ServerTimestampUnixMs.Should().Be(1_700_000_000_000L);
        handler.PingCount.Should().Be(1);
    }

    [Fact]
    public async Task GetServiceStatus_ReturnsServerDto()
    {
        await using var session = TestRpcSession.Create(new FakeIpcHandler());

        var status = await session.Proxy.GetServiceStatusAsync();

        status.ServiceName.Should().Be("TitaniRun.Service");
        status.ProtocolVersion.Should().Be(ProtocolVersion.Current);
    }

    /// <summary>
    /// Owns the in-process duplex stream + the JsonRpc instances on both ends.
    /// </summary>
    private sealed class TestRpcSession : IAsyncDisposable
    {
        private readonly IDuplexPipe _clientPipe;
        private readonly IDuplexPipe _serverPipe;
        private readonly StreamJsonRpc.JsonRpc _serverRpc;
        private readonly StreamJsonRpc.JsonRpc _clientRpc;

        private TestRpcSession(
            IDuplexPipe clientPipe,
            IDuplexPipe serverPipe,
            StreamJsonRpc.JsonRpc serverRpc,
            StreamJsonRpc.JsonRpc clientRpc,
            ITitaniRunIpc proxy)
        {
            _clientPipe = clientPipe;
            _serverPipe = serverPipe;
            _serverRpc = serverRpc;
            _clientRpc = clientRpc;
            Proxy = proxy;
        }

        public ITitaniRunIpc Proxy { get; }

        public static TestRpcSession Create(ITitaniRunIpc handler)
        {
            var (clientPipe, serverPipe) = FullDuplexStream.CreatePipePair();
            var serverStream = serverPipe.AsStream();
            var clientStream = clientPipe.AsStream();

            var serverRpc = TitaniRunRpcHost.Host(serverStream, handler);
            var (proxy, clientRpc) = TitaniRunRpcClient.Attach(clientStream);
            return new TestRpcSession(clientPipe, serverPipe, serverRpc, clientRpc, proxy);
        }

        public ValueTask DisposeAsync()
        {
            _clientRpc.Dispose();
            _serverRpc.Dispose();
            _clientPipe.Input.Complete();
            _clientPipe.Output.Complete();
            _serverPipe.Input.Complete();
            _serverPipe.Output.Complete();
            return ValueTask.CompletedTask;
        }
    }
}
