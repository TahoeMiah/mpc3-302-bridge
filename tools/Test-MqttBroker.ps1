# Test-MqttBroker.ps1
#
# A throwaway, single-client MQTT 3.1.1 broker stub for validating the bridge's
# MqttClient end to end without installing mosquitto. It does the bare minimum:
#   - accepts one TCP client on -Port (default 1883)
#   - replies CONNACK to CONNECT, SUBACK to SUBSCRIBE, PINGRESP to PINGREQ
#   - prints every PUBLISH (topic + payload) it receives
#
# It does NOT route messages or persist retains - it's a sink to prove the MPC3
# connects, subscribes, and publishes its state. Runs for -Seconds then exits.
#
#   .\tools\Test-MqttBroker.ps1 -Seconds 60

[CmdletBinding()]
param([int]$Port = 1883, [int]$Seconds = 60)

$ErrorActionPreference = 'Stop'

$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Any, $Port)
$listener.Start()
Write-Host ("[broker] listening on 0.0.0.0:{0} for {1}s" -f $Port, $Seconds) -ForegroundColor Cyan

$deadline = [DateTime]::UtcNow.AddSeconds($Seconds)

function Read-N([System.Net.Sockets.NetworkStream]$s, [int]$n) {
    $buf = New-Object byte[] $n
    $off = 0
    while ($off -lt $n) {
        $r = $s.Read($buf, $off, $n - $off)
        if ($r -le 0) { return $null }
        $off += $r
    }
    return $buf
}

# Wait for a client (poll so we can honor the overall deadline).
while (-not $listener.Pending()) {
    if ([DateTime]::UtcNow -gt $deadline) { Write-Host '[broker] no client connected'; $listener.Stop(); return }
    Start-Sleep -Milliseconds 100
}
$client = $listener.AcceptTcpClient()
$ep = $client.Client.RemoteEndPoint
Write-Host ("[broker] client connected from {0}" -f $ep) -ForegroundColor Green
$ns = $client.GetStream()
$ns.ReadTimeout = 1000

$pubCount = 0
try {
    while ([DateTime]::UtcNow -lt $deadline -and $client.Connected) {
        $fixed = $null
        try { $fixed = Read-N $ns 1 } catch { continue }   # read control byte (timeout -> retry)
        if ($null -eq $fixed) { break }
        $ctl = $fixed[0]

        # remaining length (variable byte)
        $rem = 0; $mult = 1
        do {
            $b = Read-N $ns 1
            if ($null -eq $b) { throw 'eof in remlen' }
            $rem += ($b[0] -band 0x7F) * $mult
            $mult *= 128
        } while (($b[0] -band 0x80) -ne 0)

        $body = if ($rem -gt 0) { Read-N $ns $rem } else { @() }
        $type = ($ctl -shr 4) -band 0x0F

        switch ($type) {
            1 { # CONNECT -> CONNACK (session accepted)
                Write-Host '[broker] <- CONNECT     -> CONNACK' -ForegroundColor Yellow
                $ns.Write([byte[]](0x20,0x02,0x00,0x00), 0, 4); $ns.Flush()
            }
            8 { # SUBSCRIBE -> SUBACK (granted QoS 0)
                $pktId = ($body[0] -shl 8) -bor $body[1]
                $ack = [byte[]](0x90,0x03, $body[0], $body[1], 0x00)
                $ns.Write($ack, 0, $ack.Length); $ns.Flush()
                Write-Host ("[broker] <- SUBSCRIBE   -> SUBACK (pid={0})" -f $pktId) -ForegroundColor Yellow
            }
            12 { # PINGREQ -> PINGRESP
                $ns.Write([byte[]](0xD0,0x00), 0, 2); $ns.Flush()
                Write-Host '[broker] <- PINGREQ     -> PINGRESP' -ForegroundColor DarkGray
            }
            3 { # PUBLISH
                $tl = ($body[0] -shl 8) -bor $body[1]
                $topic = [Text.Encoding]::UTF8.GetString($body, 2, $tl)
                $qos = ($ctl -shr 1) -band 0x03
                $pi = 2 + $tl
                if ($qos -gt 0) { $pi += 2 }
                $plen = $body.Length - $pi
                $payload = if ($plen -gt 0) { [Text.Encoding]::UTF8.GetString($body, $pi, $plen) } else { '' }
                $retain = if (($ctl -band 0x01) -ne 0) { ' [retain]' } else { '' }
                $pubCount++
                if ($payload.Length -gt 80) { $payload = $payload.Substring(0,80) + '...' }
                Write-Host ("[broker] PUBLISH{0} {1} = {2}" -f $retain, $topic, $payload) -ForegroundColor White
            }
            14 { Write-Host '[broker] <- DISCONNECT'; break }
            default { Write-Host ("[broker] <- type {0} ({1} bytes)" -f $type, $rem) -ForegroundColor DarkGray }
        }
    }
} catch {
    Write-Host ("[broker] loop end: {0}" -f $_.Exception.Message) -ForegroundColor DarkGray
} finally {
    try { $client.Close() } catch {}
    $listener.Stop()
}
Write-Host ("[broker] done. PUBLISH packets received: {0}" -f $pubCount) -ForegroundColor Cyan
