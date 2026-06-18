<#
.SYNOPSIS
    Phase 6.7 P4 QA — fire the FirstRunWanTalkerRule.

.DESCRIPTION
    Copies curl.exe to a fresh randomized filename in %TEMP%, then runs
    it against a WAN URL within the rule's 60s first-seen window. The
    new image path has never been seen before, so the attribution
    pipeline INSERTs a new apps row with first_seen = now; the WAN
    connection that follows clears the rule's predicate.

    No elevation required.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$source = "$env:WINDIR\System32\curl.exe"
$dest   = Join-Path $env:TEMP ("zenvizor-firstrun-{0}.exe" -f (Get-Random))
Copy-Item -Path $source -Destination $dest -Force
Write-Host "Cloned curl.exe to: $dest"

Write-Host "Making WAN connection from the freshly-installed binary..."
& $dest --silent --output NUL https://1.1.1.1/cdn-cgi/trace
Write-Host "Done. The alert should land within ~10 s (one flush cycle)."
