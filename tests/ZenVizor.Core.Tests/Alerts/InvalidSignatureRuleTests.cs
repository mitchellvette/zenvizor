using FluentAssertions;
using ZenVizor.Core.Alerts;
using ZenVizor.Core.Attribution;
using ZenVizor.Core.Observations;
using ZenVizor.Core.Storage;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Tests.Alerts;

/// <summary>
/// Pure unit tests for <see cref="InvalidSignatureRule"/>. Predicate gates:
/// signature_status = "Invalid" exact-match AND WAN connection present.
/// Path is intentionally NOT a gate (a tampered-after-signing binary in a
/// system folder still matters), distinguishing this rule from
/// <see cref="UnsignedFromUserPathRule"/>.
/// </summary>
public sealed class InvalidSignatureRuleTests
{
    private const long T0 = 1_780_704_000_000L;

    private static AppIdentity App(
        string sig = "Invalid",
        bool userWritable = false,
        string? publisher = "Contoso Software",
        string name = "updater.exe",
        string path = @"C:\Program Files\Contoso\updater.exe") =>
        new(ImagePath: path, ImageName: name, Publisher: publisher,
            SignatureStatus: sig, IsUserWritablePath: userWritable);

    private static PendingConnection WanConn(long firstSeen = T0) => new(
        Pid: 7777,
        Protocol: Protocol.Tcp,
        RemoteAddress: "1.1.1.1",
        RemotePort: 443,
        RemoteClass: RemoteClass.Wan,
        BytesUpDelta: 0,
        BytesDownDelta: 0,
        FirstSeenUnixMs: firstSeen,
        LastSeenUnixMs: firstSeen);

    private static NewSessionContext Ctx(
        AppIdentity? app = null,
        PendingConnection? conn = null,
        int appId = 88) =>
        new(AppId: appId,
            ImagePath:          (app ?? App()).ImagePath,
            ImageName:          (app ?? App()).ImageName,
            Publisher:          (app ?? App()).Publisher,
            SignatureStatus:    (app ?? App()).SignatureStatus,
            IsUserWritablePath: (app ?? App()).IsUserWritablePath,
            WanConnection:      conn ?? WanConn(),
            FlushTimeUnixMs:    T0);

    [Fact]
    public void TryEvaluate_InvalidSignatureWithWan_ReturnsRaiseRequest()
    {
        var rule = new InvalidSignatureRule();
        var req = rule.TryEvaluate(Ctx(appId: 88));

        req.Should().NotBeNull();
        req!.Type.Should().Be(AlertType.InvalidSignature);
        req.Severity.Should().Be(NotableSeverity.Critical);
        req.SourceMonitor.Should().Be(SourceMonitor.Capture);
        req.EntityKind.Should().Be(AlertEntityKind.App);
        req.EntityRef.Should().Be("88");
        req.AppId.Should().Be(88);
        req.Title.Should().Be("Program with invalid signature talking to the network: updater.exe");
    }

    [Fact]
    public void TryEvaluate_FiresInSystemPath_NotJustUserWritable()
    {
        // Distinction from UnsignedFromUserPathRule — Invalid signature is
        // dangerous wherever the binary lives.
        var rule = new InvalidSignatureRule();
        rule.TryEvaluate(Ctx(app: App(userWritable: false))).Should().NotBeNull();
        rule.TryEvaluate(Ctx(app: App(userWritable: true))).Should().NotBeNull();
    }

    [Fact]
    public void TryEvaluate_SignedApp_ReturnsNull()
    {
        new InvalidSignatureRule().TryEvaluate(Ctx(app: App(sig: "Signed"))).Should().BeNull();
    }

    [Fact]
    public void TryEvaluate_UnsignedApp_ReturnsNull()
    {
        // Routes to UnsignedFromUserPathRule, not this one.
        new InvalidSignatureRule().TryEvaluate(Ctx(app: App(sig: "Unsigned"))).Should().BeNull();
    }

    [Fact]
    public void TryEvaluate_UncheckedSignature_ReturnsNull()
    {
        new InvalidSignatureRule().TryEvaluate(Ctx(app: App(sig: "Unchecked"))).Should().BeNull();
    }

    [Fact]
    public void RenderDetail_FirstObservation_NamesSignerAndPath()
    {
        var rule = new InvalidSignatureRule();
        var ctx = Ctx(conn: WanConn(firstSeen: T0));

        var expectedTs = DateTimeOffset
            .FromUnixTimeMilliseconds(T0)
            .LocalDateTime
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        var detail = rule.RenderDetail(ctx, connectionCount: 1);

        detail.Should().Contain("updater.exe is signed but the signature does not verify");
        detail.Should().Contain(@"Image path: C:\Program Files\Contoso\updater.exe");
        detail.Should().Contain("Signer: Contoso Software");
        detail.Should().Contain($"First connection: {expectedTs}");
        detail.Should().Contain("Connections so far: 1.");
        // Memory: feedback_no_emdash_in_ui_copy.
        detail.Should().NotContain("—");
    }

    [Fact]
    public void RenderDetail_NullPublisher_RendersAsUnknown()
    {
        var rule = new InvalidSignatureRule();
        var detail = rule.RenderDetail(Ctx(app: App(publisher: null)), connectionCount: 1);
        detail.Should().Contain("Signer: unknown");
    }

    [Fact]
    public void RenderDetail_HigherCount_AdvancesConnectionsPhrase()
    {
        var rule = new InvalidSignatureRule();
        var detail = rule.RenderDetail(Ctx(), connectionCount: 9);
        detail.Should().Contain("Connections so far: 9.");
    }

    [Fact]
    public void CooldownMs_Is24Hours_PerCatalogLock()
    {
        new InvalidSignatureRule().CooldownMs
            .Should().Be((long)TimeSpan.FromHours(24).TotalMilliseconds);
    }
}
