using System;
using System.Collections.Generic;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro;
using Crestron.SimplSharpPro.DeviceSupport;
using Mpc3TcpBridge.State;

namespace Mpc3TcpBridge.Hardware
{
    // Thin facade over the MPC3-302's built-in keypad / LEDs / volume.
    //
    // The MPC3-302 is itself the processor - there is no IPID to register.
    // We read the device off CrestronControlSystem.MPC3x30xTouchscreenSlot.
    //
    //   * Buttons (Power, Mute, Button1..10): ButtonStateChange.
    //
    //   * Volume knob - what the SDK actually exposes (confirmed by reflecting
    //     Crestron.SimplSharpPro MPC3x3XXBase on firmware 1.8001.0298):
    //       - Volume                       UShortOutputSig  (absolute, FROM panel)
    //       - VolumeClockwiseFeedback      BoolOutputSig    (CW detent pulse)
    //       - VolumeCounterClockwiseFeedback BoolOutputSig  (CCW detent pulse)
    //       - VolumeControlEnabledFeedBack BoolOutputSig    (did the knob arm?)
    //       - VolumeFeedback / VolumeBargraph UShortInputSig (drive the LED ring)
    //     PanelStateChange event ids of interest:
    //       33 VolumeBargraph  34 VolumeControlEnabled  35 Disabled
    //       36 Volume (absolute)  37 Clockwise  38 CounterClockwise
    //
    //     There is NO Knob* sig on MPC3 (the Knob* event ids 45-52 in the enum
    //     belong to other panels). The dial reports ONLY through the Volume*
    //     family above.
    //
    //   * WHY THE DIAL WAS DEAD (working hypothesis): once EnableVolumeControl()
    //     is called the panel firmware integrates the detents itself and reports
    //     an ABSOLUTE level via Volume / VolumeEventId(36) - it does NOT pulse
    //     CW/CCW. The previous code only handled CW/CCW (37/38), so it never saw
    //     a thing. This build handles BOTH and self-selects whichever the
    //     hardware actually drives, and logs every event id so we can confirm.
    //
    //   * Prereqs that were the ORIGINAL bug, still required:
    //       1. Register() the slot device (events only fire when registered).
    //       2. EnableVolumeControl() (the knob is disabled by default).
    public sealed class Mpc3Wrapper : IDisposable
    {
        // Percent change per encoder detent when running in RELATIVE mode.
        private const int VolumeStepPercent = 2;

        // Human-readable names for FrontPanelEventIds so the logs are legible.
        private static readonly Dictionary<int, string> EventNames = BuildEventNames();

        private readonly CrestronControlSystem _cs;
        private readonly DeviceState _state;
        private MPC3x30xTouchscreen _panel;

        // IButton instance -> logical name. Reference equality is fine; the
        // SDK hands us the same Button instances on every event.
        private readonly Dictionary<object, string> _buttonToName = new Dictionary<object, string>();
        private readonly Dictionary<string, Feedback> _nameToFeedback = new Dictionary<string, Feedback>();

        // Poll the volume output sigs AND each button's State as a belt-and-
        // suspenders fallback: even if ButtonStateChange / PanelStateChange stay
        // quiet, the underlying sig VALUES may still update live.
        private CTimer _knobPoll;
        private bool _lastCw;
        private bool _lastCcw;
        private int _lastVolumeRaw = -1;        // -1 = not yet sampled
        private int _heartbeatPolls;

        // Per-button last polled State, for rising-edge detection in the poll.
        private readonly List<Button> _polledButtons = new List<Button>();
        private readonly Dictionary<object, bool> _lastButtonPressed = new Dictionary<object, bool>();

        // Volume reporting mode auto-detection. Once we've seen the absolute
        // Volume sig actually move we trust it and ignore CW/CCW (so a single
        // detent isn't counted twice). Until then we honour CW/CCW pulses.
        private bool _absoluteSeen;

        public Mpc3Wrapper(CrestronControlSystem cs, DeviceState state)
        {
            _cs = cs;
            _state = state;
        }

