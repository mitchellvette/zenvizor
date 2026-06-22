// SPDX-License-Identifier: GPL-3.0-or-later

using FluentAssertions;
using ZenVizor.Core.Alerts;
using ZenVizor.Core.Observations;
using ZenVizor.Core.Storage;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Tests.Alerts;

/// <summary>
/// Pure unit tests for <see cref="FirstRunWanTalkerRule"/>. Predicate gates:
/// WAN connection present, AppFirstSeenUnixMs > 0, and
/// (FlushTimeUnixMs - AppFirstSeenUnixMs) within
/// <see cref="FirstRunWanTalkerRule.FirstRunWindowMs"/>.
/// </summary>
public sealed class FirstRunWanTalkerRuleTests
{
    private const long T0 = 1_780_704_000_000L;

    private static PendingConnection WanConn(long firstSeen = T0) => new(
        Pid: 5555,
        Protocol: Protocol.Tcp,
        RemoteAddress: "1.0.0.1",
        RemotePort: 443,
        RemoteClass: RemoteClass.Wan,
        BytesUpDelta: 0,
        BytesDownDelta: 0,
        FirstSeenUnixMs: firstSeen,
        LastSeenUnixMs: firstSeen);

    private static NewSessionContext Ctx(
        long appFirstSeen,
        long flushTime,
        int appId = 101,
        string imageName = "freshinstall.exe",
        string imagePath = @"C:\Users\Mitch\AppData\Local\Programs\X\freshinstall.exe") =>
        new(AppId: appId,
            ImagePath:          imagePath,
            ImageName:          imageName,
            Publisher:          null,
            SignatureStatus:    "Signed",
            IsUserWritablePath: false,
            WanConnection:      WanConn(flushTime),
            FlushTimeUnixMs:    flushTime,
            AppFirstSeenUnixMs: appFirstSeen);

    [Fact]
    public void TryEvaluate_NewAppWithinWindow_ReturnsRaiseRequest()
    {
        var rule = new FirstRunWanTalkerRule();
        // App first seen 30 s ago, WAN connection now → fires.
        var ctx = Ctx(appFirstSeen: T0, flushTime: T0 + 30_000);
        var req = rule.TryEvaluate(ctx);

        req.Should().NotBeNull();
        req!.Type.Should().Be(AlertType.FirstRunWanTalker);
        req.Severity.Should().Be(NotableSeverity.Info);
        req.SourceMonitor.Should().Be(SourceMonitor.Capture);
        req.EntityKind.Should().Be(AlertEntityKind.App);
        req.EntityRef.Should().Be("101");
        req.Title.Should().Be("Newly-installed program reached the network: freshinstall.exe");
    }

    [Fact]
    public void TryEvaluate_AtExactlyWindowBoundary_StillFires()
    {
        var rule = new FirstRunWanTalkerRule();
        // App first seen exactly 60 s ago → on the boundary, inclusive.
        var ctx = Ctx(appFirstSeen: T0, flushTime: T0 + FirstRunWanTalkerRule.FirstRunWindowMs);
        rule.TryEvaluate(ctx).Should().NotBeNull();
    }

    [Fact]
    public void TryEvaluate_PastWindow_ReturnsNull()
    {
        var rule = new FirstRunWanTalkerRule();
        // App first seen 61 s ago → outside the 60 s window.
        var ctx = Ctx(appFirstSeen: T0, flushTime: T0 + FirstRunWanTalkerRule.FirstRunWindowMs + 1);
        rule.TryEvaluate(ctx).Should().BeNull();
    }

    [Fact]
    public void TryEvaluate_NoFirstSeen_ReturnsNull()
    {
        // AppFirstSeenUnixMs default is 0 — producer's lookup miss.
        var rule = new FirstRunWanTalkerRule();
        var ctx = Ctx(appFirstSeen: 0, flushTime: T0);
        rule.TryEvaluate(ctx).Should().BeNull();
    }

    [Fact]
    public void TryEvaluate_FirstSeenInFuture_ReturnsNull()
    {
        // Defensive: clock skew can produce negative ages. Don't fire.
        var rule = new FirstRunWanTalkerRule();
        var ctx = Ctx(appFirstSeen: T0 + 60_000, flushTime: T0);
        rule.TryEvaluate(ctx).Should().BeNull();
    }

    [Fact]
    public void RenderDetail_NamesFirstSeenAndConnectionDelta()
    {
        var rule = new FirstRunWanTalkerRule();
        var ctx = Ctx(appFirstSeen: T0, flushTime: T0 + 5_000);

        var detail = rule.RenderDetail(ctx, connectionCount: 1);

        detail.Should().Contain("freshinstall.exe was first observed at");
        detail.Should().Contain("opened its first network connection at");
        detail.Should().Contain("(5 s after first observed)");
        detail.Should().Contain(@"Image path: C:\Users\Mitch\AppData\Local\Programs\X\freshinstall.exe");
        detail.Should().Contain("Connections so far: 1.");
        detail.Should().NotContain("—");
    }

    [Fact]
    public void RenderDetail_HigherCount_AdvancesConnectionsPhrase()
    {
        var rule = new FirstRunWanTalkerRule();
        var detail = rule.RenderDetail(Ctx(appFirstSeen: T0, flushTime: T0 + 1_000), connectionCount: 4);
        detail.Should().Contain("Connections so far: 4.");
    }

    [Fact]
    public void CooldownMs_IsEffectivelyForever()
    {
        // FirstRun is one-shot per app lifetime; cooldown is locked at the
        // catalog level to "effectively never". Asserts the lock so a future
        // edit that tries to weaken it surfaces in CI.
        new FirstRunWanTalkerRule().CooldownMs.Should().BeGreaterThan(
            (long)TimeSpan.FromDays(365 * 100).TotalMilliseconds);
    }
}
