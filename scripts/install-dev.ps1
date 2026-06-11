<#
.SYNOPSIS
    Register the ZenVizor service from the Release build output for development.

.DESCRIPTION
    Builds the Service project in Release if needed, then registers it with the
    Windows Service Control Manager via sc.exe and starts it. Must be run from
    an elevated PowerShell. This is the dev path -- the .msi installer (Phase 6)
    handles production install.

.PARAMETER NoBuild
    Skip the dotnet build step. Use when you've already built.

.PARAMETER StartMode
    auto | demand | disabled  (default: demand -- start on user request)
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [ValidateSet('auto','demand','disabled')]
    [string]$StartMode = 'demand'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()
      ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "install-dev.ps1 must be run from an elevated PowerShell."
    exit 1
}

$repoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..')
$serviceCsproj = Join-Path $repoRoot 'src\ZenVizor.Service\ZenVizor.Service.csproj'
$serviceName = 'ZenVizor'

if (-not $NoBuild) {
    Write-Host "Building ZenVizor.Service (Release)..." -ForegroundColor Cyan
    & dotnet build $serviceCsproj -c Release --nologo | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

$serviceDll = Join-Path $repoRoot 'src\ZenVizor.Service\bin\Release\net10.0-windows\ZenVizor.Service.dll'
$serviceExe = Join-Path $repoRoot 'src\ZenVizor.Service\bin\Release\net10.0-windows\ZenVizor.Service.exe'

# Worker template produces both a .dll and a small launcher .exe on Windows.
if (-not (Test-Path $serviceExe)) {
    Write-Error "Service executable not found at $serviceExe -- did the build succeed?"
    exit 1
}

# Stop + delete prior registration so we always pick up the latest binary path.
$existing = & sc.exe query $serviceName 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Existing $serviceName service found -- stopping and removing..." -ForegroundColor Yellow
    & sc.exe stop $serviceName | Out-Null

    # Poll sc query for STOPPED instead of a fixed Start-Sleep: a long-running
    # service (e.g. waiting on its capture pipeline tear-down) can outlast 500ms
    # and produce a spurious "Service cannot be stopped at this time" on the
    # following sc delete. Bound the wait so we don't hang here if Windows
    # gets stuck.
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $query = & sc.exe query $serviceName 2>$null
        if ($LASTEXITCODE -ne 0) { break }                  # service already gone
        if ($query -match 'STATE\s*:\s*\d+\s+STOPPED') { break }
        Start-Sleep -Milliseconds 200
    }

    & sc.exe delete $serviceName | Out-Null

    # Poll for the SCM to drop the entry. sc delete is async on names with an
    # open handle: a fast follow-up sc create lands on the same name and fails
    # with 1072 ERROR_SERVICE_MARKED_FOR_DELETE.
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        & sc.exe query $serviceName 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { break }
        Start-Sleep -Milliseconds 200
    }
}

Write-Host "Registering $serviceName from $serviceExe..." -ForegroundColor Cyan
& sc.exe create $serviceName binPath= "`"$serviceExe`"" start= $StartMode DisplayName= "ZenVizor" | Write-Host
if ($LASTEXITCODE -ne 0) { throw "sc create failed." }

& sc.exe description $serviceName "ZenVizor passive network monitor (dev install)." | Out-Null

Write-Host "Starting $serviceName..." -ForegroundColor Cyan
& sc.exe start $serviceName | Write-Host
if ($LASTEXITCODE -ne 0) { throw "sc start failed." }

Write-Host ""
Write-Host "Service is installed and running. Verify with:" -ForegroundColor Green
Write-Host "  sc.exe query $serviceName"
Write-Host "  zvctl ping       (build src/ZenVizor.Cli first)"
Write-Host "  Get-EventLog -LogName Application -Source ZenVizor -Newest 5"