        public void Initialize()
        {
            var slot = _cs.MPC3x30xTouchscreenSlot;
            if (slot == null)
            {
                ErrorLog.Error("[mpc3] MPC3x30xTouchscreenSlot is null - is this actually running on an MPC3-301/302?");
                return;
            }
            _panel = slot;

            Diag("Initialize: panel type={0}", _panel.GetType().Name);

            BindButtons();
            BindFeedbacks();
            _panel.ButtonStateChange += OnButtonStateChange;
            _panel.PanelStateChange  += OnPanelStateChange;

            // Built-in slot devices start un-registered; the SDK only delivers
            // callbacks for registered devices. Register() is a no-op if it
            // was already registered. Subscribe BEFORE Register() since events
            // can fire immediately on registration.
            try
            {
                if (!_panel.Registered)
                {
                    var rc = _panel.Register();
                    Diag("panel.Register() -> {0}", rc);
                }
                else
                {
                    Diag("panel already registered");
                }
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mpc3] panel.Register() threw: {0}", e.Message);
            }

            // Arm the inputs. On firmware v1.8001.6192 (Dec 2025) Register() is
            // NOT enough: each button is gated behind its own Enable* call, and
            // the rotary behind EnableVolumeControl(). Without these, physical
            // presses/turns never reach the program (confirmed: buttons + knob
            // were both dead with only EnableVolumeControl).
            try
            {
                _panel.EnablePowerButton();
                _panel.EnableMuteButton();
                for (uint i = 1; i <= 10; i++)
                    _panel.EnableNumericalButton(i);
                Diag("EnablePowerButton/MuteButton/NumericalButton(1..10) called");
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mpc3] EnableButtons threw: {0}", e.Message);
            }
            try
            {
                _panel.EnableVolumeControl();
                Diag("EnableVolumeControl() called");
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mpc3] EnableVolumeControl() threw: {0}", e.Message);
            }

            // Confirm the arm actually took, and snapshot the starting raw value.
            try
            {
                Diag("VolumeControlEnabledFeedBack={0} registered={1} online={2}",
                    SafeBool(_panel.VolumeControlEnabledFeedBack),
                    _panel.Registered, _panel.IsOnline);
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mpc3] post-enable probe: {0}", e.Message);
            }

            // Mirror local state changes back out to the hardware.
            _state.LedChanged    += OnLedRequested;
            _state.VolumeChanged += OnVolumeRequested;
            _state.MuteChanged   += OnMuteRequested;

            // Seed LEDs + the bargraph from current logical state.
            foreach (var name in ButtonNames.All())
                ApplyLed(name, _state.GetLed(name));
            ApplyVolume(_state.GetVolumePercent());

            // Poll the volume sigs (events may be quiet; values may be live).
            _lastCw = false;
            _lastCcw = false;
            _lastVolumeRaw = SafeUShort(GetVolumeRawSig());
            _knobPoll = new CTimer(PollKnob, null, 200, 50);

            ErrorLog.Notice("[mpc3] initialized: 12 buttons + volume (absolute+relative, polled, max-diag)");
        }

        // ---- Diagnostics ------------------------------------------------------

        // One line to three places: ErrorLog (survives in `err`), the live SSH
        // console, and the SSE/TCP stream (so a connected web/tcp client sees it
        // too). This is the "maximum debugging" surface.
        private void Diag(string fmt, params object[] args)
        {
            string msg;
            try { msg = string.Format(fmt, args); }
            catch { msg = fmt; }
            ErrorLog.Notice("[mpc3] {0}", msg);
            try { CrestronConsole.PrintLine("[mpc3] {0}", msg); }
            catch { }
        }

        // Push a short token onto the button event stream so SSE/TCP clients can
        // watch the knob diagnostics live (kept terse - this fires per detent).
        private void DiagStream(string token)
        {
            try { _state.RecordButtonEvent(token, true); }
            catch { }
        }

        private static bool SafeBool(BoolOutputSig s)
        {
            try { return s != null && s.BoolValue; }
            catch { return false; }
        }

        private static ushort SafeUShort(UShortOutputSig s)
        {
            try { return s == null ? (ushort)0 : s.UShortValue; }
            catch { return 0; }
        }

        private UShortOutputSig GetVolumeRawSig()
        {
            try { return _panel.Volume; }
            catch { return null; }
        }

        // Called by the `mpctcp diag` console command - dump everything live.
        public void DumpDiagnostics(Action<string> write)
        {
            if (write == null) return;
            if (_panel == null) { write("panel is null (not on MPC3 hardware?)"); return; }
            try
            {
                write(string.Format("registered={0} online={1}", _panel.Registered, _panel.IsOnline));
                write(string.Format("VolumeControlEnabledFeedBack={0} DisabledFeedBack={1}",
                    SafeBool(_panel.VolumeControlEnabledFeedBack),
                    SafeBool(_panel.VolumeControlDisabledFeedBack)));
                ushort raw = SafeUShort(_panel.Volume);
                write(string.Format("Volume(raw absolute)={0} -> {1}%", raw, RawToPercent(raw)));
                write(string.Format("VolumeClockwiseFeedback={0} VolumeCounterClockwiseFeedback={1}",
                    SafeBool(_panel.VolumeClockwiseFeedback),
                    SafeBool(_panel.VolumeCounterClockwiseFeedback)));
                write(string.Format("PowerBtnEnabled={0} MuteBtnEnabled={1}",
                    SafeBool(_panel.PowerButtonEnabledFeedBack),
                    SafeBool(_panel.MuteButtonEnabledFeedBack)));
                // Live button states (proves whether press updates the sig).
                var sb = new System.Text.StringBuilder("btnState:");
                for (int i = 0; i < _polledButtons.Count; i++)
                {
                    Button b = _polledButtons[i];
                    string nm; if (!_buttonToName.TryGetValue(b, out nm)) nm = "?";
                    bool pr = false; try { pr = b.State == eButtonState.Pressed; } catch { }
                    if (pr) sb.Append(" " + nm + "=DOWN");
                }
                write(sb.ToString());
                write(string.Format("mode={0} stateVolume={1}%",
                    _absoluteSeen ? "ABSOLUTE" : "relative/unknown",
                    _state.GetVolumePercent()));
            }
            catch (Exception e)
            {
                write("diag error: " + e.Message);
            }
        }

        // ---- Polling fallback -------------------------------------------------

        private void PollKnob(object unused)
        {
            try
            {
                // Absolute path: watch the Volume output sig for any movement.
                int raw = SafeUShort(GetVolumeRawSig());
                if (raw != _lastVolumeRaw)
                {
                    int prev = _lastVolumeRaw;
                    _lastVolumeRaw = raw;
                    if (prev >= 0)   // ignore the very first sample
                    {
                        OnAbsoluteVolume(raw, "poll");
                    }
                }

                // Relative path: rising-edge detect the CW/CCW pulse booleans.
                bool cw = SafeBool(_panel.VolumeClockwiseFeedback);
                bool ccw = SafeBool(_panel.VolumeCounterClockwiseFeedback);
                if (cw && !_lastCw)  OnRelativeDetent(+1, "poll");
                if (ccw && !_lastCcw) OnRelativeDetent(-1, "poll");
                _lastCw = cw;
                _lastCcw = ccw;

                // Button fallback: rising/falling-edge detect each button's State
                // in case ButtonStateChange events stay quiet on this firmware.
                for (int i = 0; i < _polledButtons.Count; i++)
                {
                    Button b = _polledButtons[i];
                    bool pressed;
                    try { pressed = b.State == eButtonState.Pressed; }
                    catch { continue; }
                    bool last;
                    if (!_lastButtonPressed.TryGetValue(b, out last)) last = false;
                    if (pressed != last)
                    {
                        _lastButtonPressed[b] = pressed;
                        string name;
                        if (_buttonToName.TryGetValue(b, out name))
                        {
                            Diag("button(poll) {0} {1}", name, pressed ? "PRESSED" : "released");
                            _state.RecordButtonEvent(name, pressed);
                        }
                    }
                }

                // Heartbeat ~ every 5s (100 * 50ms): proof the poll is alive and
                // a periodic snapshot of the raw sigs, console-only (no SSE spam).
                if (++_heartbeatPolls >= 100)
                {
                    _heartbeatPolls = 0;
                    try
                    {
                        CrestronConsole.PrintLine(
                            "[mpc3][hb] volRaw={0} cw={1} ccw={2} volEnabled={3} mode={4} vol={5}%",
                            raw, cw, ccw, SafeBool(_panel.VolumeControlEnabledFeedBack),
                            _absoluteSeen ? "ABS" : "rel", _state.GetVolumePercent());
                    }
                    catch { }
                }
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mpc3] PollKnob: {0}", e.Message);
            }
        }

        // ---- Hardware event handlers -----------------------------------------

        private void OnPanelStateChange(GenericBase device, BaseEventArgs args)
        {
            try
            {
                int id = args.EventId;
                string name;
                if (!EventNames.TryGetValue(id, out name)) name = "?";

                // DIAG: surface EVERY panel event id, with the live absolute raw
                // value, to console + SSE so we can see exactly what the knob does.
                ushort raw = SafeUShort(GetVolumeRawSig());
                Diag("PanelStateChange id={0}({1}) volRaw={2}", id, name, raw);
                DiagStream("evt#" + id + "(" + name + ")");

                if (id == FrontPanelEventIds.VolumeEventId)
                {
                    // Absolute report from the panel firmware.
                    OnAbsoluteVolume(raw, "event");
                }
                else if (id == FrontPanelEventIds.VolumeClockwiseEventId)
                {
                    OnRelativeDetent(+1, "event");
                }
                else if (id == FrontPanelEventIds.VolumeCounterClockwiseEventId)
                {
                    OnRelativeDetent(-1, "event");
                }
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mpc3] OnPanelStateChange: {0}", e.Message);
            }
        }

        // Absolute volume came off the panel (Volume sig / VolumeEventId). Trust
        // it as the source of truth and switch into ABSOLUTE mode.
        private void OnAbsoluteVolume(int raw, string src)
        {
            int percent = RawToPercent(raw);
            if (!_absoluteSeen)
            {
                _absoluteSeen = true;
                Diag("absolute volume detected via {0}; switching to ABSOLUTE mode", src);
            }
            DiagStream("volAbs#" + percent);
            int cur = _state.GetVolumePercent();
            if (percent != cur)
                _state.SetVolumePercent(percent);   // fans out to web + tcp + bargraph
        }

        // A relative detent (CW=+1 / CCW=-1). Ignored once absolute mode wins so
        // a single physical detent never counts twice.
        private void OnRelativeDetent(int dir, string src)
        {
            DiagStream(dir > 0 ? "knobCW(" + src + ")" : "knobCCW(" + src + ")");
            if (_absoluteSeen)
                return;   // panel is reporting absolute; don't double-count
            AdjustVolume(dir * VolumeStepPercent);
        }

        private void OnButtonStateChange(GenericBase device, ButtonEventArgs args)
        {
            try
            {
                string name;
                if (!_buttonToName.TryGetValue(args.Button, out name))
                {
                    bool up = args.NewButtonState == eButtonState.Pressed;
                    DiagStream("btnUnk#" + args.Button.Number);
                    Diag("unmapped button num={0} state={1}", args.Button.Number, args.NewButtonState);
                    return;
                }
                bool pressed = args.NewButtonState == eButtonState.Pressed;
                Diag("button {0} {1}", name, pressed ? "PRESSED" : "released");
                _state.RecordButtonEvent(name, pressed);
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mpc3] OnButtonStateChange: {0}", e.Message);
            }
        }

        // ---- Outbound (state -> hardware) ------------------------------------

        private void AdjustVolume(int deltaPercent)
        {
            int cur = _state.GetVolumePercent();
            int next = cur + deltaPercent;
            if (next < 0) next = 0;
            if (next > 100) next = 100;
            if (next == cur) return;
            _state.SetVolumePercent(next);
        }

        private void OnLedRequested(string name, bool on) { ApplyLed(name, on); }
        private void OnVolumeRequested(int percent) { ApplyVolume(percent); }
        private void OnMuteRequested(bool muted) { ApplyLed(ButtonNames.Mute, muted); }

        private void ApplyLed(string name, bool on)
        {
            try
            {
                Feedback fb;
                if (!_nameToFeedback.TryGetValue(name, out fb)) return;
                fb.State = on;
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mpc3] ApplyLed({0}={1}): {2}", name, on, e.Message);
            }
        }

        // Drive the LED ring. VolumeBargraph is a NullSig on this panel (it threw
        // "set UShortValue of NullSig"); VolumeFeedback is the real bargraph sig.
        private void ApplyVolume(int percent)
        {
            if (_panel == null) return;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            ushort raw = (ushort)((percent * 65535) / 100);
            try { _panel.VolumeFeedback.UShortValue = raw; }
            catch (Exception e) { ErrorLog.Warn("[mpc3] VolumeFeedback({0}): {1}", percent, e.Message); }
        }

        // ---- Binding + helpers -----------------------------------------------

        private void BindButtons()
        {
            _buttonToName[_panel.Power] = ButtonNames.Power;
            _buttonToName[_panel.Mute]  = ButtonNames.Mute;
            _buttonToName[_panel.Button1]  = ButtonNames.Programmable(1);
            _buttonToName[_panel.Button2]  = ButtonNames.Programmable(2);
            _buttonToName[_panel.Button3]  = ButtonNames.Programmable(3);
            _buttonToName[_panel.Button4]  = ButtonNames.Programmable(4);
            _buttonToName[_panel.Button5]  = ButtonNames.Programmable(5);
            _buttonToName[_panel.Button6]  = ButtonNames.Programmable(6);
            _buttonToName[_panel.Button7]  = ButtonNames.Programmable(7);
            _buttonToName[_panel.Button8]  = ButtonNames.Programmable(8);
            _buttonToName[_panel.Button9]  = ButtonNames.Programmable(9);
            _buttonToName[_panel.Button10] = ButtonNames.Programmable(10);

            // Same set, kept as concrete Button refs so the poll can read .State.
            _polledButtons.Add(_panel.Power);
            _polledButtons.Add(_panel.Mute);
            _polledButtons.Add(_panel.Button1);
            _polledButtons.Add(_panel.Button2);
            _polledButtons.Add(_panel.Button3);
            _polledButtons.Add(_panel.Button4);
            _polledButtons.Add(_panel.Button5);
            _polledButtons.Add(_panel.Button6);
            _polledButtons.Add(_panel.Button7);
            _polledButtons.Add(_panel.Button8);
            _polledButtons.Add(_panel.Button9);
            _polledButtons.Add(_panel.Button10);
        }

        private void BindFeedbacks()
        {
            _nameToFeedback[ButtonNames.Power] = _panel.FeedbackPower;
            _nameToFeedback[ButtonNames.Mute]  = _panel.FeedbackMute;
            _nameToFeedback[ButtonNames.Programmable(1)]  = _panel.Feedback1;
            _nameToFeedback[ButtonNames.Programmable(2)]  = _panel.Feedback2;
            _nameToFeedback[ButtonNames.Programmable(3)]  = _panel.Feedback3;
            _nameToFeedback[ButtonNames.Programmable(4)]  = _panel.Feedback4;
            _nameToFeedback[ButtonNames.Programmable(5)]  = _panel.Feedback5;
            _nameToFeedback[ButtonNames.Programmable(6)]  = _panel.Feedback6;
            _nameToFeedback[ButtonNames.Programmable(7)]  = _panel.Feedback7;
            _nameToFeedback[ButtonNames.Programmable(8)]  = _panel.Feedback8;
            _nameToFeedback[ButtonNames.Programmable(9)]  = _panel.Feedback9;
            _nameToFeedback[ButtonNames.Programmable(10)] = _panel.Feedback10;
        }

        private static int RawToPercent(int raw)
        {
            if (raw < 0) raw = 0;
            if (raw > 65535) raw = 65535;
            return (raw * 100 + 32767) / 65535;   // rounded
        }

        private static Dictionary<int, string> BuildEventNames()
        {
            var d = new Dictionary<int, string>();
            d[33] = "VolumeBargraph";
            d[34] = "VolumeControlEnabled";
            d[35] = "VolumeControlDisabled";
            d[36] = "Volume";
            d[37] = "VolumeClockwise";
            d[38] = "VolumeCounterClockwise";
            d[44] = "LEDBrightness";
            d[45] = "KnobClockwise";
            d[46] = "KnobCounterClockwise";
            d[47] = "KnobAcc";
            d[49] = "KnobDeltaAcc";
            d[50] = "KnobDeltaNoAcc";
            d[51] = "KnobPktDelta";
            d[52] = "KnobPktTime";
            return d;
        }

        public void Dispose()
        {
            try
            {
                if (_knobPoll != null)
                {
                    _knobPoll.Stop();
                    _knobPoll.Dispose();
                    _knobPoll = null;
                }
            }
            catch { }
            try
            {
                if (_panel != null)
                {
                    _panel.ButtonStateChange -= OnButtonStateChange;
                    _panel.PanelStateChange  -= OnPanelStateChange;
                }
            }
            catch { }
            _state.LedChanged    -= OnLedRequested;
            _state.VolumeChanged -= OnVolumeRequested;
            _state.MuteChanged   -= OnMuteRequested;
        }
    }
}
