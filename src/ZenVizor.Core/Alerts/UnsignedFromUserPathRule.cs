using System.Globalization;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Alerts;

/// <summary>
/// The Phase-6 first real alert rule (brief §13 lock). Fires when an
/// attributed process whose image lives under a user-writable path AND
/// whose signer reports as <c>Unsigned</c> makes an outbound WAN
/// connection. Severity locked Critical per catalog §1.4.
/// <para>
/// The signature-status gate is exact-match on <c>"Unsigned"</c>:
/// <c>"Invalid"</c> routes to a future <c>InvalidSignature</c> rule;
/// <c>"Unchecked"</c> means the SignerCache hasn't classified this image
/// yet and we never want to claim "this is unsigned" without evidence
/// (CLAUDE.md "never fabricate precision"). <c>"Signed"</c> is the safe
/// path.
/// </para>
/// </summary>
public sealed class UnsignedFromUserPathRule : IAlertRule
{
    private const string SignatureStatusUnsigned = "Unsigned";

    /// <summary>24h cooldown per brief §13. Hard-coded; future rules with
    /// per-instance configurability can pull from settings.</summary>
    public long CooldownMs => TimeSpan.FromHours(24).Ticks / TimeSpan.TicksPerMillisecond;

    public RaiseRequest? TryEvaluate(NewSessionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // All three predicates must hold. None compose with the others'
        // failure paths — order is for readability not short-circuit logic.
        if (!string.Equals(ctx.SignatureStatus, SignatureStatusUnsigned, StringComparison.Ordinal))
            return null;
        if (!ctx.IsUserWritablePath)
            return null;
        if (ctx.WanConnection is null)
            return null;

        return new RaiseRequest(
            Type:          AlertType.UnsignedFromUserPath,
            Severity:      NotableSeverity.Critical,
            SourceMonitor: SourceMonitor.Capture,
            EntityKind:    AlertEntityKind.App,
            EntityRef:     ctx.AppId.ToString(CultureInfo.InvariantCulture),
            AppId:         ctx.AppId,
            Title:         $"Unsigned program talking to the network: {ctx.ImageName}");
    }

    public string RenderDetail(NewSessionContext ctx, int connectionCount)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (connectionCount <= 0) connectionCount = 1;

        var firstConnectionLocal =
            DateTimeOffset.FromUnixTimeMilliseconds(ctx.WanConnection.FirstSeenUnixMs)
                          .LocalDateTime
                          .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        // Catalog §2 template, verbatim. Plain English, no em-dashes (memory
        // feedback_no_emdash_in_ui_copy). The "Signer: none (unsigned)"
        // phrase is the locked rendering for the Unsigned signature_status —
        // future rules that fire on other signature states render their
        // own signer phrase.
        return
            $"{ctx.ImageName} is running from a user-writable folder and started making " +
            $"network connections. Image path: {ctx.ImagePath}. " +
            $"Signer: none (unsigned). " +
            $"First connection: {firstConnectionLocal}. " +
            $"Connections so far: {connectionCount}.";
    }
}
