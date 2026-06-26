# mpc3-tcp-bridge

Your Crestron **MPC3-302** (or MPC3-301) is a perfectly nice wall keypad that
normally only talks to other Crestron gear. This little SIMPL# Pro program kicks
the doors open and lets *anything* on your network play with its buttons, LEDs,
mute, and volume knob — three ways:

- 🔌 **JSON-over-TCP** on port `8023` — one tidy JSON object per line.
- 🌐 **A web panel** on port `8080` — a live mirror of the physical keypad,
  updating in real time, with a settings page baked in.
- 📡 **MQTT** — publishes everything the panel does and takes commands back,
  with one-click Home Assistant auto-discovery.

All three share one brain (an in-memory `DeviceState`), so poke it from any
direction and everyone else sees it instantly — including the physical panel.

Got a sibling project, [`mpc3-ha-bridge`](https://github.com/anouk/mpc3-ha-bridge),
that does a similar job a different way. They can't run at the same time (one
program slot, one winner), so this repo exists to A/B them on the same hardware.

> Heads-up: this trusts your LAN completely. No auth, no TLS, no cookies, no
> judgement. Keep it on a network you control.

## Install (no Crestron tools required)

Grab **`Mpc3TcpBridge.cpz`** from the [**latest release**](../../releases/latest),
SFTP it to `/Program01/` on your processor, SSH in and run `progload -P:01`. Two
steps, no Crestron Toolbox, no SIMPL. Full walkthrough (WinSCP, FileZilla, or
command line) in [**`docs/INSTALL.md`**](docs/INSTALL.md). Then browse to
`http://<your-mpc3>:8080/`.

