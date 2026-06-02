using FluentAssertions;
using ZenVizor.Attribution.Authenticode;

namespace ZenVizor.Attribution.Tests;

/// <summary>
/// Headless unit tests for the verifier's pure-managed helpers and missing-file
/// behavior. The native WinVerifyTrust path is exercised in the manual gates
/// (real signed/unsigned binaries on the dev box), not on CI, since reproducible
/// signed test fixtures across CI runners are fragile (cert expiry, store
/// contents).
/// </summary>
public sealed class WinVerifyTrustSignatureVerifierTests
{
    [Fact]
    public void Verify_MissingFile_ReturnsUnchecked()
    {
        var verifier = new WinVerifyTrustSignatureVerifier();
        var bogus = Path.Combine(Path.GetTempPath(), $"zenvizor-missing-{Guid.NewGuid():N}.exe");

        var result = verifier.Verify(bogus);

        result.Status.Should().Be("Unchecked");
        result.Publisher.Should().BeNull();
    }

    [Fact]
    public void Verify_NullOrEmptyPath_ReturnsUnchecked()
    {
        var verifier = new WinVerifyTrustSignatureVerifier();

        verifier.Verify("").Status.Should().Be("Unchecked");
        verifier.Verify(null!).Status.Should().Be("Unchecked");
    }

    [Theory]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
        "Microsoft Corporation")]
    [InlineData("CN=Google LLC, O=Google LLC, L=Mountain View, C=US", "Google LLC")]
    [InlineData("O=NoCN Inc, C=US", null)]
    [InlineData("", null)]
    [InlineData("CN=\"Some, Quoted, Name\", O=Acme", "Some, Quoted, Name")]
    [InlineData("cn=lowercase, O=Acme", "lowercase")]
    public void ExtractSubjectCommonName_ParsesVariousSubjects(string subject, string? expected)
    {
        WinVerifyTrustSignatureVerifier.ExtractSubjectCommonName(subject).Should().Be(expected);
    }
}
