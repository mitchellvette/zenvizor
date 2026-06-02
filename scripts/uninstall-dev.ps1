<#
.SYNOPSIS
    Stop and unregister the ZenVizor dev service.

.DESCRIPTION
    Mirror of install-dev.ps1. Does NOT delete %ProgramData%\ZenVizor\ -- the
    SQLite database stays so re-installs preserve history. Pass -PurgeData to
    remove the data directory as well.
#>
[CmdletBinding()]
param(
    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()
      ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "uninstall-dev.ps1 must be run from an elevated PowerShell."
    exit 1
}

$serviceName = 'ZenVizor'
$dataDir     = Join-Path $env:ProgramData 'ZenVizor'

$existing = & sc.exe query $serviceName 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Stopping $serviceName..." -ForegroundColor Cyan
    & sc.exe stop $serviceName | Out-Null
    Start-Sleep -Milliseconds 500

    Write-Host "Deleting $serviceName..." -ForegroundColor Cyan
    & sc.exe delete $serviceName | Out-Null
} else {
    Write-Host "$serviceName is not registered -- nothing to delete." -ForegroundColor Yellow
}

if ($PurgeData) {
    if (Test-Path $dataDir) {
        Write-Host "Removing $dataDir..." -ForegroundColor Cyan
        Remove-Item -Recurse -Force $dataDir
    }
} else {
    Write-Host "Data directory preserved at $dataDir (use -PurgeData to remove)." -ForegroundColor Yellow
}

Write-Host "Done." -ForegroundColor Green
