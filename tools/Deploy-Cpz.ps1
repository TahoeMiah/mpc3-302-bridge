# Deploy-Cpz.ps1
#
# Pushes Mpc3TcpBridge.cpz to the processor and loads it into a program slot.
#
# Auto-discovers the processor's SSH host key via ssh-keyscan (so plink
# never prompts for host-key trust) and passes it to plink via -hostkey.
# This means the script works fresh without any one-time setup.
#
# Prereqs:
#   - PuTTY (plink.exe + pscp.exe) on PATH or at C:\Program Files\PuTTY\
#   - Windows OpenSSH ssh-keyscan.exe (built into Windows 10/11)
#   - Processor admin password (provide via -Password or $env:MPC_PASS)
#
# Usage:
#   $env:MPC_PASS = 'your-password'
#   .\tools\Deploy-Cpz.ps1                       # 192.168.16.240, slot 1, latest .cpz
#   .\tools\Deploy-Cpz.ps1 -Target 192.168.16.240 -Slot 1
#   .\tools\Deploy-Cpz.ps1 -Cpz '.\path\to\Mpc3TcpBridge.cpz'

[CmdletBinding()]
param(
    [string]$Target   = '192.168.16.240',
    [string]$User     = 'admin',
    [string]$Password = $env:MPC_PASS,
    [int]$Slot        = 1,
    [string]$Cpz      = (Join-Path $PSScriptRoot '..\crestron\Mpc3TcpBridge\bin\Release\Mpc3TcpBridge.cpz')
)

$ErrorActionPreference = 'Stop'

if (-not $Password) {
    Write-Host "ERROR: set `$env:MPC_PASS or pass -Password" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $Cpz)) {
    Write-Host "ERROR: .cpz not found at $Cpz - run .\tools\Build-Cpz.ps1 first" -ForegroundColor Red
    exit 1
}
$Cpz = (Resolve-Path $Cpz).Path

$plink = if (Get-Command plink -EA SilentlyContinue) { (Get-Command plink).Source } else { 'C:\Program Files\PuTTY\plink.exe' }
$pscp  = if (Get-Command pscp  -EA SilentlyContinue) { (Get-Command pscp ).Source } else { 'C:\Program Files\PuTTY\pscp.exe'  }
if (-not (Test-Path $plink)) { throw "plink.exe not found" }
if (-not (Test-Path $pscp))  { throw "pscp.exe not found"  }

# ---- 1. fetch the processor's host key fingerprint ----
# ssh-keyscan returns multiple key types; the SHA-256 fingerprint of any
# one of them is enough for plink's -hostkey arg. Empirically the ECDSA
# key is the one plink picks first for the kex handshake on Crestron.
Write-Host "[deploy] fetching host key for $Target" -ForegroundColor Cyan
# Windows PowerShell 5.1 turns native-cmd stderr into ErrorRecords even with
# `2>$null`, which trips ErrorActionPreference=Stop. Route through cmd.exe so
# the redirect happens before PS sees it.
$keyscan = (Get-Command ssh-keyscan -EA SilentlyContinue).Source
if (-not $keyscan) { $keyscan = 'C:\Windows\System32\OpenSSH\ssh-keyscan.exe' }
if (-not (Test-Path $keyscan)) { throw "ssh-keyscan.exe not found" }
$keys = cmd /c "`"$keyscan`" -t ecdsa,rsa -T 5 $Target 2>nul"
if (-not $keys) { throw "ssh-keyscan returned no keys for $Target - is the processor reachable?" }
$ecdsa = $keys | Where-Object { $_ -match '^\S+\s+ecdsa-' } | Select-Object -First 1
if (-not $ecdsa) { throw "no ECDSA host key returned by $Target" }
$blob = ($ecdsa -split '\s+')[2]
$bytes = [Convert]::FromBase64String($blob)
$hash = [Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
$hk = 'SHA256:' + ([Convert]::ToBase64String($hash).TrimEnd('='))
Write-Host "[deploy] host key $hk" -ForegroundColor DarkGray

function Plink([string]$Cmd, [int]$Timeout = 30) {
    & $plink -ssh -hostkey $hk -pw $Password -batch "$User@$Target" $Cmd 2>&1
}

# ---- 2. transfer .cpz ----
Write-Host "[deploy] uploading $(Split-Path $Cpz -Leaf) ($([math]::Round((Get-Item $Cpz).Length/1KB,1)) KB)" -ForegroundColor Cyan
$slotPath = "/Program0$Slot"
if ($Slot -ge 10) { $slotPath = "/Program$Slot" }
$dest = "${User}@${Target}:$slotPath/Mpc3TcpBridge.cpz"
& $pscp -batch -hostkey $hk -pw $Password $Cpz $dest 2>&1 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
if ($LASTEXITCODE -ne 0) { throw "pscp failed ($LASTEXITCODE)" }

# ---- 3. progload (extracts the .cpz and starts the program) ----
Write-Host "[deploy] progload -P:$('{0:D2}' -f $Slot)" -ForegroundColor Cyan
$slotArg = '{0:D2}' -f $Slot
Plink "progload -P:$slotArg" | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }

# ---- 4. wait for startup, then sample console output ----
Write-Host "[deploy] waiting 15s for startup..."
Start-Sleep -Seconds 15
Write-Host "[deploy] last err entries (program-related):" -ForegroundColor Cyan
$err = Plink 'err'
($err -join "`n") -replace '\x1b\[[0-9;]*m', '' |
    Out-String -Stream |
    Select-String -Pattern '\[tcp\]|\[mpc3\]|\[settings\]|Program 1 Started|App 1.*Error' |
    Select-Object -Last 8 |
    ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }

Write-Host ''
Write-Host "[deploy] done." -ForegroundColor Green
Write-Host "         TCP:   nc $Target 8023"
Write-Host "         test:  echo '{`"cmd`":`"hello`"}' | nc $Target 8023"
