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
    // We read the device off CrestronControlSystem.MPC3x30xTouchscreenSlot
    // and attach to the device-wide ButtonStateChange event, which fires for
    // every button on the front panel (Power, Mute, Button1..Button10).
    //
    // KNOWN FIRMWARE BUG (MPC3-302 1.8001.0251): the SDK never delivers
    // ButtonStateChange or PanelStateChange events for the local slot, so
    // physical button presses and rotary turns are silently dropped here.
    // Output (LED writes, bargraph) still works. The TCP server still emits
    // synthetic button events when the `mpctcp emit` console command is used,
    // which is the only way to verify the client-bound event path today.
    //
    // SDK references used here:
    //   MPC3Basic.ButtonStateChange      - device-wide press/release event
    //   MPC3Basic.Power / .Mute / .Button1..6
    //   MPC3x3XXBase.Button7..10
    //   MPC3Basic.FeedbackPower / .FeedbackMute / .Feedback1..6
    //   MPC3x3XXBase.Feedback7..10       - per-button LED set via .State
    //   MPC3x3XXBase.Volume              - rotary input sig (read)
    //   MPC3x3XXBase.VolumeFeedback      - bargraph output sig (write)
    public sealed class Mpc3Wrapper : IDisposable
    {
        private readonly CrestronControlSystem _cs;
        private readonly DeviceState _state;
        private MPC3x30xTouchscreen _panel;

        // IButton instance -> logical name. Reference equality is fine; the
        // SDK hands us the same Button instances on every event.
        private readonly Dictionary<object, string> _buttonToName = new Dictionary<object, string>();
        private readonly Dictionary<string, Feedback> _nameToFeedback = new Dictionary<string, Feedback>();

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

            BindButtons();
            BindFeedbacks();
            _panel.ButtonStateChange += OnButtonStateChange;
            _panel.PanelStateChange  += OnPanelStateChange;

            // Mirror local state changes back out to the hardware.
            _state.LedChanged    += OnLedRequested;
            _state.VolumeChanged += OnVolumeRequested;
            _state.MuteChanged   += OnMuteRequested;

            // Seed feedback so any LEDs left lit by a prior program version
            // are cleared and the bargraph reflects the default volume.
            foreach (var name in ButtonNames.All())
                ApplyLed(name, _state.GetLed(name));
            ApplyVolume(_state.GetVolumePercent());

            ErrorLog.Notice("[mpc3] initialized: 10 programmable buttons + Power + Mute + volume");
        }

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

        private void OnButtonStateChange(GenericBase device, ButtonEventArgs args)
        {
            try
            {
                string name;
                if (!_buttonToName.TryGetValue(args.Button, out name))
                {
                    ErrorLog.Notice("[mpc3] button event from unknown source");
                    return;
                }
                bool pressed = args.NewButtonState == eButtonState.Pressed;
                _state.RecordButtonEvent(name, pressed);
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mpc3] OnButtonStateChange: {0}", e.Message);
            }
        }

        // PanelStateChange surfaces rotary ticks + the absolute volume sig.
        // We watch the volume value here so that physically turning the dial
        // updates DeviceState. Each VolumeEventId carries the new 0..65535
        // sig value via _panel.Volume.UShortValue.
        private void OnPanelStateChange(GenericBase device, BaseEventArgs args)
        {
            try
            {
                if (args.EventId == FrontPanelEventIds.VolumeEventId)
                {
                    var raw = _panel.Volume.UShortValue;
                    var pct = (raw * 100) / 65535;
                    _state.SetVolumePercent(pct);
                    // Echo the same value back to the bargraph so the visual
                    // matches the dial position without lag.
                    _panel.VolumeFeedback.UShortValue = raw;
                }
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mpc3] OnPanelStateChange: {0}", e.Message);
            }
        }

        private void OnLedRequested(string name, bool on)
        {
            ApplyLed(name, on);
        }

        private void OnVolumeRequested(int percent)
        {
            ApplyVolume(percent);
        }

        // The mute LED is the physical indicator on the Mute key. Whenever
        // the soft mute state flips, mirror it onto the LED so the panel
        // visibly tracks what TCP clients have set.
        private void OnMuteRequested(bool muted)
        {
            ApplyLed(ButtonNames.Mute, muted);
        }

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

        private void ApplyVolume(int percent)
        {
            if (_panel == null) return;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            try
            {
                ushort raw = (ushort)((percent * 65535) / 100);
                _panel.VolumeFeedback.UShortValue = raw;
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mpc3] ApplyVolume({0}): {1}", percent, e.Message);
            }
        }

        public void Dispose()
        {
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
