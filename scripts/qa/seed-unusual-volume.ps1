<#
.SYNOPSIS
    Phase 6.7 P4 QA — seed synthetic baseline data + fire UnusualDailyVolumeRule.

.DESCRIPTION
    The UnusualDailyVolumeRule reads traffic_daily over the past 15 UTC
    days. To trigger it on demand without waiting 14 days for natural
    baseline:

      1. Pick an existing app from the apps table (must already exist —
         the rule joins against apps).
      2. INSERT 14 synthetic traffic_daily rows for days -2 through -15
         at -BaselineMb each (default 100 MB).
      3. INSERT a spike row for yesterday at -SpikeMb (default 300 MB).
      4. Call zvctl alerts run-rollup-rules-now to force re-evaluation
         immediately (the rule's date gate would otherwise wait for the
         next UTC midnight).

    The %ProgramData%\ZenVizor\zenvizor.db file is ACL'd to SYSTEM +
    Administrators only. Run as Administrator OR via psexec -s.

.NOTES
    sqlite3.exe is required. winget install --id SQLite.SQLite if missing.

    Cleanup: pass -Cleanup to delete the synthetic rows. They won't
    survive a Reset history either way; the cleanup just lets you tidy
    up without wiping unrelated data.

.PARAMETER AppImage
    image_name to seed against. Pick something already in apps. Default
    "chrome.exe".

.PARAMETER BaselineMb
    Per-day MB for the 14 baseline days.

.PARAMETER SpikeMb
    MB for the yesterday spike row. Must clear k * BaselineMb AND
    (SpikeMb - BaselineMb) >= 50 MB to trigger the rule.

.PARAMETER Cleanup
    Delete the previously-seeded synthetic rows rather than seed new ones.

.PARAMETER ZvctlPath
    Path to zvctl.exe; defaults to the dev build output.
#>
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$AppImage = "chrome.exe",
    [int]$BaselineMb = 100,
    [int]$SpikeMb = 300,
    [switch]$Cleanup,
    [string]$ZvctlPath = "C:\dev\zenvizor\src\ZenVizor.Cli\bin\Release\net10.0-windows\zvctl.exe"
)

$ErrorActionPreference = 'Stop'

$db = "$env:ProgramData\ZenVizor\zenvizor.db"
if (-not (Test-Path $db))
{
    throw "Database not found at $db. Install the dev service first via scripts\install-dev.ps1."
}

$sqlite3 = Get-Command sqlite3.exe -ErrorAction SilentlyContinue
if (-not $sqlite3)
{
    throw "sqlite3.exe not found. winget install --id SQLite.SQLite, then re-run."
}

# Look up the app_id. The rule joins against apps so an unknown image
# name silently no-ops.
$appId = sqlite3.exe -readonly $db "SELECT app_id FROM apps WHERE image_name='$AppImage' LIMIT 1;"
if (-not $appId)
{
    throw "No row in apps with image_name='$AppImage'. Browse with $AppImage briefly so the attribution pipeline registers it, then re-run."
}
Write-Host "Resolved $AppImage to app_id $appId."

# UTC day alignment matches BucketAligner.AlignToDay: 86_400_000 ms.
$nowMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$dayMs = 86400000L
$todayMs = $nowMs - ($nowMs % $dayMs)
$yesterdayMs = $todayMs - $dayMs

if ($Cleanup)
{
    Write-Host "Cleaning up synthetic rows for app_id $appId..."
    $cutoff = $todayMs - (16 * $dayMs)
    sqlite3.exe $db "DELETE FROM traffic_daily WHERE app_id=$appId AND bucket_start >= $cutoff AND bucket_start < $todayMs;"
    Write-Host "Done. Note: this also clears any real rows in the same window."
    return
}

# Synthetic baseline: 14 rows at $BaselineMb each, half up + half down.
$halfBaseline = [int]([math]::Round($BaselineMb * 1024 * 1024 / 2))
for ($i = 2; $i -le 15; $i++)
{
    $bucket = $todayMs - ($i * $dayMs)
    # WAN bytes (remote_class = 'Wan') is the realistic shape.
    sqlite3.exe $db "INSERT OR REPLACE INTO traffic_daily (app_id, bucket_start, remote_class, bytes_up, bytes_down) VALUES ($appId, $bucket, 'Wan', $halfBaseline, $halfBaseline);"
}

# Spike row for yesterday.
$halfSpike = [int]([math]::Round($SpikeMb * 1024 * 1024 / 2))
sqlite3.exe $db "INSERT OR REPLACE INTO traffic_daily (app_id, bucket_start, remote_class, bytes_up, bytes_down) VALUES ($appId, $yesterdayMs, 'Wan', $halfSpike, $halfSpike);"

Write-Host "Seeded baseline ($BaselineMb MB/day for 14 days) + yesterday spike ($SpikeMb MB)."
Write-Host "Calling zvctl alerts run-rollup-rules-now to force evaluation..."

& $ZvctlPath alerts run-rollup-rules-now
Write-Host "Done. Alert should appear in the Alerts feed and via zvctl alerts list --type UnusualDailyVolume."