Want to build it yourself instead? Jump to [Build + deploy](#build--deploy).

## Wire protocol (TCP, port 8023)

One JSON object per line, both directions. Lines end in `\n` (`\r\n` is fine on
the way in too). UTF-8, naturally.

### You say (client → server)

```
{"cmd":"hello"}                                  -> reply: hello event
{"cmd":"state"}                                  -> reply: state event
{"cmd":"ping"}                                   -> reply: pong event
{"cmd":"led","name":"btn01","on":true}           -> sets LED, broadcasts led event
{"cmd":"vol","level":75}                         -> sets bargraph + volume state
{"cmd":"mute","on":true}                         -> sets soft mute state + mute LED
{"cmd":"emit","name":"btn03","pressed":true}     -> diagnostic: fake a button press
```

`name` is one of: `power`, `mute`, `btn01` .. `btn10`. `level` is `0..100`.

### It says back (server → client, shouted to every connected socket)

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

`hello` shows up unsolicited the moment you connect, so a client can figure out
what it's talking to without saying a word first. Polite, really.

### Kick the tires from a shell

```powershell
# Windows: ncat (from nmap) or PuTTY in raw mode
ncat 192.168.16.240 8023
{"cmd":"led","name":"btn01","on":true}
{"cmd":"vol","level":80}
{"cmd":"state"}
```

```bash
# Linux/macOS: good old nc
nc 192.168.16.240 8023
{"cmd":"hello"}
```

Type a line, it's a command. Receive a line, it's an event. That's the whole deal.

## Web UI (HTTP, port 8080)

Point a browser at `http://<mpc3>:8080/` and you get a slick single-page replica
of the real keypad: ten programmable buttons in a 2×5 grid, power, a volume dial,
and mute. It's entirely self-contained — no CDNs, no build step, no tracking, just
HTML/CSS/JS stuffed into the program. Press a physical button and the matching
tile lights up blue while you hold it; spin the real knob and the on-screen dial
chases it. Everyone watching sees the same thing, because it all rides the same
shared state.

The routes, if you want to script against it:

| Method | Path           | What it does                                                            |
| ------ | -------------- | ----------------------------------------------------------------------- |
| `GET`  | `/`            | The panel page (everything inlined)                                     |
| `GET`  | `/api/state`   | JSON snapshot of LEDs, volume, mute (+ `mqtt_connected`)                |
| `GET`  | `/api/events`  | Server-Sent Events — every state change, pushed live                    |
| `POST` | `/api/cmd`     | Same JSON commands as the TCP wire (`led`/`vol`/`mute`/`emit`)          |
| `GET`  | `/config`      | Settings page — name the device, point it at MQTT, Save, Restart        |
| `GET`  | `/api/settings`| Current settings as JSON (MQTT password redacted, obviously)            |
| `POST` | `/api/settings`| Save settings to `/User/appsettings.json` (blank password = keep old)   |
| `POST` | `/api/restart` | `progreset` the slot to apply MQTT changes                              |

The ⚙️ gear in the top corner takes you to `/config`.

## Configuration

Settings live in `/User/appsettings.json` on the processor. No file? No problem —
sensible defaults kick in. Crib from
[`appsettings.sample.json`](crestron/Mpc3TcpBridge/appsettings.sample.json):

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

Set `Web.Port` to `0` to switch the web UI off entirely. Hand-edits apply after
`progres -P:01` — **but honestly, don't bother editing this by hand.** The
`/config` page writes it for you and offers a Restart button.

## MQTT

Want the keypad in Home Assistant (or any MQTT thing)? Flip MQTT on and the bridge
will happily narrate everything the panel does and obey commands you send back.

It ships **off**. Turn it on the easy way: open the ⚙️ settings page (or
`http://<mpc>:8080/config`), type in your broker, hit Save, hit Restart. No SSH,
no JSON wrangling. The page even shows a little green/grey dot for whether it's
actually connected.

Under the hood it's a tiny, dependency-free MQTT 3.1.1 client built to survive
.NET Compact Framework 3.5 (same job as
[fasteddy516/SimplMQTT](https://github.com/fasteddy516/SimplMQTT), minus the
SIMPL+ baggage): CONNECT with optional login + last-will, QoS-0 publish/subscribe,
keepalive, and auto-reconnect when the broker blinks. No TLS — if you need
encryption, park an MQTT-over-TLS proxy in front of it.

Everything lives under `<BaseTopic>/<DeviceId>/`:

```
status                     online | offline   (retained, last-will)
led/<name>/state           ON | OFF           (retained)   name = power|mute|btn01..btn10
led/<name>/set             -> you publish here to light an LED
volume/state               0..100             (retained)
volume/set                 -> you publish here to set volume
mute/state                 ON | OFF           (retained)
mute/set                   -> you publish here to toggle mute
button/<name>/event        pressed | released (fired on every physical press)
```

Leave `HaDiscovery: true` and the panel just *appears* in Home Assistant — a
switch for every LED + mute, a slider for volume, and press/release triggers for
all 12 keys, no YAML required. Want plain vanilla MQTT with none of the HA
sprinkles? Set it to `false`.

No broker handy but want to prove it works? `tools/Test-MqttBroker.ps1` is a
throwaway one-client broker that accepts the connection and prints every message
the panel publishes.

## Build + deploy

```powershell
.\tools\Build-Cpz.ps1                                # VS 2008 DTE -> .cpz
$env:MPC_PASS = 'your-admin-password'
.\tools\Deploy-Cpz.ps1 -Target 192.168.16.240        # SCP + progload
# ...or do both in one go:
.\tools\Build-And-Deploy.ps1 -Target 192.168.16.240
```

Fair warning: SIMPL# Pro is fussy about packaging. The step that turns your code
into a loadable `.cpz` only fires inside the **VS 2008** IDE — plain `msbuild`
gives you a `.dll` and a shrug. The PowerShell scripts drive VS 2008 over COM
automation so you don't have to click around. (Welcome to 2008. Mind the gap.)

Tired of typing the password? Drop a gitignored `.secrets/secrets.env`:

```
MPC_HOST=192.168.16.240
MPC_USER=admin
MPC_PASS=your-admin-password
```

## Poking it over SSH

SSH in and run `mpctcp help`:

```
mpctcp state                  dump current state
mpctcp led btn03 on           light a single LED
mpctcp vol 75                 set the volume bargraph
mpctcp mute on                set mute
mpctcp emit btn03 press       fake a button press (broadcasts to all clients)
mpctcp clients                how many TCP + web clients are watching
mpctcp diag                   live panel signals (volume raw/%, CW/CCW, button state)
```

Press a real button and you'll see `[mpc3] button btnXX PRESSED|released` on the
console and in `err`. `mpctcp diag` is your best friend when the knob or buttons
are misbehaving — it shows exactly what the hardware is reporting. And `mpctcp
emit` lets you fake presses with no panel attached, handy for testing clients in
your pajamas.

## What's where

```
crestron/
|-- Mpc3TcpBridge.sln              VS 2008 solution
`-- Mpc3TcpBridge/
    |-- ControlSystem.cs           the conductor: wires settings -> state -> servers
    |-- Config/AppSettings.cs      reads + writes /User/appsettings.json
    |-- Hardware/Mpc3Wrapper.cs    the panel whisperer: buttons / LEDs / knob
    |-- State/DeviceState.cs       the single source of truth + event bus
    |-- State/ButtonNames.cs       canonical names (power, mute, btn01..btn10)
    |-- Tcp/TcpServer.cs           JSON-per-line TCP server
    |-- Mqtt/MqttClient.cs         tiny dependency-free MQTT 3.1.1 client (CF 3.5)
    |-- Mqtt/MqttBridge.cs         DeviceState <-> MQTT + HA discovery
    |-- Web/WebServer.cs           HTTP/1.1 + SSE server + settings API (:8080)
    |-- Web/Static.cs              the inlined panel + /config pages
    |-- ProgramInfo.config
    |-- appsettings.sample.json
    `-- Properties/

tools/
|-- Build-Cpz.ps1                  herd VS 2008 into producing a .cpz
|-- Deploy-Cpz.ps1                 pscp upload + plink progload
|-- Build-And-Deploy.ps1          do both, for fast inner-loop iterating
|-- Watch-Stream.ps1              tail the live JSON-over-TCP event stream
|-- Crestron-Console.ps1         drive the Crestron console over telnet (when SSH is AWOL)
`-- Test-MqttBroker.ps1          a throwaway MQTT broker to test against

docs/
`-- DESIGN-NOTES.md               the nerdy deep-dive (genuinely worth a read)
```

## Want the full story?

[`docs/DESIGN-NOTES.md`](docs/DESIGN-NOTES.md) is where the real adventure lives:
how the whole thing fits together, and the saga of getting an MPC3 front panel to
talk at all — the `Register()` + `Enable*Button` incantations the newer firmware
demands, the great absolute-vs-relative volume mystery, and the time a resident
Crestron app was quietly eating every button press. If you're hacking on this,
start there.
