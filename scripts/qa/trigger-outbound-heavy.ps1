<#
.SYNOPSIS
    Phase 6.7 P4 QA — fire the OutboundHeavyRule.

.DESCRIPTION
    Creates a ~15 MB binary payload in %TEMP%, POSTs it via curl to
    Cloudflare's speedtest upload endpoint, which returns a tiny ack
    response. That makes the upload dominate the download by several
    orders of magnitude, well above the 3:1 ratio lock, and clears the
    default 10 MB floor in a single shot.

    NOTE: Earlier versions of this script POSTed to httpbin.org/post.
    That endpoint echoes the entire POST body back as JSON, which
    causes the download to roughly match the upload — sometimes
    tripping the LargeDownloadRule instead of OutboundHeavyRule. The
    Cloudflare __up endpoint returns a tiny JSON timing response, so
    the bytes_up / bytes_down ratio is predictable.

    Rule defaults: 10 MB floor, 3:1 ratio, 15-min rolling window. If
    you've tuned the floor higher in Settings, bump the payload size
    via the -PayloadMb parameter so it still clears.

    No elevation required.
#>
[CmdletBinding()]
param(
    [int]$PayloadMb = 15
)

$ErrorActionPreference = 'Stop'

$payload = Join-Path $env:TEMP "zenvizor-outbound-payload.bin"
Write-Host "Generating $PayloadMb MB payload at $payload..."
$bytes = New-Object byte[] (1MB)
$file = [System.IO.File]::Create($payload)
try
{
    for ($i = 0; $i -lt $PayloadMb; $i++)
    {
        $file.Write($bytes, 0, $bytes.Length)
    }
}
finally
{
    $file.Dispose()
}

Write-Host "POSTing $PayloadMb MB to https://speed.cloudflare.com/__up via curl..."
& curl.exe --silent --output NUL `
    --data-binary "@$payload" `
    https://speed.cloudflare.com/__up

Write-Host "Done. The alert should land within ~10 s (one flush cycle)."
Write-Host "Cleanup: Remove-Item $payload"
