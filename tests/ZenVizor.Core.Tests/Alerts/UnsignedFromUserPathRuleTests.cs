using FluentAssertions;
using ZenVizor.Core.Alerts;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Observations;
using ZenVizor.Core.Storage;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Tests.Alerts;

/// <summary>
/// Pure unit tests for <see cref="UnsignedFromUserPathRule"/> — the four
/// firing gates (signature=Unsigned, IsUserWritablePath, WAN connection
/// present, AppId resolved) and the catalog §2 detail-template lock. No DB,
/// no producer, no aggregator — just the rule's TryEvaluate + RenderDetail
/// against handcrafted contexts.
/// </summary>
public sealed class UnsignedFromUserPathRuleTests
{
    private const long T0 = 1_780_704_000_000L;

    private static AppIdentity App(
        string sig = "Unsigned",
        bool userWritable = true,
        string name = "7zG.exe",
        string path = @"C:\Users\Mitch\AppData\Local\Temp\7zG.exe") =>
        new(ImagePath: path, ImageName: name, Publisher: null,
            SignatureStatus: sig, IsUserWritablePath: userWritable);

    private static PendingConnection WanConn(long firstSeen = T0) => new(
        Pid: 4242,
        Protocol: Protocol.Tcp,
        RemoteAddress: "8.8.8.8",
        RemotePort: 443,
        RemoteClass: RemoteClass.Wan,
        BytesUpDelta: 0,
        BytesDownDelta: 0,
        FirstSeenUnixMs: firstSeen,
        LastSeenUnixMs: firstSeen);

    private static NewSessionContext Ctx(
        AppIdentity? app = null,
        PendingConnection? conn = null,
        int appId = 47) =>
        new(AppId: appId,
            ImagePath:          (app ?? App()).ImagePath,
            ImageName:          (app ?? App()).ImageName,
            Publisher:          (app ?? App()).Publisher,
            SignatureStatus:    (app ?? App()).SignatureStatus,
            IsUserWritablePath: (app ?? App()).IsUserWritablePath,
            WanConnection:      conn ?? WanConn(),
            FlushTimeUnixMs:    T0);

    [Fact]
    public void TryEvaluate_AllGatesPass_ReturnsRaiseRequest()
    {
        var rule = new UnsignedFromUserPathRule();
        var req = rule.TryEvaluate(Ctx(appId: 47));

        req.Should().NotBeNull();
        req!.Type.Should().Be(AlertType.UnsignedFromUserPath);
        req.Severity.Should().Be(NotableSeverity.Critical);
        req.SourceMonitor.Should().Be(SourceMonitor.Capture);
        req.EntityKind.Should().Be(AlertEntityKind.App);
        req.EntityRef.Should().Be("47");
        req.AppId.Should().Be(47);
        req.Title.Should().Be("Unsigned program talking to the network: 7zG.exe");
    }

    [Fact]
    public void TryEvaluate_SignedApp_ReturnsNull()
    {
        var rule = new UnsignedFromUserPathRule();
        rule.TryEvaluate(Ctx(app: App(sig: "Signed"))).Should().BeNull();
    }

    [Fact]
    public void TryEvaluate_InvalidSignature_ReturnsNull_BecauseDifferentRuleWillHandleIt()
    {
        // brief §13: Invalid routes to a future InvalidSignature rule, not
        // this one. Exact-string match on "Unsigned" enforces the boundary.
        var rule = new UnsignedFromUserPathRule();
        rule.TryEvaluate(Ctx(app: App(sig: "Invalid"))).Should().BeNull();
    }

    [Fact]
    public void TryEvaluate_UncheckedSignature_ReturnsNull()
    {
        // SignerCache hasn't classified this image yet; "never fabricate
        // precision" (CLAUDE.md) — silent until we have evidence.
        var rule = new UnsignedFromUserPathRule();
        rule.TryEvaluate(Ctx(app: App(sig: "Unchecked"))).Should().BeNull();
    }

    [Fact]
    public void TryEvaluate_SystemPath_ReturnsNull()
    {
        var rule = new UnsignedFromUserPathRule();
        rule.TryEvaluate(Ctx(app: App(userWritable: false))).Should().BeNull();
    }

    [Fact]
    public void RenderDetail_FirstObservation_MatchesCatalogTemplateLiteral()
    {
        var rule = new UnsignedFromUserPathRule();
        var ctx = Ctx(conn: WanConn(firstSeen: T0));

        // Expected timestamp piece is local-time formatted; build the same
        // way the rule does so the test isn't tied to the runner's TZ.
        var expectedTs = DateTimeOffset
            .FromUnixTimeMilliseconds(T0)
            .LocalDateTime
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        var detail = rule.RenderDetail(ctx, connectionCount: 1);

        detail.Should().Contain("7zG.exe is running from a user-writable folder");
        detail.Should().Contain(@"Image path: C:\Users\Mitch\AppData\Local\Temp\7zG.exe");
        detail.Should().Contain("Signer: none (unsigned)");
        detail.Should().Contain($"First connection: {expectedTs}");
        detail.Should().Contain("Connections so far: 1.");
        // Memory: feedback_no_emdash_in_ui_copy — UI prose never uses
        // em-dash. Catalog template is the authoritative version of this
        // string; assert no em-dash leaks into the rendered form.
        detail.Should().NotContain("—");
    }

    [Fact]
    public void RenderDetail_HigherCount_AdvancesConnectionsPhrase()
    {
        var rule = new UnsignedFromUserPathRule();
        var detail = rule.RenderDetail(Ctx(), connectionCount: 17);
        detail.Should().Contain("Connections so far: 17.");
    }

    [Fact]
    public void CooldownMs_Is24Hours_PerBriefLock()
    {
        // brief §13: per-rule cooldown lock for UnsignedFromUserPath.
        var rule = new UnsignedFromUserPathRule();
        rule.CooldownMs.Should().Be((long)TimeSpan.FromHours(24).TotalMilliseconds);
    }
}
