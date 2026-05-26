# Build-And-Deploy.ps1
#
# One-shot wrapper: builds the .cpz via VS 2008 DTE, then deploys to the
# processor. Use this for the inner-loop "I changed some code, push it"
# workflow.
#
# Usage:
#   $env:MPC_PASS = 'your-password'
#   .\tools\Build-And-Deploy.ps1
#   .\tools\Build-And-Deploy.ps1 -Target 192.168.16.240 -Slot 1

[CmdletBinding()]
param(
    [string]$Target = '192.168.16.240',
    [string]$User   = 'admin',
    [string]$Password = $env:MPC_PASS,
    [int]$Slot      = 1
)

$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'Build-Cpz.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot 'Deploy-Cpz.ps1') -Target $Target -User $User -Password $Password -Slot $Slot
exit $LASTEXITCODE
