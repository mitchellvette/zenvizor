<#
.SYNOPSIS
    Phase 6.7 P4 QA — fire the InvalidSignatureRule.

.DESCRIPTION
    Creates a short-lived self-signed code-signing cert in
    CurrentUser\My (NOT installed to TrustedRoot), clones curl.exe to
    %TEMP% under a fresh randomized name, signs the clone with the
    untrusted cert, then runs the signed-but-untrusted binary against
    a WAN URL. ZenVizor's signature verifier classifies the signed-
    but-untrusted state as "Invalid", which is the InvalidSignatureRule
    predicate's trigger.

    Run as Administrator (New-SelfSignedCertificate + Set-AuthenticodeSignature
    don't require elevation but the WinVerifyTrust path in the service
    is cleaner when the cert lives in CurrentUser\My, so elevation is
    the path of least surprise).

.NOTES
    Cleanup: Remove the temp binary and the self-signed cert when done.
    The cert lives at Cert:\CurrentUser\My\<thumbprint>.
#>
#requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$cert = New-SelfSignedCertificate `
    -Subject "CN=ZenVizor QA Invalid-Sig" `
    -Type CodeSigningCert `
    -CertStoreLocation Cert:\CurrentUser\My `
    -NotAfter (Get-Date).AddDays(7)
Write-Host "Self-signed cert thumbprint: $($cert.Thumbprint)"

$tempDir = $env:TEMP
$source  = "$env:WINDIR\System32\curl.exe"
$dest    = Join-Path $tempDir ("zenvizor-invalid-{0}.exe" -f (Get-Random))
Copy-Item -Path $source -Destination $dest -Force
Write-Host "Cloned curl.exe to: $dest"

Set-AuthenticodeSignature -FilePath $dest -Certificate $cert | Out-Null
$sig = Get-AuthenticodeSignature -FilePath $dest
Write-Host "Signature status: $($sig.Status)"
# Expected: NotTrusted (the cert chain doesn't lead to a trusted root).
# ZenVizor's verifier classifies this as 'Invalid'.

Write-Host "Making WAN connection from the signed-but-untrusted binary..."
& $dest --silent --output NUL https://1.1.1.1/cdn-cgi/trace
Write-Host "Done. The alert should land within ~10 s (one flush cycle)."
