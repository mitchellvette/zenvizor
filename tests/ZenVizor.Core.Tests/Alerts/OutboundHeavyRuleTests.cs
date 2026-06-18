using FluentAssertions;
using ZenVizor.Core.Alerts;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Observations;
using ZenVizor.Core.Storage;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Tests.Alerts;

/// <summary>
/// Unit tests for <see cref="OutboundHeavyRule"/>. Covers the floor
/// gate (settings-driven), the locked 3:1 ratio, the 15-minute rolling
/// window, multi-PID detail enrichment, and the no-inbound edge case.
/// </summary>
public sealed class OutboundHeavyRuleTests
{
    private const long T0 = 1_780_704_000_000L;

    private static AppIdentity App(string name = "backup.exe", string path = @"C:\Program Files\Acme\backup.exe") =>
        new(ImagePath: path, ImageName: name, Publisher: "Acme",
            SignatureStatus: "Signed", IsUserWritablePath: false);

    private static FlushConnectionState Conn(
        int pid = 4242,
        int sessionId = 100,
        int appId = 8,
        long bytesUp = 0,
        long bytesDown = 0,
        AppIdentity? app = null) =>
        new(
            Pid:                pid,
            AppId:              appId,
            SessionId:          sessionId,
            App:                app ?? App(),
            Protocol:           Protocol.Tcp,
            RemoteAddress:      "203.0.113.50",
            RemotePort:         443,
            RemoteClass:        RemoteClass.Wan,
            BytesUpDelta:       bytesUp,
            BytesDownDelta:     bytesDown,
            FirstSeenUnixMs:    T0,
            LastSeenUnixMs:     T0);

    private static FlushAlertEvent Evt(long flushTime, params FlushConnectionState[] conns) =>
        new(FlushTimeUnixMs: flushTime, FlushIntervalMs: 5_000, Connections: conns);

    private const long Mb = 1024L * 1024L;

    [Fact]
    public void Evaluate_FloorAndRatioCleared_Raises()
    {
        var settings = new StaticAlertSettingsLookup(outboundHeavyFloorMb: 10);
        var rule = new OutboundHeavyRule(settings);

        // 12 MB up, 3 MB down: 4:1 ratio, clears the 10 MB floor.
        var results = rule.Evaluate(Evt(T0,
            Conn(bytesUp: 12 * Mb, bytesDown: 3 * Mb))).ToList();

        results.Should().ContainSingle();
        var (req, detail) = results[0];
        req.Type.Should().Be(AlertType.OutboundHeavy);
        req.Severity.Should().Be(NotableSeverity.Warning);
        req.SourceMonitor.Should().Be(SourceMonitor.Capture);
        req.EntityKind.Should().Be(AlertEntityKind.App);
        req.EntityRef.Should().Be("8");
        req.Title.Should().Be("Outbound-heavy app: backup.exe");
        detail.Should().Contain("12 MB");
        detail.Should().Contain("ratio 4.0x");
        detail.Should().Contain("PID 4242");
        detail.Should().NotContain("—");
    }

