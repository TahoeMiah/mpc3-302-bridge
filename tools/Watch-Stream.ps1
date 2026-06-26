# Watch-Stream.ps1
#
# Connects to the bridge's JSON-over-TCP stream (port 8023) and prints every
# event line with an elapsed-time stamp for -Seconds. Use it to watch the
# volume-dial diagnostics live while turning the physical knob.
#
#   .\tools\Watch-Stream.ps1 -Target 192.168.16.240 -Seconds 45

[CmdletBinding()]
param(
    [string]$Target = '192.168.16.240',
    [int]$Port      = 8023,
    [int]$Seconds   = 45
)

$ErrorActionPreference = 'Stop'
$client = New-Object System.Net.Sockets.TcpClient
$client.Connect($Target, $Port)
$stream = $client.GetStream()
$stream.ReadTimeout = 500
$enc = [Text.Encoding]::ASCII
$buf = New-Object byte[] 8192
$line = New-Object System.Text.StringBuilder
$sw = [Diagnostics.Stopwatch]::StartNew()

Write-Host ("[watch] connected to {0}:{1} - turn the dial now ({2}s window)" -f $Target,$Port,$Seconds) -ForegroundColor Cyan

while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    try {
        if ($stream.DataAvailable) {
            $n = $stream.Read($buf, 0, $buf.Length)
            for ($i = 0; $i -lt $n; $i++) {
                $ch = [char]$buf[$i]
                if ($ch -eq "`n") {
                    $text = $line.ToString().Trim()
                    if ($text.Length -gt 0) {
                        Write-Host ("[{0,6:0.0}s] {1}" -f $sw.Elapsed.TotalSeconds, $text)
                    }
                    [void]$line.Clear()
                } elseif ($ch -ne "`r") {
                    [void]$line.Append($ch)
                }
            }
        } else {
            Start-Sleep -Milliseconds 50
        }
    } catch { Start-Sleep -Milliseconds 50 }
}
$client.Close()
Write-Host "[watch] done." -ForegroundColor Cyan
