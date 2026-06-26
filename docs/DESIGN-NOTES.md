# mpc3-tcp-bridge — design notes & engineering write-up

A deep-dive companion to the [README](../README.md). The README is the
*how to use it* document; this is the *what it is, how it's built, and what we
learned the hard way* document.

---

## 1. What this is

A single SIMPL# Pro program that turns a **Crestron MPC3-302** (or MPC3-301)
media-presentation controller into an open, scriptable network device. Out of
the box the MPC3 is a closed keypad you program in Crestron's toolchain; this
firmware exposes its buttons, LEDs, mute, and volume knob over three plain,
documented interfaces so anything on the LAN can drive it:

1. **JSON-over-TCP** on `:8023` — one JSON object per line, both directions.
2. **A self-contained web UI** on `:8080` — a live panel mirror over
   Server-Sent Events, plus a settings page.
3. **MQTT** — publishes panel state and accepts commands, with optional Home
   Assistant auto-discovery.

All three are driven from one shared in-memory model (`DeviceState`), so a
change from any interface is reflected on all the others and on the physical
panel simultaneously.

---

## 2. Architecture

```
                         ┌──────────────────────────┐
   physical panel  <───> │      Mpc3Wrapper         │  buttons / LEDs / knob
   (MPC3x30x slot)       │  (Hardware/)             │
                         └────────────┬─────────────┘
                                      │  events / commands
                         ┌────────────▼─────────────┐
                         │       DeviceState         │  in-memory truth:
                         │       (State/)            │  LEDs, volume, mute
                         │  events: Led/Volume/Mute/ │  + button event bus
                         │          Button           │
                         └──┬──────────┬──────────┬──┘
                            │          │          │
              ┌─────────────▼──┐ ┌─────▼──────┐ ┌─▼───────────────┐
              │   TcpServer    │ │ WebServer  │ │   MqttBridge    │
              │   (Tcp/) :8023 │ │ (Web/):8080│ │   (Mqtt/)       │
              │  JSON per line │ │ panel + SSE│ │  pub state /    │
              │                │ │ + /config  │ │  sub commands   │
              └────────────────┘ └────────────┘ └───────┬─────────┘
                                                         │
                                                  ┌──────▼──────┐
                                                  │ MqttClient  │  MQTT 3.1.1
                                                  │ (Mqtt/)     │  over TCP
                                                  └─────────────┘
```

**Key design choices**

- **One source of truth.** Every interface reads and mutates the same
  `DeviceState`. State changes fire C# events; each server subscribes and
  fans the change out to its clients. No interface owns the state.
- **Events fire outside the lock.** `DeviceState` guards its fields with a
  single lock but raises change events *after* releasing it, so a subscriber
  can call back into `DeviceState` without deadlocking.
- **Everything is one `.cpz`.** HTML/CSS/JS for both web pages is inlined as
  C# string constants (`Web/Static.cs`). No external assets, no second file to
  stage — the whole deliverable is one program file.
- **Hand-rolled servers on `TCPServer`.** The HTTP/1.1 + SSE server and the
  JSON-line server are both built directly on Crestron's `TCPServer`. The CWS
  (`/cws/...`) path is deliberately avoided here — the sibling
  `mpc3-ha-bridge` uses CWS; this project A/B's the raw-socket approach.

### Component map

| Path | Role |
| --- | --- |
| `ControlSystem.cs` | Entry point. Wires settings → state → hardware → servers. Console commands. |
| `Config/AppSettings.cs` | Loads/saves `\User\appsettings.json`; defaults if absent. |
| `Hardware/Mpc3Wrapper.cs` | The panel facade: buttons, LEDs, the volume knob, max-diagnostics. |
| `State/DeviceState.cs` | In-memory model + change-event bus. |
| `State/ButtonNames.cs` | Canonical identifiers (`power`, `mute`, `btn01`..`btn10`). |
| `Tcp/TcpServer.cs` | JSON-per-line TCP server. |
| `Web/WebServer.cs` | HTTP/1.1 + SSE server, the panel page, and the settings API. |
| `Web/Static.cs` | Inlined HTML/CSS/JS for the panel page and the `/config` page. |
| `Mqtt/MqttClient.cs` | Dependency-free MQTT 3.1.1 client (CF 3.5-safe). |
| `Mqtt/MqttBridge.cs` | Bridges `DeviceState` ↔ MQTT; optional HA discovery. |

---

## 3. The volume-dial / panel-input saga

The hardest part of this project was getting the physical front panel —
buttons *and* the volume knob — to reach the program at all. The findings
below are firmware-specific and cost real time to discover; they're recorded
here so nobody re-derives them.

### 3.1 Events don't fire until you `Register()` the slot

