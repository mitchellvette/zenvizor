using FluentAssertions;
using StreamJsonRpc;
using ZenVizor.Ipc.Contracts;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ipc.Tests;

/// <summary>
/// Phase 6.2 — settings IPC validation. Hosts the production
/// <see cref="Service.ZenVizorIpcHandler"/> in-process so the
/// negotiation-gate path, the InvalidArgument projection, and the
/// schema-version stamping all match what real callers see over the named
/// pipe.
/// </summary>
public sealed class SettingsContractTests
{
    private static async Task<GatedRpcSession> NegotiatedSessionAsync(IZenVizorIpc handler)
    {
        var session = GatedRpcSession.Create(handler);
        var negotiate = await session.Proxy.NegotiateVersionAsync(ProtocolVersion.Current);
        negotiate.Accepted.Should().BeTrue();
        return session;
    }

    private static SettingsSnapshot SampleSnapshot() => new(
        AutostartMode:               ServiceStartMode.Automatic,
        ToastOnAlert:                true,
        Theme:                       AppTheme.System,
        FlushIntervalMs:             5000,
        FlushBucketSeconds:          60,
        RetentionSamplesDays:        30,
        RetentionConnectionsDays:    30,
        RetentionHourlyDays:         90,
        RetentionDailyDays:          365,
        RetentionAlertsDaysAfterAck: 90,
        StartMinimized:              false,
        AlertLargeDownloadMb:        50,
        AlertOutboundHeavyFloorMb:   10,
        AlertUnusualDailyVolumeKTimesTen: 25);

    [Fact]
    public async Task GetSettings_AfterNegotiation_StampsSchemaVersionAndReturnsProviderPayload()
    {
        var snap = SampleSnapshot();
        var handler = ProductionHandlerFactory.CreateDefault(
            settingsProvider: () => snap);
        await using var session = await NegotiatedSessionAsync(handler);

        var envelope = await session.Proxy.GetSettingsAsync();

        envelope.SchemaVersion.Should().Be(IpcSchemaVersion.Settings);
        envelope.Payload.Should().Be(snap);
    }

    [Fact]
    public async Task UpdateSettings_ValidUpdate_InvokesApplierExactlyOnce()
    {
        SettingsUpdate? applied = null;
        var handler = ProductionHandlerFactory.CreateDefault(
            settingsApplier: u => applied = u);
        await using var session = await NegotiatedSessionAsync(handler);

        await session.Proxy.UpdateSettingsAsync(new SettingsUpdate
        {
            AutostartMode = ServiceStartMode.Manual,
            RetentionDailyDays = 730,
        });

        applied.Should().NotBeNull();
        applied!.AutostartMode.Should().Be(ServiceStartMode.Manual);
        applied.RetentionDailyDays.Should().Be(730);
    }

    [Fact]
    public async Task UpdateSettings_NegativeRetention_ReturnsInvalidArgument()
    {
        var calls = 0;
        var handler = ProductionHandlerFactory.CreateDefault(
            settingsApplier: _ => calls++);
        await using var session = await NegotiatedSessionAsync(handler);

        var bad = new SettingsUpdate { RetentionSamplesDays = -1 };

        var act = async () => await session.Proxy.UpdateSettingsAsync(bad);
        var ex = await act.Should().ThrowAsync<RemoteInvocationException>();
        ex.Which.ErrorCode.Should().Be(IpcErrorCode.InvalidArgument);

        // No partial-apply: applier MUST NOT have been called.
        calls.Should().Be(0);
    }

    [Fact]
    public async Task UpdateSettings_RetentionAboveCap_ReturnsInvalidArgument()
    {
        var handler = ProductionHandlerFactory.CreateDefault();
        await using var session = await NegotiatedSessionAsync(handler);

        var bad = new SettingsUpdate { RetentionDailyDays = 3651 };

        var act = async () => await session.Proxy.UpdateSettingsAsync(bad);
        var ex = await act.Should().ThrowAsync<RemoteInvocationException>();
        ex.Which.ErrorCode.Should().Be(IpcErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task UpdateSettings_UndefinedStartMode_ReturnsInvalidArgument()
    {
        var handler = ProductionHandlerFactory.CreateDefault();
        await using var session = await NegotiatedSessionAsync(handler);

        var bad = new SettingsUpdate { AutostartMode = (ServiceStartMode)42 };

        var act = async () => await session.Proxy.UpdateSettingsAsync(bad);
        var ex = await act.Should().ThrowAsync<RemoteInvocationException>();
        ex.Which.ErrorCode.Should().Be(IpcErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task UpdateSettings_UndefinedTheme_ReturnsInvalidArgument()
    {
        var handler = ProductionHandlerFactory.CreateDefault();
        await using var session = await NegotiatedSessionAsync(handler);

        var bad = new SettingsUpdate { Theme = (AppTheme)99 };

        var act = async () => await session.Proxy.UpdateSettingsAsync(bad);
        var ex = await act.Should().ThrowAsync<RemoteInvocationException>();
        ex.Which.ErrorCode.Should().Be(IpcErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task UpdateSettings_AllFieldsNull_AppliesNothing_Succeeds()
    {
        var calls = 0;
        SettingsUpdate? applied = null;
        var handler = ProductionHandlerFactory.CreateDefault(
            settingsApplier: u => { calls++; applied = u; });
        await using var session = await NegotiatedSessionAsync(handler);

        await session.Proxy.UpdateSettingsAsync(new SettingsUpdate());

        calls.Should().Be(1);
        applied!.AutostartMode.Should().BeNull();
        applied.ToastOnAlert.Should().BeNull();
        applied.Theme.Should().BeNull();
        applied.RetentionSamplesDays.Should().BeNull();
    }

    [Fact]
    public async Task WipeHistory_ReturnsProviderResultStampedWithSchemaVersion()
    {
        var calls = 0;
        var result = new WipeHistoryResult(
            SamplesDeleted: 5, ConnectionsDeleted: 3, HourlyDeleted: 2,
            DailyDeleted: 1, AlertsDeleted: 4, SessionsDeleted: 6);

        var handler = ProductionHandlerFactory.CreateDefault(
            historyWiper: () => { calls++; return result; });
        await using var session = await NegotiatedSessionAsync(handler);

        var envelope = await session.Proxy.WipeHistoryAsync();

        envelope.SchemaVersion.Should().Be(IpcSchemaVersion.Settings);
        envelope.Payload.Should().Be(result);
        envelope.Payload.TotalDeleted.Should().Be(5 + 3 + 2 + 1 + 4 + 6);
        calls.Should().Be(1);
    }
}
