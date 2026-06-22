// SPDX-License-Identifier: GPL-3.0-or-later

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

    // ---- Phase 6 Alerts surface --------------------------------------------

    [Fact]
    public async Task GetAlerts_RoundTripsThroughEnvelope()
    {
        var handler = new FakeIpcHandler();
        var filter = new AlertsFilter(AlertState.Active);
        handler.Alerts = new AlertsResult(
            Filter: filter,
            Alerts: new[]
            {
                new AlertDto(
                    AlertId: 42,
                    Type: AlertType.UnsignedFromUserPath,
                    Severity: NotableSeverity.Critical,
                    CreatedAtUnixMs: 1_700_000_000_000L,
                    Source: SourceMonitor.Capture,
                    EntityKind: AlertEntityKind.App,
                    EntityRef: "7",
                    Title: "Unsigned program talking to the network: 7zG.exe",
                    Detail: "7zG.exe is running from a user-writable folder...",
                    AcknowledgedAtUnixMs: null),
            },
            HasMore: false);
        await using var session = TestRpcSession.Create(handler);

        var envelope = await session.Proxy.GetAlertsAsync(filter);

        envelope.SchemaVersion.Should().Be(IpcSchemaVersion.Alerts);
        envelope.Payload.Filter.State.Should().Be(AlertState.Active);
        envelope.Payload.Alerts.Should().ContainSingle();

        var alert = envelope.Payload.Alerts.Single();
        alert.AlertId.Should().Be(42);
        alert.Type.Should().Be(AlertType.UnsignedFromUserPath);
        alert.Severity.Should().Be(NotableSeverity.Critical);
        alert.Source.Should().Be(SourceMonitor.Capture);
        alert.EntityKind.Should().Be(AlertEntityKind.App);
        alert.EntityRef.Should().Be("7");
        alert.AcknowledgedAtUnixMs.Should().BeNull();
        envelope.Payload.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task DismissAlert_RecordsAlertIdOnHandler()
    {
        var handler = new FakeIpcHandler();
        await using var session = TestRpcSession.Create(handler);

        await session.Proxy.DismissAlertAsync(99);

        handler.DismissedAlertIds.Should().ContainSingle().Which.Should().Be(99);
    }

    [Fact]
    public async Task AlertRaised_PushNotification_DispatchesToClientTarget()
    {
        // Verifies the server-to-client push path end-to-end: AlertBroadcaster
        // → JsonRpc.NotifyAsync → client-side IAlertNotifications.OnAlertRaisedAsync.
        // The locking pattern matches what AlertsClient does in production.
        var handler = new FakeIpcHandler();
        var notificationTarget = new TestAlertNotifications();
        await using var session = TestRpcSession.Create(handler, notificationTarget);

        var broadcaster = new AlertBroadcaster();
        broadcaster.Register(session.ServerRpc);

        var alert = new AlertDto(
            AlertId: 1,
            Type: AlertType.UnsignedFromUserPath,
            Severity: NotableSeverity.Critical,
            CreatedAtUnixMs: 1_700_000_000_000L,
            Source: SourceMonitor.Capture,
            EntityKind: AlertEntityKind.App,
            EntityRef: "7",
            Title: "Test alert",
            Detail: "Round-trip test.",
            AcknowledgedAtUnixMs: null);

        await broadcaster.BroadcastAlertRaisedAsync(alert);

        // StreamJsonRpc dispatches notifications asynchronously; wait briefly
        // for the inbound NotifyAsync to land on the client-side target.
        var received = await notificationTarget.WaitForAlertAsync(TimeSpan.FromSeconds(2));

        received.Should().NotBeNull();
        received!.AlertId.Should().Be(1);
        received.Title.Should().Be("Test alert");
        broadcaster.SubscriberCount.Should().Be(1);
    }

    /// <summary>
    /// Test-side implementation of <see cref="IAlertNotifications"/>. Captures
    /// the first AlertDto pushed by the server and exposes a wait helper for
    /// the async dispatch round-trip.
    /// <para>
    /// <see cref="IAlertNotifications.OnAlertRaisedAsync"/> is intentionally
    /// an EXPLICIT interface implementation, mirroring how
    /// <c>AlertsClient</c> declares it in production. This is the regression
    /// guard for the Phase 6.1a fix: <c>ZenVizorRpcClient.Attach</c> must
    /// use <c>AddLocalRpcTarget&lt;IAlertNotifications&gt;</c> so explicit
    /// impls are dispatched. The non-generic <c>AddLocalRpcTarget(object)</c>
    /// scans the concrete type's public methods and skips explicit impls,
    /// dropping every push silently. If this test fails after a future
    /// change, the suspect is almost certainly the dispatch wiring.
    /// </para>
    /// </summary>
    private sealed class TestAlertNotifications : IAlertNotifications
    {
        private readonly TaskCompletionSource<AlertDto> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task IAlertNotifications.OnAlertRaisedAsync(AlertDto alert)
        {
            _tcs.TrySetResult(alert);
            return Task.CompletedTask;
        }

        public async Task<AlertDto?> WaitForAlertAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
            return completed == _tcs.Task ? await _tcs.Task.ConfigureAwait(false) : null;
        }
    }

    /// <summary>
    /// Owns the in-process duplex stream + the JsonRpc instances on both ends.
    /// <see cref="ServerRpc"/> is exposed so push-notification tests can
    /// register the server-side <see cref="StreamJsonRpc.JsonRpc"/> with an
    /// <see cref="AlertBroadcaster"/>.
    /// </summary>
    private sealed class TestRpcSession : IAsyncDisposable
    {
        private readonly IDuplexPipe _clientPipe;
        private readonly IDuplexPipe _serverPipe;
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
            ServerRpc = serverRpc;
            _clientRpc = clientRpc;
            Proxy = proxy;
        }

        public IZenVizorIpc Proxy { get; }
        public StreamJsonRpc.JsonRpc ServerRpc { get; }

        public static TestRpcSession Create(
            IZenVizorIpc handler,
            IAlertNotifications? notificationTarget = null)
        {
            var (clientPipe, serverPipe) = FullDuplexStream.CreatePipePair();
            var serverStream = serverPipe.AsStream();
            var clientStream = clientPipe.AsStream();

            var serverRpc = ZenVizorRpcHost.Host(serverStream, handler);
            var (proxy, clientRpc) = ZenVizorRpcClient.Attach(clientStream, notificationTarget);
            return new TestRpcSession(clientPipe, serverPipe, serverRpc, clientRpc, proxy);
        }

        public ValueTask DisposeAsync()
        {
            _clientRpc.Dispose();
            ServerRpc.Dispose();
            _clientPipe.Input.Complete();
            _clientPipe.Output.Complete();
            _serverPipe.Input.Complete();
            _serverPipe.Output.Complete();
            return ValueTask.CompletedTask;
        }
    }
}
