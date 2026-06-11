using System.IO.Pipelines;
using FluentAssertions;
using Nerdbank.Streams;
using ZenVizor.Ipc.Client;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Ipc.Server;

namespace ZenVizor.Ipc.Tests;

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

        status.ServiceName.Should().Be("ZenVizor.Service");
        status.ProtocolVersion.Should().Be(ProtocolVersion.Current);
    }

    [Fact]
    public async Task GetCurrentActivitySnapshot_RoundTripsThroughEnvelope()
    {
        var handler = new FakeIpcHandler();
        var scripted = new ActivitySnapshot(
            CapturedAtUnixMs: 1_700_000_005_000L,
            WindowSeconds: 7.5,
            Apps: new[]
            {
                new AppActivity(
                    ImageName: "chrome.exe",
                    ImagePath: @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                    Publisher: "Google LLC",
                    SignatureStatus: "Signed",
                    IsUserWritablePath: false,
                    HostedServices: null,
                    BytesUpTotal: 7_500,
                    BytesDownTotal: 90_000,
                    BytesUpPerSec: 1_000.0,
                    BytesDownPerSec: 12_000.0),
                new AppActivity(
                    ImageName: "svchost.exe",
                    ImagePath: @"C:\Windows\System32\svchost.exe",
                    Publisher: "Microsoft Corporation",
                    SignatureStatus: "Signed",
                    IsUserWritablePath: false,
                    HostedServices: "Dnscache,DiagTrack",
                    BytesUpTotal: 750,
                    BytesDownTotal: 1_500,
                    BytesUpPerSec: 100.0,
                    BytesDownPerSec: 200.0),
            },
            WanLocalBreakdown: new ClassBreakdown(
                WanBytesUp: 8_000,
                WanBytesDown: 90_500,
                LocalBytesUp: 250,
                LocalBytesDown: 1_000));
        handler.SetSnapshot(scripted);

        await using var session = TestRpcSession.Create(handler);

        var envelope = await session.Proxy.GetCurrentActivitySnapshotAsync();

        // FakeIpcHandler stamps IpcSchemaVersion.ActivitySnapshot (the same
        // constant production uses) so any future bump of the schema version
        // forces this assertion to update alongside the producer.
        envelope.SchemaVersion.Should().Be(IpcSchemaVersion.ActivitySnapshot);
        envelope.Payload.CapturedAtUnixMs.Should().Be(1_700_000_005_000L);
        envelope.Payload.WindowSeconds.Should().Be(7.5);
        envelope.Payload.Apps.Should().HaveCount(2);

        var chrome = envelope.Payload.Apps.Single(a => a.ImageName == "chrome.exe");
        chrome.Publisher.Should().Be("Google LLC");
        chrome.SignatureStatus.Should().Be("Signed");
        chrome.IsUserWritablePath.Should().BeFalse();
        chrome.HostedServices.Should().BeNull();
        chrome.BytesDownPerSec.Should().Be(12_000.0);

        var svchost = envelope.Payload.Apps.Single(a => a.ImageName == "svchost.exe");
        svchost.HostedServices.Should().Be("Dnscache,DiagTrack");
        svchost.BytesUpTotal.Should().Be(750);

        envelope.Payload.WanLocalBreakdown.Should().NotBeNull();
        envelope.Payload.WanLocalBreakdown.WanBytesUp.Should().Be(8_000);
        envelope.Payload.WanLocalBreakdown.WanBytesDown.Should().Be(90_500);
        envelope.Payload.WanLocalBreakdown.LocalBytesUp.Should().Be(250);
        envelope.Payload.WanLocalBreakdown.LocalBytesDown.Should().Be(1_000);

        handler.ActivitySnapshotCount.Should().Be(1);
    }

    [Fact]
    public async Task IpcEnvelope_SchemaVersion_SurvivesSerialization()
    {
        var handler = new FakeIpcHandler { SnapshotSchemaVersion = 42 };
        await using var session = TestRpcSession.Create(handler);

        var envelope = await session.Proxy.GetCurrentActivitySnapshotAsync();

        envelope.SchemaVersion.Should().Be(42);
    }

    [Fact]
    public async Task GetAppList_RoundTripsThroughEnvelope()
    {
        var handler = new FakeIpcHandler();
        handler.AppList = new AppListResult(
            new QueryWindow(1_000, 7_000),
            new[]
            {
                new AppListEntry(1, "chrome.exe", @"C:\chrome.exe", "Google LLC",
                    "Signed", false, 5_000, 50_000, 100, 6_000),
            });
        await using var session = TestRpcSession.Create(handler);

        var envelope = await session.Proxy.GetAppListAsync(new QueryWindow(1_000, 7_000));

        envelope.SchemaVersion.Should().Be(IpcSchemaVersion.Query);
        envelope.Payload.Window.FromUnixMs.Should().Be(1_000);
        envelope.Payload.Apps.Should().ContainSingle()
            .Which.Publisher.Should().Be("Google LLC");
    }

    [Fact]
    public async Task GetAppDetail_RoundTripsThroughEnvelope()
    {
        var handler = new FakeIpcHandler();
        handler.AppDetail = new AppDetailResult(
            new QueryWindow(0, 3_600_000),
            TrafficGrain.Samples,
            new AppListEntry(7, "x.exe", "/x.exe", null, "Unchecked", true, 1, 2, 0, 0),
            new[] { new TrafficPoint(0, "Wan", 1, 2) },
            new[] { new SessionInfo(99, 1234, 0, null, "Dnscache") });
        await using var session = TestRpcSession.Create(handler);

        var envelope = await session.Proxy.GetAppDetailAsync(7, new QueryWindow(0, 3_600_000), TrafficGrain.Auto);

        envelope.Payload.Summary.AppId.Should().Be(7);
        envelope.Payload.GrainUsed.Should().Be(TrafficGrain.Samples);
        envelope.Payload.Series.Should().ContainSingle();
        envelope.Payload.RecentSessions.Single().HostedServices.Should().Be("Dnscache");
    }

    [Fact]
    public async Task GetConnections_RoundTripsThroughEnvelope()
    {
        var handler = new FakeIpcHandler();
        handler.Connections = new ConnectionListResult(
            new QueryWindow(0, 1_000),
            new[] { new ConnectionRow("TCP", "8.8.8.8", 443, "Wan", 100, 200, 0, 1_000) });
        await using var session = TestRpcSession.Create(handler);

        var envelope = await session.Proxy.GetConnectionsAsync(1, new QueryWindow(0, 1_000));

        envelope.Payload.Connections.Should().ContainSingle()
            .Which.RemoteAddress.Should().Be("8.8.8.8");
    }

    [Fact]
    public async Task GetTrafficHistory_RoundTripsThroughEnvelope()
    {
        var handler = new FakeIpcHandler();
        handler.History = new TrafficHistoryResult(
            new QueryWindow(0, 86_400_000L * 60),
            TrafficGrain.Daily,
            new[]
            {
                new TrafficPoint(0, "Wan", 1_000, 0),
                new TrafficPoint(86_400_000L, "Wan", 2_000, 0),
            });
        await using var session = TestRpcSession.Create(handler);

        var envelope = await session.Proxy.GetTrafficHistoryAsync(
            new QueryWindow(0, 86_400_000L * 60), TrafficGrain.Daily);

        envelope.Payload.GrainUsed.Should().Be(TrafficGrain.Daily);
        envelope.Payload.Series.Should().HaveCount(2);
        envelope.Payload.Series.Sum(p => p.BytesUp).Should().Be(3_000);
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
            IZenVizorIpc proxy)
        {
            _clientPipe = clientPipe;
            _serverPipe = serverPipe;
            _serverRpc = serverRpc;
            _clientRpc = clientRpc;
            Proxy = proxy;
        }

        public IZenVizorIpc Proxy { get; }

        public static TestRpcSession Create(IZenVizorIpc handler)
        {
            var (clientPipe, serverPipe) = FullDuplexStream.CreatePipePair();
            var serverStream = serverPipe.AsStream();
            var clientStream = clientPipe.AsStream();

            var serverRpc = ZenVizorRpcHost.Host(serverStream, handler);
            var (proxy, clientRpc) = ZenVizorRpcClient.Attach(clientStream);
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
