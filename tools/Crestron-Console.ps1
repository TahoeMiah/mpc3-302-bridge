# Crestron-Console.ps1
#
# Drives the Crestron 3-series text console over TELNET (port 23) when SSH
# (port 22) is unavailable - e.g. right after a firmware update that reset the
# device's network config and disabled the SSH daemon.
#
# It handles the Login:/Password: prompts, then runs each command in -Commands
# in order, printing all console output. Used to re-enable SSH so the normal
# pscp/plink deploy path works again.
#
# Usage:
#   .\tools\Crestron-Console.ps1 -Target 192.168.16.240 -User admin `
#       -Password $env:MPC_PASS -Commands 'ver','hostname','ssh'

[CmdletBinding()]
param(
    [string]$Target   = '192.168.16.240',
    [int]$Port        = 23,
    [string]$User     = 'admin',
    [string]$Password = $env:MPC_PASS,
    [string[]]$Commands = @('ver'),
    [int]$IdleMs      = 1200
)

$ErrorActionPreference = 'Stop'

$client = New-Object System.Net.Sockets.TcpClient
$client.Connect($Target, $Port)
$stream = $client.GetStream()
$stream.ReadTimeout = 800
$enc = [Text.Encoding]::ASCII

function Read-Until-Idle([int]$idleMs) {
    $sb = New-Object System.Text.StringBuilder
    $buf = New-Object byte[] 8192
    $lastData = [Diagnostics.Stopwatch]::StartNew()
    while ($lastData.ElapsedMilliseconds -lt $idleMs) {
        try {
            if ($stream.DataAvailable) {
                $n = $stream.Read($buf, 0, $buf.Length)
                if ($n -gt 0) {
                    [void]$sb.Append($enc.GetString($buf, 0, $n))
                    $lastData.Restart()
                }
            } else {
                Start-Sleep -Milliseconds 50
            }
        } catch { Start-Sleep -Milliseconds 50 }
    }
    return $sb.ToString()
}

function Send-Line([string]$text) {
    $bytes = $enc.GetBytes($text + "`r`n")
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
}

# 1. Initial banner / first prompt.
$out = Read-Until-Idle 1500
Write-Host "=== banner ===" -ForegroundColor Cyan
Write-Host ($out -replace '[^\x09\x0a\x0d\x20-\x7e]', '.')

# 2. Authenticate if prompted.
if ($out -match '(?i)login') {
    Send-Line $User
    $out = Read-Until-Idle 1200
    Write-Host ($out -replace '[^\x09\x0a\x0d\x20-\x7e]', '.')
    if ($out -match '(?i)password') {
        Send-Line $Password
        $out = Read-Until-Idle 1500
        Write-Host ($out -replace '[^\x09\x0a\x0d\x20-\x7e]', '.')
    }
}

# 3. Run each command.
foreach ($cmd in $Commands) {
    Write-Host ("=== > {0} ===" -f $cmd) -ForegroundColor Yellow
    Send-Line $cmd
    $out = Read-Until-Idle $IdleMs
    Write-Host ($out -replace '[^\x09\x0a\x0d\x20-\x7e]', '.')
}

$client.Close()
