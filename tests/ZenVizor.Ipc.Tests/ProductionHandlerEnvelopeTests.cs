using FluentAssertions;
using ZenVizor.Ipc.Client;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ipc.Tests;

/// <summary>
/// In-process tests hosting the PRODUCTION <see cref="Service.ZenVizorIpcHandler"/>
/// (via <see cref="ProductionHandlerFactory"/>) wrapped in
/// <see cref="Server.NegotiationGate"/>. These assert the envelope shape the
/// real handler stamps onto every response — the previous suite only drove
/// <see cref="FakeIpcHandler"/>, which let the schema-version drift
/// (snapshot bumped to v2 in production while tests still asserted v1)
/// slip past CI.
/// </summary>
public sealed class ProductionHandlerEnvelopeTests
{
    [Fact]
    public async Task GetCurrentActivitySnapshot_ProductionHandler_StampsActivitySnapshotSchemaVersion()
    {
        var scripted = new ActivitySnapshot(
            CapturedAtUnixMs: 1_700_000_000_000L,
            WindowSeconds: 5.0,
            Apps: Array.Empty<AppActivity>(),
            WanLocalBreakdown: ClassBreakdown.Empty);
        var handler = ProductionHandlerFactory.CreateDefault(snapshotProvider: () => scripted);
        await using var session = GatedRpcSession.Create(handler);
        await session.Proxy.NegotiateVersionAsync(ProtocolVersion.Current);

        var envelope = await session.Proxy.GetCurrentActivitySnapshotAsync();

        // This is the test that was missing: assert the shared constant, so a
        // future bump of the snapshot schema version flips this assertion at
        // the same time it flips the producer's value.
        envelope.SchemaVersion.Should().Be(IpcSchemaVersion.ActivitySnapshot);
        envelope.Payload.WindowSeconds.Should().Be(5.0);
    }

    [Fact]
    public async Task GetCaptureStats_ProductionHandler_StampsCaptureStatsSchemaVersion()
    {
        var stats = new CaptureStats(
            CapturedAtUnixMs: 1_700_000_001_000L,
            ObservationsSeen: 100,
            ObservationsUnattributed: 3);
        var handler = ProductionHandlerFactory.CreateDefault(statsProvider: () => stats);
        await using var session = GatedRpcSession.Create(handler);
        await session.Proxy.NegotiateVersionAsync(ProtocolVersion.Current);

        var envelope = await session.Proxy.GetCaptureStatsAsync();

        envelope.SchemaVersion.Should().Be(IpcSchemaVersion.CaptureStats);
        envelope.Payload.ObservationsSeen.Should().Be(100);
        envelope.Payload.ObservationsUnattributed.Should().Be(3);
    }

    [Fact]
    public async Task GetDailyReport_ProductionHandler_StampsDailyReportSchemaVersion()
    {
        var report = new DailyReportResult(
            Date: new DateOnly(2026, 6, 10),
            Anchor: AnchorMode.Avg7d,
            AnchorSpecificDate: null,
            Hero: new DailyReportHero(
                TotalUpBytes: 5_000,
                TotalDownBytes: 12_000,
                WanRatio: 0.9,
                LocalRatio: 0.1,
                TotalDeltaPct: 12.5,
                UpDeltaPct: 8.0,
                DownDeltaPct: 14.0),
            HourlyTraffic: Array.Empty<DailyReportHourPoint>(),
            TopApps: new[]
            {
                new DailyReportAppRow(
                    AppId: 7, ImageName: "chrome.exe", ImagePath: @"C:\chrome.exe",
                    Publisher: "Google LLC", SignatureStatus: "Signed", IsUserWritablePath: false,
                    BytesUp: 1_000, BytesDown: 5_000, HasOverlap: false),
            },
            UncommonTalkers: Array.Empty<DailyReportTalker>(),
            Notable: Array.Empty<DailyReportNotable>());
        var handler = ProductionHandlerFactory.CreateDefault(
            dailyReportProvider: (_, _, _) => report);
        await using var session = GatedRpcSession.Create(handler);
        await session.Proxy.NegotiateVersionAsync(ProtocolVersion.Current);

        var envelope = await session.Proxy.GetDailyReportAsync(
            new DateOnly(2026, 6, 10), AnchorMode.Avg7d, anchorSpecificDate: null);

        envelope.SchemaVersion.Should().Be(IpcSchemaVersion.DailyReport);
        envelope.Payload.TopApps.Should().ContainSingle()
            .Which.ImageName.Should().Be("chrome.exe");
        envelope.Payload.Hero.TotalDeltaPct.Should().Be(12.5);
    }

    [Fact]
    public async Task UnwrapWithSchemaCheck_FloorMet_ReturnsPayload()
    {
        var handler = ProductionHandlerFactory.CreateDefault();
        await using var session = GatedRpcSession.Create(handler);
        await session.Proxy.NegotiateVersionAsync(ProtocolVersion.Current);

        var envelope = await session.Proxy.GetCaptureStatsAsync();
        var payload = envelope.UnwrapWithSchemaCheck(
            nameof(CaptureStats), IpcSchemaVersion.CaptureStats);

        payload.Should().NotBeNull();
    }

    [Fact]
    public void UnwrapWithSchemaCheck_BelowFloor_ThrowsTypedException()
    {
        // Floor is 2, server returned 1 — old service / new client case.
        var envelope = new IpcEnvelope<string>(SchemaVersion: 1, Payload: "stale");

        var act = () => envelope.UnwrapWithSchemaCheck("Synthetic", expectedMinSchemaVersion: 2);

        act.Should().Throw<IpcSchemaVersionException>()
            .Where(e => e.PayloadName == "Synthetic"
                     && e.ExpectedMinSchemaVersion == 2
                     && e.ActualSchemaVersion == 1);
    }
}
