# mpc3-tcp-bridge

SIMPL# Pro program that loads onto a Crestron **MPC3-302** (or MPC3-301) and
exposes its buttons, LEDs, mute, and volume two ways:

- A plain **JSON-over-TCP** socket on port `8023` (one JSON object per
  line; no MQTT, no HTTP).
- A **web UI on port `8080`** at `http://<mpc3>:8080/` that mirrors every
  state change in real time over Server-Sent Events.

Built as a sibling to [`mpc3-ha-bridge`](https://github.com/anouk/mpc3-ha-bridge)
so the two can be A/B'd on the same hardware. They never run at the same
time (one program slot at a time).

> **What was previously documented here as a firmware bug** — "panel
> `ButtonStateChange` / `PanelStateChange` events don't fire in user
> programs on MPC3-302 firmware 1.8001.0251" — turned out **not to be a
> firmware bug**. It was a missing `_panel.Register()` call. The built-in
> `MPC3x30xTouchscreenSlot` device starts un-registered, and the SDK only
> delivers callbacks for explicitly-registered devices. With the
> [single-line Register()](crestron/Mpc3TcpBridge/Hardware/Mpc3Wrapper.cs)
> call in place, physical button presses, releases, and rotary turns all
> fire as expected on firmware 1.8001.0298 (likely on 1.8001.0251 too).
> The sibling `mpc3-ha-bridge` repo almost certainly has the same fix
> waiting.

## Wire protocol (TCP, port 8023)

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

### Try it from a shell

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

## Web UI (HTTP, port 8080)

Browse to `http://<mpc3>:8080/` and you get a single-page panel that
mirrors the physical MPC3-302: ten programmable buttons in a 2×5 grid,
power, volume dial, and mute. The page is self-contained — no external
assets, no auth, no cookies. It assumes the LAN is trusted (same posture
as the TCP server).

Routes:

| Method | Path           | Purpose                                                                 |
| ------ | -------------- | ----------------------------------------------------------------------- |
| `GET`  | `/`            | The HTML page (CSS/JS inlined)                                          |
| `GET`  | `/api/state`   | JSON snapshot of LEDs, volume, mute                                     |
| `GET`  | `/api/events`  | Server-Sent Events stream — pushes every state change as it happens     |
| `POST` | `/api/cmd`     | Same JSON command vocabulary as the TCP wire (`led`/`vol`/`mute`/`emit`) |

Press a physical button on the panel and the matching tile in the web UI
lights up blue (`.pressing` highlight) for the duration of the press;
volume rotary turns update the dial in real time. The web UI and the TCP
bridge share one in-memory `DeviceState`, so any client can drive state
that every other client sees.

## Configuration

Settings come from `/User/appsettings.json` on the processor. If the file
is missing, defaults apply. Start from
[`crestron/Mpc3TcpBridge/appsettings.sample.json`](crestron/Mpc3TcpBridge/appsettings.sample.json):

```json
{
  "Tcp": {
    "Port": 8023,
    "BindAddress": "0.0.0.0",
    "MaxClients": 8,
    "BufferBytes": 4096
  },
  "Web": {
    "Port": 8080,
    "BindAddress": "0.0.0.0"
  },
  "Volume": {
    "DefaultLevel": 50
  }
}
```

Set `Web.Port` to `0` to disable the web UI entirely. Edits take effect
after `progres -P:01`.

## Build + deploy

```powershell
.\tools\Build-Cpz.ps1                                # VS 2008 DTE -> .cpz
$env:MPC_PASS = 'your-admin-password'
.\tools\Deploy-Cpz.ps1 -Target 192.168.16.240        # SCP + progload
# or in one shot:
.\tools\Build-And-Deploy.ps1 -Target 192.168.16.240
```

The post-build SIMPL# Pro packaging step must run inside the VS 2008 IDE
host — `msbuild` alone will produce a `.dll` but not a deployable `.cpz`.
The PowerShell scripts drive that via VS 2008's DTE COM automation.

For a per-checkout `MPC_PASS` you can paste once and forget, drop a file
at `.secrets/secrets.env` (gitignored):

```
MPC_HOST=192.168.16.240
MPC_USER=admin
MPC_PASS=your-admin-password
```

## Console diagnostics

SSH into the processor and run `mpctcp help`:

```
mpctcp state                  dump current state
mpctcp led btn03 on           drive a single LED
mpctcp vol 75                 set volume bargraph
mpctcp mute on                set mute state
mpctcp emit btn03 press       inject a synthetic button event (broadcasts to clients)
mpctcp clients                show tcp + web client counts
```

When a physical button is pressed, you'll also see a `[mpc3] button
btnXX PRESSED|released` line on the SSH console, and the same line in
`err` for post-hoc review.

`mpctcp emit` is still useful: it lets you inject button events from the
console even when no panel is connected — handy for end-to-end testing
of TCP / web clients without leaving the keyboard.

## Layout

```
crestron/
|-- Mpc3TcpBridge.sln              VS 2008 solution
`-- Mpc3TcpBridge/
    |-- ControlSystem.cs           entry point + console commands
    |-- Config/AppSettings.cs      /User/appsettings.json loader
    |-- Hardware/Mpc3Wrapper.cs    panel buttons / LEDs / volume <-> DeviceState
    |-- State/DeviceState.cs       in-memory model, event source
    |-- State/ButtonNames.cs       canonical button identifiers
    |-- Tcp/TcpServer.cs           JSON-per-line TCP server (Crestron TCPServer)
    |-- Web/WebServer.cs           HTTP/1.1 + SSE server (port 8080)
    |-- Web/Static.cs              inline HTML/CSS/JS for the panel page
    |-- ProgramInfo.config
    |-- appsettings.sample.json
    `-- Properties/

tools/
|-- Build-Cpz.ps1                  drive VS 2008 DTE to produce .cpz
|-- Deploy-Cpz.ps1                 pscp upload + plink progload
`-- Build-And-Deploy.ps1           chain the two for inner-loop dev
```
