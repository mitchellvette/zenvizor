// SPDX-License-Identifier: GPL-3.0-or-later

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

    private static SettingsSnapshot SampleSnapshot(
        bool toastOnCritical = true,
        bool toastOnWarning = false,
        bool toastOnInfo = false) => new(
        AutostartMode:               ServiceStartMode.Automatic,
        ToastOnAlert:                toastOnCritical || toastOnWarning || toastOnInfo,
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
        AlertUnusualDailyVolumeKTimesTen: 25,
        SmoothChartAnimations:       false,
        ToastOnCritical:             toastOnCritical,
        ToastOnWarning:              toastOnWarning,
        ToastOnInfo:                 toastOnInfo);

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

    // ── Epic B (1.2.0) — per-severity toast preferences ─────────────────

    [Fact]
    public async Task GetSettings_PerSeverityFields_RoundTripAcrossPipe()
    {
        // Non-default combo: Critical off, Warning on, Info on. Exercises
        // that all three trailing SettingsSnapshot fields survive JSON
        // serialization + the envelope wrap.
        var snap = SampleSnapshot(
            toastOnCritical: false,
            toastOnWarning:  true,
            toastOnInfo:     true);
        var handler = ProductionHandlerFactory.CreateDefault(
            settingsProvider: () => snap);
        await using var session = await NegotiatedSessionAsync(handler);

        var envelope = await session.Proxy.GetSettingsAsync();

        envelope.Payload.ToastOnCritical.Should().BeFalse();
        envelope.Payload.ToastOnWarning.Should().BeTrue();
        envelope.Payload.ToastOnInfo.Should().BeTrue();
        // Master is the OR of the three — it's how UIs older than 1.2.0
        // see a coherent answer.
        envelope.Payload.ToastOnAlert.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateSettings_PerSeverityFieldsOnly_InvokesApplierWithMatchingFields()
    {
        // A 1.2.0 UI sends only ToastOnWarning; the master and the other
        // two severities stay null so the handler doesn't touch them.
        SettingsUpdate? applied = null;
        var handler = ProductionHandlerFactory.CreateDefault(
            settingsApplier: u => applied = u);
        await using var session = await NegotiatedSessionAsync(handler);

        await session.Proxy.UpdateSettingsAsync(new SettingsUpdate
        {
            ToastOnWarning = true,
        });

        applied.Should().NotBeNull();
        applied!.ToastOnWarning.Should().BeTrue();
        applied.ToastOnCritical.Should().BeNull();
        applied.ToastOnInfo.Should().BeNull();
        applied.ToastOnAlert.Should().BeNull();
    }

    [Fact]
    public async Task UpdateSettings_LegacyMasterFromOldUi_RoundTripsIntact()
    {
        // A pre-1.2.0 UI sends only ToastOnAlert. The wire-level payload
        // must land on the applier unchanged so the service can mass-set
        // the three per-severity keys server-side (that mass-set is not
        // observable from this test — it happens inside
        // ApplySettingsUpdate — but the DTO reaching the applier is what
        // makes the mass-set possible).
        SettingsUpdate? applied = null;
        var handler = ProductionHandlerFactory.CreateDefault(
            settingsApplier: u => applied = u);
        await using var session = await NegotiatedSessionAsync(handler);

        await session.Proxy.UpdateSettingsAsync(new SettingsUpdate
        {
            ToastOnAlert = false,
        });

        applied.Should().NotBeNull();
        applied!.ToastOnAlert.Should().BeFalse();
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
    public async Task UpdateSettings_SmoothChartAnimations_RoundTripsToApplier()
    {
        // Phase 9.a — the new SmoothChartAnimations field is partial-update
        // friendly (nullable on SettingsUpdate) and round-trips through the
        // negotiation gate + schema-stamped envelope just like the other
        // bools. No range validation on the wire; the field is bool so
        // there is no invalid value to test.
        SettingsUpdate? applied = null;
        var handler = ProductionHandlerFactory.CreateDefault(
            settingsApplier: u => applied = u);
        await using var session = await NegotiatedSessionAsync(handler);

        await session.Proxy.UpdateSettingsAsync(new SettingsUpdate
        {
            SmoothChartAnimations = true,
        });

        applied.Should().NotBeNull();
        applied!.SmoothChartAnimations.Should().BeTrue();
        applied.AutostartMode.Should().BeNull(); // no co-applied fields
    }

    [Fact]
    public async Task GetSettings_SmoothChartAnimations_RoundTripsOnSnapshot()
    {
        var snap = SampleSnapshot() with { SmoothChartAnimations = true };
        var handler = ProductionHandlerFactory.CreateDefault(
            settingsProvider: () => snap);
        await using var session = await NegotiatedSessionAsync(handler);

        var envelope = await session.Proxy.GetSettingsAsync();

        envelope.SchemaVersion.Should().Be(IpcSchemaVersion.Settings);
        envelope.Payload.SmoothChartAnimations.Should().BeTrue();
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
        applied.SmoothChartAnimations.Should().BeNull();
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
