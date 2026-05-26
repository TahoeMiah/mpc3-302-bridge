using System;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro;
using Crestron.SimplSharpPro.CrestronThread;
using Mpc3TcpBridge.Config;
using Mpc3TcpBridge.Hardware;
using Mpc3TcpBridge.State;
using Mpc3TcpBridge.Tcp;
using Mpc3TcpBridge.Web;

namespace Mpc3TcpBridge
{
    // Entry point. Wires together:
    //   1. AppSettings (from /user/appsettings.json or defaults)
    //   2. DeviceState (in-memory model)
    //   3. Mpc3Wrapper (panel hardware <-> DeviceState)
    //   4. TcpServer  (JSON/TCP <-> DeviceState)
    //
    // Plus a `mpctcp` console command for hand-driven verification over SSH,
    // which is the only reliable way to test the server -> client event path
    // until the MPC3 panel-input firmware bug is resolved.
    public class ControlSystem : CrestronControlSystem
    {
        private AppSettings _settings;
        private DeviceState _state;
        private Mpc3Wrapper _hw;
        private TcpServer _tcp;
        private WebServer _web;

        public ControlSystem()
            : base()
        {
            try
            {
                Thread.MaxNumberOfUserThreads = 20;
                CrestronEnvironment.ProgramStatusEventHandler += OnProgramStatus;
                CrestronEnvironment.EthernetEventHandler      += OnEthernetEvent;
            }
            catch (Exception e)
            {
                ErrorLog.Error("ControlSystem ctor: {0}", e.Message);
            }
        }

        public override void InitializeSystem()
        {
            try
            {
                CrestronConsole.PrintLine("=== mpc3-tcp-bridge starting ===");

                _settings = AppSettings.LoadOrDefault();
                _state = new DeviceState();
                _state.SetVolumePercent(_settings.Volume.DefaultLevel);

                CrestronConsole.PrintLine(
                    "Settings: tcp={0}:{1} max={2} buf={3}",
                    _settings.Tcp.BindAddress,
                    _settings.Tcp.Port,
                    _settings.Tcp.MaxClients,
                    _settings.Tcp.BufferBytes);

                // Order: hardware must observe state events from TcpServer's
                // inbound commands, so wire the hardware first.
                _hw = new Mpc3Wrapper(this, _state);
                _hw.Initialize();

                _tcp = new TcpServer(_settings, _state);
                _tcp.Start();

                if (_settings.Web != null && _settings.Web.Port > 0)
                {
                    _web = new WebServer(_state, _settings.Web.Port, _settings.Web.BindAddress);
                    _web.Start();
                }
                else
                {
                    CrestronConsole.PrintLine("[web] disabled (Web.Port=0)");
                }

                RegisterConsoleCommands();
                CrestronConsole.PrintLine(
                    "=== mpc3-tcp-bridge ready (tcp={0}, web={1}) ===",
                    _settings.Tcp.Port,
                    _web == null ? 0 : _web.Port);
            }
            catch (Exception e)
            {
                ErrorLog.Error("InitializeSystem: {0}", e.Message);
            }
        }

        // Console helpers. Try `mpctcp help` from the processor's SSH console.
        // The `emit` command is the most important one today: it lets you
        // verify that connected TCP clients are receiving button events
        // without depending on the (currently broken) panel input.
        private void RegisterConsoleCommands()
        {
            try
            {
                CrestronConsole.AddNewConsoleCommand(
                    OnMpcConsole,
                    "mpctcp",
                    "TCP bridge controls. Try 'mpctcp help'.",
                    ConsoleAccessLevelEnum.AccessOperator);
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[console] register: {0}", e.Message);
            }
        }

        private void OnMpcConsole(string argString)
        {
            try
            {
                var parts = (argString ?? "").Trim().Split(' ');
                var cmd = parts.Length > 0 ? parts[0].ToLower() : "";

                if (cmd == "state")
                {
                    var snap = _state.CaptureSnapshot();
                    CrestronConsole.ConsoleCommandResponse(
                        "volume={0} muted={1} clients={2}\r\n",
                        snap.VolumePercent, snap.Muted, _tcp == null ? 0 : _tcp.ClientCount);
                    foreach (var kv in snap.Leds)
                        CrestronConsole.ConsoleCommandResponse("led {0,-8} = {1}\r\n", kv.Key, kv.Value ? "ON" : "off");
                }
                else if (cmd == "led" && parts.Length >= 3)
                {
                    _state.SetLed(parts[1], IsOn(parts[2]));
                    CrestronConsole.ConsoleCommandResponse("led {0} -> {1}\r\n", parts[1], _state.GetLed(parts[1]));
                }
                else if ((cmd == "vol" || cmd == "volume") && parts.Length >= 2)
                {
                    int v;
                    if (Parse.TryInt(parts[1], out v))
                    {
                        _state.SetVolumePercent(v);
                        CrestronConsole.ConsoleCommandResponse("volume -> {0}\r\n", _state.GetVolumePercent());
                    }
                }
                else if (cmd == "mute" && parts.Length >= 2)
                {
                    _state.SetMuted(IsOn(parts[1]));
                    CrestronConsole.ConsoleCommandResponse("mute -> {0}\r\n", _state.GetMuted());
                }
                else if (cmd == "emit" && parts.Length >= 3)
                {
                    // mpctcp emit btn03 press|release
                    bool pressed = parts[2].ToLower().StartsWith("p");
                    _state.RecordButtonEvent(parts[1], pressed);
                    CrestronConsole.ConsoleCommandResponse(
                        "emit {0} {1} -> broadcast\r\n", parts[1], pressed ? "PRESSED" : "RELEASED");
                }
                else if (cmd == "clients")
                {
                    CrestronConsole.ConsoleCommandResponse(
                        "tcp clients: {0}    web clients: {1} (sse: {2})\r\n",
                        _tcp == null ? 0 : _tcp.ClientCount,
                        _web == null ? 0 : _web.ClientCount,
                        _web == null ? 0 : _web.SseClientCount);
                }
                else
                {
                    CrestronConsole.ConsoleCommandResponse(
                        "usage:\r\n" +
                        "  mpctcp state                       - dump current state\r\n" +
                        "  mpctcp led <name> on|off           - set LED (power/mute/btn01..btn10)\r\n" +
                        "  mpctcp vol <0..100>                - set volume bargraph\r\n" +
                        "  mpctcp mute on|off                 - set mute state\r\n" +
                        "  mpctcp emit <name> press|release   - inject a fake button event\r\n" +
                        "  mpctcp clients                     - show connected TCP client count\r\n");
                }
            }
            catch (Exception e)
            {
                CrestronConsole.ConsoleCommandResponse("error: {0}\r\n", e.Message);
            }
        }

        private static bool IsOn(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim().ToLower();
            return s == "on" || s == "1" || s == "true";
        }

        private void OnProgramStatus(eProgramStatusEventType type)
        {
            if (type == eProgramStatusEventType.Stopping)
            {
                CrestronConsole.PrintLine("=== mpc3-tcp-bridge stopping ===");
                try { if (_web != null) _web.Dispose(); } catch { }
                try { if (_tcp != null) _tcp.Dispose(); } catch { }
                try { if (_hw  != null) _hw.Dispose();  } catch { }
            }
        }

        private void OnEthernetEvent(EthernetEventArgs args)
        {
            CrestronConsole.PrintLine(
                "[eth] adapter={0} event={1}",
                args.EthernetAdapter,
                args.EthernetEventType);
        }
    }
}
