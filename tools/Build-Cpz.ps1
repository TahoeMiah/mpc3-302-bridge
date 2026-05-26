# Build-Cpz.ps1
#
# Drives Visual Studio 2008's DTE (COM automation) to build the
# Mpc3TcpBridge SIMPL# Pro program and produce a deployable .cpz.
#
# This approach is required because the Crestron SIMPL# Pro plugin's
# post-build packaging step (manifest.info, manifest.ser, the actual
# .cpz zip) only fires when the build runs inside the VS 2008 IDE.
# Headless `msbuild` produces a .dll but no .cpz, and that .dll
# cannot be loaded by the processor on its own.
#
# Prereqs:
#   - Visual Studio 2008 Professional (SP1)
#   - Crestron SIMPL# Pro plugin installed and registered in VS 2008
#   - .NET Compact Framework 3.5 SDK
#   - This script must run as the same user that activated the VS
#     license (DTE will refuse otherwise).
#
# Usage:
#   .\tools\Build-Cpz.ps1
#
# Output: crestron\Mpc3TcpBridge\bin\Release\Mpc3TcpBridge.cpz

[CmdletBinding()]
param(
    [string]$SolutionPath = (Join-Path $PSScriptRoot '..\crestron\Mpc3TcpBridge.sln'),
    [string]$Configuration = 'Release',
    [int]$WaitMaxSeconds = 180
)

$ErrorActionPreference = 'Stop'

$SolutionPath = (Resolve-Path $SolutionPath).Path
if (-not (Test-Path $SolutionPath)) {
    Write-Host "ERROR: solution not found: $SolutionPath" -ForegroundColor Red
    exit 1
}

Write-Host "Launching VS 2008 DTE..." -ForegroundColor Cyan
$dte = New-Object -ComObject VisualStudio.DTE.9.0
$dte.UserControl = $false   # never hand the IDE window to the user

try {
    Write-Host "Opening solution: $SolutionPath" -ForegroundColor Cyan
    $dte.Solution.Open($SolutionPath)
    Start-Sleep -Seconds 3   # let DTE settle - plugin loads its own targets here

    $sb = $dte.Solution.SolutionBuild
    $sb.SolutionConfigurations.Item($Configuration).Activate()

    Write-Host "Building ($Configuration)..." -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $sb.Build($false)        # async build

    while ($sb.BuildState -ne 3 -and $sw.Elapsed.TotalSeconds -lt $WaitMaxSeconds) {
        Start-Sleep -Milliseconds 500
    }
    $sw.Stop()

    if ($sb.BuildState -ne 3) {
        Write-Host "ERROR: build timed out after $WaitMaxSeconds seconds" -ForegroundColor Red
        exit 2
    }

    $rc = $sb.LastBuildInfo
    if ($rc -eq 0) {
        Write-Host "Build succeeded in $([math]::Round($sw.Elapsed.TotalSeconds, 1))s" -ForegroundColor Green
    } else {
        Write-Host "WARNING: $rc project(s) failed to build" -ForegroundColor Yellow
    }

    Start-Sleep -Seconds 3   # let the plugin's post-build packaging finish
} finally {
    Write-Host "Quitting DTE..." -ForegroundColor Cyan
    try { $dte.Quit() } catch { }
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($dte) | Out-Null
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}

$cpz = Get-ChildItem (Split-Path $SolutionPath -Parent) -Filter '*.cpz' -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($cpz) {
    Write-Host ''
    Write-Host "PRODUCED:  $($cpz.FullName)" -ForegroundColor Green
    Write-Host "Size:      $([math]::Round($cpz.Length / 1024, 1)) KB"
    Write-Host "Modified:  $($cpz.LastWriteTime)"
} else {
    Write-Host ''
    Write-Host "WARNING: no .cpz produced. The SIMPL# Pro plugin may have failed silently." -ForegroundColor Yellow
    Write-Host "         Open $SolutionPath in VS 2008 manually and try Build > Rebuild." -ForegroundColor Yellow
    exit 3
}
