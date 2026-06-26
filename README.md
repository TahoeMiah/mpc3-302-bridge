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
| `GET`  | `/config`      | Settings page — edit device name + MQTT broker, Save, Restart           |
| `GET`  | `/api/settings`| Current settings as JSON (MQTT password redacted)                       |
| `POST` | `/api/settings`| Persist settings to `/User/appsettings.json` (blank password = keep)    |
| `POST` | `/api/restart` | `progreset` this program slot (apply MQTT changes)                      |

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
  "DeviceId": "mpc3-302",
  "FriendlyName": "MPC3 Controller",
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
  "Mqtt": {
    "Enabled": false,
    "Host": "",
    "Port": 1883,
    "Username": "",
    "Password": "",
    "BaseTopic": "mpc3",
    "HaDiscovery": true,
    "DiscoveryPrefix": "homeassistant",
    "KeepAliveSeconds": 30
  },
  "Volume": {
    "DefaultLevel": 50
  }
}
```

Set `Web.Port` to `0` to disable the web UI entirely. Edits take effect
after `progres -P:01`. **You don't have to edit this file by hand** — the
web UI has a settings page (see below) that writes it for you.

## MQTT

The bridge can mirror the panel onto an MQTT broker (publish state, accept
commands) and optionally advertise itself to Home Assistant via MQTT
discovery. It's **off by default** — point it at your broker from the
**settings page** (gear icon on the web UI, or `http://<mpc>:8080/config`),
click Save, then Restart program. No SSH or file editing required.

The MQTT client is the dependency-free CF-3.5 MQTT 3.1.1 client shared with
the sibling `mpc3-ha-bridge` (same role as
[fasteddy516/SimplMQTT](https://github.com/fasteddy516/SimplMQTT)): CONNECT
with optional credentials + last-will, QoS-0 PUBLISH/SUBSCRIBE, keepalive,
and auto-reconnect. No TLS — front it with an MQTT-over-TLS proxy if needed.

Topics live under `<BaseTopic>/<DeviceId>/`:

```
status                     online | offline   (retained, last-will)
led/<name>/state           ON | OFF           (retained)   name = power|mute|btn01..btn10
led/<name>/set             -> subscribe; command an LED on/off
volume/state               0..100             (retained)
volume/set                 -> subscribe; set volume
mute/state                 ON | OFF           (retained)
mute/set                   -> subscribe; toggle mute
button/<name>/event        pressed | released (not retained)
```

With `HaDiscovery: true` it also publishes Home Assistant discovery configs
under `DiscoveryPrefix` (default `homeassistant`): a switch per LED + mute, a
number for volume, and device-automation triggers for every button edge — so
the panel appears in HA with no YAML. Set `HaDiscovery: false` for a plain
generic-MQTT bridge.

To validate without installing a broker, `tools/Test-MqttBroker.ps1` is a
throwaway single-client broker stub that accepts the connection and prints
every PUBLISH it receives.

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
mpctcp diag                   dump live panel signal values (volume raw/%, CW/CCW, button state)
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
    |-- Config/AppSettings.cs      /User/appsettings.json loader + saver
    |-- Hardware/Mpc3Wrapper.cs    panel buttons / LEDs / volume <-> DeviceState
    |-- State/DeviceState.cs       in-memory model, event source
    |-- State/ButtonNames.cs       canonical button identifiers
    |-- Tcp/TcpServer.cs           JSON-per-line TCP server (Crestron TCPServer)
    |-- Mqtt/MqttClient.cs         dependency-free MQTT 3.1.1 client (CF 3.5)
    |-- Mqtt/MqttBridge.cs         DeviceState <-> MQTT + HA discovery
    |-- Web/WebServer.cs           HTTP/1.1 + SSE server, settings API (port 8080)
    |-- Web/Static.cs              inline HTML/CSS/JS for the panel + /config pages
    |-- ProgramInfo.config
    |-- appsettings.sample.json
    `-- Properties/

tools/
|-- Build-Cpz.ps1                  drive VS 2008 DTE to produce .cpz
|-- Deploy-Cpz.ps1                 pscp upload + plink progload
|-- Build-And-Deploy.ps1          chain the two for inner-loop dev
|-- Watch-Stream.ps1              tail the JSON-over-TCP event stream
|-- Crestron-Console.ps1         drive the Crestron console over telnet (no SSH)
`-- Test-MqttBroker.ps1          throwaway MQTT broker stub for end-to-end testing

docs/
`-- DESIGN-NOTES.md               architecture + MPC3 firmware findings (read this)
```

## Further reading

[`docs/DESIGN-NOTES.md`](docs/DESIGN-NOTES.md) is the engineering write-up:
architecture and data flow, the hard-won MPC3 panel-input findings (the
`Register()` + `Enable*Button` requirements on firmware 1.8001.6192, absolute
vs. relative volume reporting, and how a resident Crestron Home / AV-Framework
app can silently own the front panel), the MQTT design, and the build/deploy
toolchain.