    [Fact]
    public void Evaluate_BelowFloor_DoesNotFire()
    {
        var settings = new StaticAlertSettingsLookup(outboundHeavyFloorMb: 100);
        var rule = new OutboundHeavyRule(settings);

        // 50 MB up, 1 MB down: 50:1 ratio (clears) BUT 50 MB doesn't
        // clear the 100 MB floor.
        rule.Evaluate(Evt(T0,
            Conn(bytesUp: 50 * Mb, bytesDown: 1 * Mb))).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_FloorClearedRatioFails_DoesNotFire()
    {
        var settings = new StaticAlertSettingsLookup(outboundHeavyFloorMb: 10);
        var rule = new OutboundHeavyRule(settings);

        // 15 MB up, 10 MB down: 1.5:1 ratio, fails the 3:1 lock.
        rule.Evaluate(Evt(T0,
            Conn(bytesUp: 15 * Mb, bytesDown: 10 * Mb))).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_NoInbound_ClearsRatioAutomatically()
    {
        var settings = new StaticAlertSettingsLookup(outboundHeavyFloorMb: 10);
        var rule = new OutboundHeavyRule(settings);

        var results = rule.Evaluate(Evt(T0,
            Conn(bytesUp: 12 * Mb, bytesDown: 0))).ToList();

        results.Should().ContainSingle();
        results[0].Detail.Should().Contain("no inbound traffic in window");
    }

    [Fact]
    public void Evaluate_AggregatesAcross15MinWindow()
    {
        var settings = new StaticAlertSettingsLookup(outboundHeavyFloorMb: 10);
        var rule = new OutboundHeavyRule(settings);

        // 4 MB up over 3 flushes within window → 12 MB cumulative.
        // 1 MB down across the same → 12:1 ratio.
        rule.Evaluate(Evt(T0,         Conn(bytesUp: 4 * Mb, bytesDown: 0))).Should().BeEmpty();
        rule.Evaluate(Evt(T0 + 5_000, Conn(bytesUp: 4 * Mb, bytesDown: 0))).Should().BeEmpty();
        var third = rule.Evaluate(Evt(T0 + 10_000, Conn(bytesUp: 4 * Mb, bytesDown: 1 * Mb))).ToList();
        third.Should().ContainSingle();
    }

    [Fact]
    public void Evaluate_BucketsOutsideWindow_AreEvicted()
    {
        var settings = new StaticAlertSettingsLookup(outboundHeavyFloorMb: 10);
        var rule = new OutboundHeavyRule(settings);

        // 8 MB up 16 minutes ago, then 5 MB up now. Old bucket evicts;
        // current window is 5 MB which fails the floor.
        rule.Evaluate(Evt(T0, Conn(bytesUp: 8 * Mb, bytesDown: 0))).Should().BeEmpty();
        rule.Evaluate(Evt(T0 + (long)TimeSpan.FromMinutes(16).TotalMilliseconds,
            Conn(bytesUp: 5 * Mb, bytesDown: 0))).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_MultiPidSameApp_DetailEnumeratesPerPidContribution()
    {
        var settings = new StaticAlertSettingsLookup(outboundHeavyFloorMb: 10);
        var rule = new OutboundHeavyRule(settings);

        var results = rule.Evaluate(Evt(T0,
            Conn(pid: 1111, bytesUp: 8 * Mb, bytesDown: 0),
            Conn(pid: 2222, bytesUp: 6 * Mb, bytesDown: 0))).ToList();

        results.Should().ContainSingle();
        var detail = results[0].Detail;
        detail.Should().Contain("14 MB");
        // Largest contributor surfaces first.
        detail.Should().Contain("PIDs 1111 (8 MB), 2222 (6 MB)");
    }

    [Fact]
    public void Evaluate_AlreadyAlertedApp_DoesNotDoubleFire()
    {
        var settings = new StaticAlertSettingsLookup(outboundHeavyFloorMb: 10);
        var rule = new OutboundHeavyRule(settings);

        var first = rule.Evaluate(Evt(T0,
            Conn(bytesUp: 12 * Mb, bytesDown: 1 * Mb))).ToList();
        first.Should().ContainSingle();

        // Same app, same flush window — should not re-fire from the
        // rule (producer dedupe would absorb anyway).
        var second = rule.Evaluate(Evt(T0 + 5_000,
            Conn(bytesUp: 5 * Mb, bytesDown: 1 * Mb))).ToList();
        second.Should().BeEmpty();
    }

    [Fact]
    public void CooldownMs_Is24Hours()
    {
        new OutboundHeavyRule(new StaticAlertSettingsLookup())
            .CooldownMs.Should().Be((long)TimeSpan.FromHours(24).TotalMilliseconds);
    }
}
