<#
.SYNOPSIS
    Repeatable attribution-reliability verification for Phase 3.

.DESCRIPTION
    Runs N curl downloads against a known endpoint and verifies that ZenVizor
    attributes EVERY one of them. Fails loudly if any run is missed.

    Each iteration:
      1. Snapshot baseline counters via `zvctl stats`.
      2. Run a curl download to NUL (default: 50 MB from speed.cloudflare.com).
      3. Snapshot post-curl counters.
      4. Pull `zvctl snapshot --json` and check whether curl.exe appears with
         the expected order-of-magnitude bytes.
      5. Compute the delta of ObservationsUnattributed -- should be 0 (or near).

    Pass criteria (per iteration):
      - curl.exe present in the snapshot.
      - curl.exe BytesDownTotal >= 80 % of the download size (allows for ETW
        sampling jitter but catches a "we missed most of it" failure).
      - ObservationsUnattributed delta is 0 (strict).

    The script reports a per-iteration PASS/FAIL line and a final summary.
    Exit code 0 if all iterations passed, 1 otherwise.

.PARAMETER Iterations
    Number of curl runs to perform. Default: 5.

.PARAMETER Bytes
    Bytes per curl download. Default: 50000000 (50 MB).

.PARAMETER DelayMs
    Wait between iterations to let the rolling window settle.
    Default: 6000 (one flush cycle plus a bit).

.PARAMETER ZvctlPath
    Override the zvctl binary location. Default: the Release output of
    src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe.

.EXAMPLE
    .\scripts\verify-attribution.ps1
    .\scripts\verify-attribution.ps1 -Iterations 10 -Bytes 100000000
#>

[CmdletBinding()]
param(
    [int] $Iterations = 5,
    [long] $Bytes = 50000000,
    [int] $DelayMs = 6000,
    [string] $ZvctlPath = (Join-Path $PSScriptRoot '..\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ZvctlPath)) {
    Write-Error "zvctl not found at $ZvctlPath. Run 'dotnet build -c Release' first."
}

# Verify the service is up before we start. Don't use 2>&1 -- on PS 5.1 it
# wraps native stderr in ErrorRecord and corrupts $?; we check $LASTEXITCODE.
$status = & $ZvctlPath status
if ($LASTEXITCODE -ne 0) {
    Write-Error "zvctl status failed -- is the service running? (last exit code $LASTEXITCODE)"
}
Write-Host "Service is up. Running $Iterations iterations of curl $Bytes-byte download." -ForegroundColor Cyan
Write-Host ""

function Get-Stats {
    $raw = & $ZvctlPath stats
    if ($LASTEXITCODE -ne 0) { throw "zvctl stats failed: $raw" }
    $seen = ($raw | Select-String 'Observations seen\s+:\s+(\d+)').Matches.Groups[1].Value -as [long]
    $una  = ($raw | Select-String 'Observations unattributed\s+:\s+(\d+)').Matches.Groups[1].Value -as [long]
    [pscustomobject]@{ Seen = $seen; Unattributed = $una }
}

function Get-Snapshot {
    $json = & $ZvctlPath snapshot --json
    if ($LASTEXITCODE -ne 0) { throw "zvctl snapshot failed: $json" }
    return ($json | ConvertFrom-Json)
}

$results = @()
$passes = 0
$fails = 0

for ($i = 1; $i -le $Iterations; $i++) {
    Write-Host ("=== Iteration {0}/{1} ===" -f $i, $Iterations) -ForegroundColor Yellow

    # Baseline counters BEFORE the curl.
    $before = Get-Stats
    Write-Host ("  baseline: seen={0} unattributed={1}" -f $before.Seen, $before.Unattributed)

    # Curl. Capture the elapsed time for context.
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & curl.exe --silent -o NUL "https://speed.cloudflare.com/__down?bytes=$Bytes" | Out-Null
    $sw.Stop()
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "  curl exited non-zero ($LASTEXITCODE); skipping iteration"
        $fails++
        continue
    }
    Write-Host ("  curl finished in {0:n0} ms" -f $sw.ElapsedMilliseconds)

    # Give the service time for ETW events to drain through the kernel buffer
    # into the partial accumulator. 500 ms was too tight under load -- TraceEvent
    # buffer flush cadence can be 250-1000 ms depending on rate.
    Start-Sleep -Milliseconds 1500

    $after = Get-Stats
    Write-Host ("  after:    seen={0} unattributed={1}" -f $after.Seen, $after.Unattributed)

    $seenDelta = $after.Seen - $before.Seen
    $unaDelta  = $after.Unattributed - $before.Unattributed
    Write-Host ("  delta:    seen=+{0} unattributed=+{1}" -f $seenDelta, $unaDelta)

    # Snapshot -- does curl appear?
    $snap = Get-Snapshot
    $curlRow = $snap.Payload.Apps | Where-Object { $_.ImageName -ieq 'curl.exe' }

    $reasons = @()
    if (-not $curlRow) {
        $reasons += 'curl.exe NOT in snapshot Apps[]'
    }
    else {
        $minBytes = [long]($Bytes * 0.8)
        if ($curlRow.BytesDownTotal -lt $minBytes) {
            $reasons += ("curl bytes_down={0} < 80% of {1}" -f $curlRow.BytesDownTotal, $Bytes)
        }
    }
    if ($unaDelta -gt 0) {
        $reasons += ("unattributed observations grew by {0}" -f $unaDelta)
    }

    if ($reasons.Count -eq 0) {
        Write-Host ("  RESULT:   PASS (curl bytes_down={0})" -f $curlRow.BytesDownTotal) -ForegroundColor Green
        $passes++
    }
    else {
        Write-Host ("  RESULT:   FAIL  -- {0}" -f ($reasons -join '; ')) -ForegroundColor Red
        $fails++
    }

    $results += [pscustomobject]@{
        Iter            = $i
        ElapsedMs       = $sw.ElapsedMilliseconds
        SeenDelta       = $seenDelta
        UnattributedDelta = $unaDelta
        CurlBytesDown   = $(if ($curlRow) { $curlRow.BytesDownTotal } else { 0 })
        Pass            = ($reasons.Count -eq 0)
    }

    if ($i -lt $Iterations) {
        Start-Sleep -Milliseconds $DelayMs
    }
}

Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host
Write-Host ("Passes: {0} / {1}" -f $passes, $Iterations) -ForegroundColor $(if ($fails -eq 0) { 'Green' } else { 'Red' })
Write-Host ("Fails:  {0} / {1}" -f $fails,  $Iterations) -ForegroundColor $(if ($fails -eq 0) { 'Green' } else { 'Red' })

if ($fails -gt 0) { exit 1 } else { exit 0 }
