# mpc3-tcp-bridge

SIMPL# Pro program that loads onto a Crestron **MPC3-302** (or MPC3-301) and
exposes its buttons, LEDs, mute, and volume over a plain **TCP/IP** socket.
No MQTT, no HTTP, no Home Assistant glue — just one line of JSON per message,
in and out.

Built as a sibling to [`mpc3-ha-bridge`](https://github.com/anouk/mpc3-ha-bridge)
so the two can be A/B'd on the same hardware. They never run at the same
time (one program slot at a time).

> **Heads-up — known firmware bug:** on MPC3-302 firmware `1.8001.0251` the
> panel's `ButtonStateChange` / `PanelStateChange` events don't fire in user
> programs. LED writes and bargraph updates work fine; physical button presses
> and rotary turns are silently dropped *at the SDK layer*, before this
> program ever sees them. That means the **outbound** half of this bridge
> (server → client events on press) is currently dead on that firmware, and
> changing transport from MQTT to TCP doesn't fix it. The **inbound** half
> (client → server LED / volume / mute commands) works. See the
> `mpc3-slot-registration-bug` memory in `mpc3-ha-bridge` for details and
> the suspected firmware-update fix.

## Wire protocol

One JSON object per line, both directions. Lines are terminated by `\n`
(`\r\n` accepted on input). UTF-8.

### Commands (client → server)

```
{"cmd":"hello"}                                  -> reply: hello event
{"cmd":"state"}                                  -> reply: state event
{"cmd":"ping"}                                   -> reply: pong event
{"cmd":"led","name":"btn01","on":true}           -> sets LED, broadcasts led event
{"cmd":"vol","level":75}                         -> sets bargraph + volume state
{"cmd":"mute","on":true}                         -> sets soft mute state + mute LED
{"cmd":"emit","name":"btn03","pressed":true}     -> diagnostic: fake a button event
```

`name` is one of: `power`, `mute`, `btn01` .. `btn10`.
`level` is `0..100`.

### Events (server → client, broadcast on every connected socket)

```
{"event":"hello","version":"0.1.0","port":8023,"buttons":["power","mute","btn01",...]}
{"event":"state","leds":{"power":false,...},"volume":50,"muted":false}
{"event":"pong","at_utc":"2026-05-26T..."}
{"event":"button","name":"btn03","pressed":true,"at_utc":"..."}
{"event":"volume","level":42}
{"event":"mute","on":true}
{"event":"led","name":"btn01","on":true}
{"event":"error","message":"unknown cmd 'foo'"}
```

`hello` is sent unsolicited to each client immediately on connect, so a
client can identify the device without issuing any command.

## Try it from a shell

```powershell
# Windows: ncat (from nmap) or PuTTY in raw mode
ncat 192.168.16.240 8023
{"cmd":"led","name":"btn01","on":true}
{"cmd":"vol","level":80}
{"cmd":"state"}
```

```bash
# Linux/macOS: plain nc
nc 192.168.16.240 8023
{"cmd":"hello"}
```

Each line you type is one JSON command; each line you receive is one event.

## Configuration

The TCP port and bind address come from `/user/appsettings.json` on the
processor. If the file is missing the defaults apply (`0.0.0.0:8023`, all
adapters). Start from `crestron/Mpc3TcpBridge/appsettings.sample.json`:

```json
{
  "Tcp": {
    "Port": 8023,
    "BindAddress": "0.0.0.0",
    "MaxClients": 8,
    "BufferBytes": 4096
  }
}
```

Drop edits in via SCP and `progres -P:01` to reload.

## Build + deploy

```powershell
.\tools\Build-Cpz.ps1                                # VS 2008 DTE -> .cpz
$env:MPC_PASS = 'your-admin-password'
.\tools\Deploy-Cpz.ps1 -Target 192.168.16.240        # SCP + progload
# or in one shot:
.\tools\Build-And-Deploy.ps1 -Target 192.168.16.240
```

Same VS 2008 + SIMPL# Pro plugin requirement as `mpc3-ha-bridge` — the
post-build packaging step has to run inside the IDE host. The PowerShell
scripts drive that via COM automation.

## Console diagnostics

SSH into the processor and run `mpctcp help`:

```
mpctcp state                  dump current state
mpctcp led btn03 on           drive a single LED
mpctcp vol 75                 set volume bargraph
mpctcp mute on                set mute state
mpctcp emit btn03 press       inject a synthetic button event (broadcasts to clients)
mpctcp clients                list connected TCP clients
```

The `emit` command is the way to verify the server -> client event path
while the panel-input firmware bug is in effect.

## Layout

```
crestron/
|-- Mpc3TcpBridge.sln              VS 2008 solution
`-- Mpc3TcpBridge/
    |-- ControlSystem.cs           entry point + console commands
    |-- Config/AppSettings.cs      /user/appsettings.json loader
    |-- Hardware/Mpc3Wrapper.cs    buttons, LEDs, volume <-> DeviceState
    |-- State/DeviceState.cs       in-memory model, event source
    |-- State/ButtonNames.cs       canonical button identifiers
    |-- Tcp/TcpServer.cs           JSON-per-line TCP server (Crestron TCPServer)
    |-- ProgramInfo.config
    |-- appsettings.sample.json
    `-- Properties/

tools/
|-- Build-Cpz.ps1                  drive VS 2008 DTE to produce .cpz
|-- Deploy-Cpz.ps1                 pscp upload + plink progload
`-- Build-And-Deploy.ps1           chain the two for inner-loop dev
```
