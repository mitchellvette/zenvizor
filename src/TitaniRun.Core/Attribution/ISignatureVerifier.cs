namespace TitaniRun.Core.Attribution;

/// <summary>
/// Authenticode signature check. Implementations MUST use offline verification
/// (CLAUDE.md invariant #1 — zero outbound network from our processes). The
/// Windows implementation passes <c>WTD_REVOKE_NONE</c> to <c>WinVerifyTrust</c>.
/// </summary>
public interface ISignatureVerifier
{
    SignatureVerificationResult Verify(string imagePath);
}

/// <param name="Status">One of <c>"Signed"</c>, <c>"Unsigned"</c>, <c>"Invalid"</c>, <c>"Unchecked"</c>.</param>
/// <param name="Publisher">
/// Subject CN of the signing certificate when <paramref name="Status"/> is
/// <c>"Signed"</c>; otherwise <c>null</c>.
/// </param>
public sealed record SignatureVerificationResult(string Status, string? Publisher)
{
    public static SignatureVerificationResult Unchecked { get; } =
        new(Status: "Unchecked", Publisher: null);
}
