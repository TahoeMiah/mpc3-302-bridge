# Installing without Crestron tools

You do **not** need Crestron Toolbox, SIMPL, or any Crestron software to install
this. A 3-Series processor is just a little Linux-ish box with **SFTP** and
**SSH** — so all you need is:

- an **SFTP client** (WinSCP, FileZilla, or the `sftp`/`scp` command line), and
- an **SSH client** (PuTTY, or the built-in `ssh` on Windows/macOS/Linux).

Plus, obviously: the processor's **IP address**, an **admin login**, and SSH
turned on (it usually is — see [troubleshooting](#troubleshooting) if not).

> The whole job is: copy one file up, run one command. That's it.

---

## 1. Grab the program

Download **`Mpc3TcpBridge.cpz`** from the
[**Releases page**](../../releases/latest). That single file *is* the program —
no unzipping, no extra assets.

## 2. Copy it onto the processor (SFTP)

Connect with SFTP to the processor on port **22**, log in as `admin`, and drop
the `.cpz` into the program slot directory. Slot 1 lives at **`/Program01/`**.

### Option A — WinSCP / FileZilla (GUI, easiest)

1. New connection → **SFTP**, host = the processor IP, port `22`, user `admin`,
   your password.
2. Accept the host-key prompt the first time.
3. On the remote side, open the **`/Program01/`** folder.
4. Drag `Mpc3TcpBridge.cpz` into it.

### Option B — command line

```bash
# OpenSSH (Windows 10/11, macOS, Linux)
scp Mpc3TcpBridge.cpz admin@<processor-ip>:/Program01/Mpc3TcpBridge.cpz
```

```powershell
# PuTTY's pscp (Windows)
pscp Mpc3TcpBridge.cpz admin@<processor-ip>:/Program01/Mpc3TcpBridge.cpz
```

```bash
# psftp / sftp interactive
sftp admin@<processor-ip>
sftp> cd /Program01
sftp> put Mpc3TcpBridge.cpz
sftp> bye
```

> **Use forward slashes** (`/Program01/`) for SFTP. Slot 2 would be
> `/Program02/`, slot 10 `/Program10/`, etc.

## 3. Load it (SSH)

SSH in and tell the processor to unpack and start the program in that slot:

```bash
ssh admin@<processor-ip>
MPC3>progload -P:01
```

(That's a zero-padded slot number: `-P:01`, not `-P:1`.) You'll see it unzip and
print **`Program Start successfully sent for App 1`**.

## 4. Check it's alive

```bash
MPC3>err            # look for "**Program 1 Started**" and any [mpc3]/[tcp]/[web] lines
```

Then just open a browser to **`http://<processor-ip>:8080/`** — you should get
the live keypad. Done. 🎉

## 5. (Optional) point it at MQTT

You don't need to touch any files for this. Open the web UI, click the ⚙️ gear
(or go to `http://<processor-ip>:8080/config`), fill in your MQTT broker, **Save**,
then **Restart program**. The page shows a connected indicator.

Prefer config-as-a-file? Upload an `appsettings.json` (see
[`appsettings.sample.json`](../crestron/Mpc3TcpBridge/appsettings.sample.json)) to
**`/User/appsettings.json`** via SFTP, then `progres -P:01` over SSH to restart.

---

## Updating to a new version

Same as installing — SFTP the new `.cpz` over the old one, then reload. If the
processor stubbornly keeps the old code, force a clean reload over SSH:

```bash
MPC3>progreset -P:01
MPC3>del \Program01\Mpc3TcpBridge.dll
MPC3>del \Program01\manifest.info
MPC3>del \Program01\manifest.ser
MPC3>progload -P:01
```

> Note the **backslashes** here — Crestron's `del`/`progload` console commands
> use `\Program01\`, while SFTP uses `/Program01/`. Yes, really.

## Troubleshooting

- **SSH/SFTP refuses to connect.** SSH may be off. The only sure-fire way to turn
  it on without Crestron tools is the local console: connect a USB cable to the
  processor's front **COMPUTER/console** port (or use the on-box console) and run
  the SSH-enable command for your firmware. If that's not an option, this is the
  one step where Crestron Toolbox's "Manage SSH" toggle is the easy button.
- **`http://<ip>:8080` shows a generic error, not the panel.** The program isn't
  running. Re-check `err` for `**Program 1 Started**`; if you see
  *"...or one of its dependencies was not found"*, the `.cpz` didn't unpack —
  re-upload and `progload` again.
- **Buttons/knob do nothing but the web UI works.** Something else on the box may
  own the front panel (e.g. a resident Crestron Home / AV-Framework app). Run
  `taskstat` over SSH — a second `SimplSharpPro.exe` / `CPHProcessor` is the
  tell. See [`DESIGN-NOTES.md`](DESIGN-NOTES.md) for the full story.
- **Wrong firmware.** Needs a 3-Series MPC3-301/302. Minimum firmware is modest
  (1.009.0029); it's tested on 1.8001.x.
