// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Core.Alerts;

/// <summary>
/// Fires when an attributed process whose signature reports as
/// <c>"Invalid"</c> makes an outbound WAN connection. Severity locked
/// Critical per catalog §1.4. Source <see cref="SourceMonitor.Capture"/>.
/// <para>
/// Sibling of <see cref="UnsignedFromUserPathRule"/>. Distinguishing
/// predicate: <c>Invalid</c> signature_status fires regardless of path
/// (a tampered-after-signing or revoked-certificate binary is dangerous
/// in a system folder, not just a user-writable one), while
/// <see cref="UnsignedFromUserPathRule"/> requires <c>Unsigned</c> +
/// user-writable. <c>WinVerifyTrustSignatureVerifier</c> classifies the
/// three states; this rule trusts that classification.
/// </para>
/// </summary>
public sealed class InvalidSignatureRule : IAlertRule
{
    private const string SignatureStatusInvalid = "Invalid";

    /// <summary>24h cooldown per catalog §1.4 critical-rule lock.</summary>
    public long CooldownMs => TimeSpan.FromHours(24).Ticks / TimeSpan.TicksPerMillisecond;

    public RaiseRequest? TryEvaluate(NewSessionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (!string.Equals(ctx.SignatureStatus, SignatureStatusInvalid, StringComparison.Ordinal))
            return null;
        if (ctx.WanConnection is null)
            return null;

        return new RaiseRequest(
            Type:          AlertType.InvalidSignature,
            Severity:      NotableSeverity.Critical,
            SourceMonitor: SourceMonitor.Capture,
            EntityKind:    AlertEntityKind.App,
            EntityRef:     ctx.AppId.ToString(CultureInfo.InvariantCulture),
            AppId:         ctx.AppId,
            Title:         $"Program with invalid signature talking to the network: {ctx.ImageName}");
    }

    public string RenderDetail(NewSessionContext ctx, int connectionCount)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (connectionCount <= 0) connectionCount = 1;

        var firstConnectionLocal =
            DateTimeOffset.FromUnixTimeMilliseconds(ctx.WanConnection.FirstSeenUnixMs)
                          .LocalDateTime
                          .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        var publisher = string.IsNullOrEmpty(ctx.Publisher) ? "unknown" : ctx.Publisher;

        return
            $"{ctx.ImageName} is signed but the signature does not verify (tampered, expired, or revoked). " +
            $"Image path: {ctx.ImagePath}. " +
            $"Signer: {publisher}. " +
            $"First connection: {firstConnectionLocal}. " +
            $"Connections so far: {connectionCount}.";
    }
}