The built-in `MPC3x30xTouchscreenSlot` device starts **un-registered**, and the
SDK only delivers `ButtonStateChange` / `PanelStateChange` callbacks for
registered devices. Subscribe to the events **first**, then call
`_panel.Register()`. Without it the panel looks completely dead while LED
writes still work — which makes it look like a firmware bug (it isn't).

### 3.2 New firmware gates every input behind an `Enable*` call

On firmware **v1.8001.6192.23139 (Dec 2025)**, `Register()` alone is **not
enough**. Each input must be explicitly enabled or the panel delivers nothing:

```csharp
_panel.EnablePowerButton();
_panel.EnableMuteButton();
for (uint i = 1; i <= 10; i++) _panel.EnableNumericalButton(i);
_panel.EnableVolumeControl();
```

(Older firmware, e.g. 1.8001.0298, delivered buttons with just `Register()`.
The `Enable*` calls are harmless there and required here, so we always make
them.)

### 3.3 The dial is the `Volume*` family, and it reports *absolute*

Reflecting `MPC3x3XXBase` shows the knob is exposed only through the `Volume*`
signals — there is **no** `Knob*` signal on the MPC3 (the `Knob*` event IDs in
the shared `FrontPanelEventIds` enum belong to TSW touchscreens). Relevant
`PanelStateChange` event IDs:

| ID | Name | Meaning |
| --- | --- | --- |
| 36 | `VolumeEventId` | absolute level changed (read `panel.Volume`, 0–65535) |
| 37 | `VolumeClockwiseEventId` | one detent clockwise |
| 38 | `VolumeCounterClockwiseEventId` | one detent counter-clockwise |
| 33 | `VolumeBargraphEventId` | bargraph feedback |
| 34 / 35 | VolumeControl Enabled / Disabled | arming acknowledgement |

Once `EnableVolumeControl()` is called, the panel integrates detents itself and
reports an **absolute** value via `Volume` / event 36 — it does *not* simply
pulse CW/CCW. The original code only handled CW/CCW (37/38), so it never saw a
thing. The fix: handle both, and **self-select** — the wrapper watches whether
the absolute `Volume` signal actually moves; if it does, it drives volume from
the absolute value and ignores the CW/CCW pulses (so a single detent is never
counted twice). On this hardware it locks into `mode=ABSOLUTE`.

Also: `VolumeBargraph` is a `NullSig` on the MPC3 — drive the LED ring through
`VolumeFeedback` only.

### 3.4 A resident Crestron app can own the panel

On one of the two MPC3 units tested, **no** physical input reached the program
through any channel — events *or* polled signal state — even with `Register()`
and every `Enable*` call. Diagnosis (`taskstat`): a second `SimplSharpPro.exe`
+ `CustomAppManager.exe` plus `CPHProcessor.exe` (Crestron Home Processor),
`HydrogenManager.exe` (Crestron Home OS codename), `CloudClient.exe`, and
`RedisServer.exe` were resident. A **resident Crestron Home / AV-Framework
custom app was consuming the front-panel input** before the user program could
see it. The program could still *write* LEDs and owned the OSD Front Panel
slot, which is why only config-acknowledgement events ever appeared.

**Tell:** if an MPC3 panel seems dead from a custom program, run `taskstat`.
A second `SimplSharpPro.exe` / `CPHProcessor` / `HydrogenManager` means a
resident app owns the panel — that's an environment fix (remove/disable the
resident app or reflash a clean image), not a code fix. On a clean unit the
code in this repo "just works."

### 3.5 Sensors

The MPC3-302 has **no occupancy / proximity / motion sensor**. The only sensor
the SDK exposes on the panel class is an **ambient-light** meter
(`AmbientLightLevelFeedBack`), used for auto-dimming the key backlights. The
`Proximity*` event IDs in `FrontPanelEventIds` are shared with TSW
touchscreens, which do have a PIR — the MPC3 does not.

---

## 4. MQTT

The MQTT layer (`Mqtt/MqttClient.cs` + `Mqtt/MqttBridge.cs`) is a
dependency-free MQTT 3.1.1 client (same role as
[fasteddy516/SimplMQTT](https://github.com/fasteddy516/SimplMQTT), but
CF-3.5-safe and with no SIMPL+ wrapper): CONNECT with optional credentials and
last-will, QoS-0 PUBLISH/SUBSCRIBE, keepalive, and auto-reconnect with backoff.
No TLS — front it with an MQTT-over-TLS proxy if you need encryption.

Topic tree, command/feedback mapping, and Home Assistant discovery details are
in the [README](../README.md#mqtt). In short: every LED (12), mute, the mute
LED, and the volume bargraph are **controllable** via `…/set`; button presses,
LED states, volume, and mute are **published** as feedback. Physical press and
LED are independent signals — pressing a key publishes its event but does not
auto-light the LED unless you command it.

---

## 5. Build & deploy

SIMPL# Pro `.cpz` packaging only runs inside the **VS 2008** IDE host (the
plugin's MSBuild post-build target is ignored by headless `msbuild`). The
`tools/` scripts drive VS 2008 via DTE COM automation, then deploy over
SSH/SFTP:

| Tool | Purpose |
| --- | --- |
| `tools/Build-Cpz.ps1` | Drive VS 2008 DTE → produce the `.cpz`. |
| `tools/Deploy-Cpz.ps1` | `pscp` upload + `plink progload`, auto-discovering the host key. |
| `tools/Build-And-Deploy.ps1` | Chain the two. |
| `tools/Watch-Stream.ps1` | Tail the JSON-over-TCP event stream (watch the dial/buttons live). |
| `tools/Crestron-Console.ps1` | Drive the Crestron text console over telnet when SSH is unavailable. |
| `tools/Test-MqttBroker.ps1` | Throwaway single-client MQTT broker stub for end-to-end MQTT testing. |

A fast `msbuild` compile (no `.cpz`) is useful for catching C# errors in ~5s;
use `Build-Cpz.ps1` for anything you intend to load. The code is **C# 3.0
against .NET Compact Framework 3.5** — no string interpolation, null-condition
operators, `nameof`, tuples, `async`/`await`, or `int.TryParse`.

---

## 6. Console diagnostics

Over SSH: `mpctcp help`. Notably `mpctcp diag` dumps live panel signal values
(volume raw + percent, CW/CCW booleans, button enable feedback, per-button
state, the self-selected volume mode) — the single most useful command when
the dial or buttons misbehave. `Hardware/Mpc3Wrapper.cs` also logs every
`PanelStateChange` event ID to `err`, the console, and the SSE/TCP stream, so a
knob turn or button press is fully traceable.
