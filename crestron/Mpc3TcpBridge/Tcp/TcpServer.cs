using System;
using System.Collections.Generic;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;
using Mpc3TcpBridge.Config;
using Mpc3TcpBridge.State;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mpc3TcpBridge.Tcp
{
    // JSON-per-line TCP server. One Crestron TCPServer instance accepts up
    // to MaxClients concurrent connections; each client gets its own line
    // accumulator so partial reads don't lose data.
    //
    // Inbound (client -> server): one JSON object per line. Examples:
    //     {"cmd":"led","name":"btn01","on":true}
    //     {"cmd":"vol","level":75}
    //     {"cmd":"state"}
    //     {"cmd":"ping"}
    //     {"cmd":"emit","name":"btn03","pressed":true}   <- diagnostic
    //
    // Outbound (server -> client): broadcast to every connected client on
    // every DeviceState change. Examples:
    //     {"event":"button","name":"btn03","pressed":true,"at_utc":"..."}
    //     {"event":"volume","level":42}
    //     {"event":"mute","on":true}
    //     {"event":"led","name":"btn01","on":true}
    //
    // The {"event":"hello",...} announcement is sent unsolicited to each new
    // connection so a client can identify the device without polling.
    public sealed class TcpServer : IDisposable
    {
        private const string Version = "0.1.0";

        private readonly AppSettings _settings;
        private readonly DeviceState _state;
        private TCPServer _server;
        private readonly object _lock = new object();
        // clientIndex -> per-client line accumulator
        private readonly Dictionary<uint, StringBuilder> _accum = new Dictionary<uint, StringBuilder>();
        private bool _disposing;

        public TcpServer(AppSettings settings, DeviceState state)
        {
            _settings = settings;
            _state = state;
        }

        public int Port { get { return _settings.Tcp.Port; } }

        public int ClientCount
        {
            get
            {
                var s = _server;
                if (s == null) return 0;
                try { return s.NumberOfClientsConnected; }
                catch { return 0; }
            }
        }

        public void Start()
        {
            try
            {
                int port = _settings.Tcp.Port > 0 ? _settings.Tcp.Port : 8023;
                int bufBytes = _settings.Tcp.BufferBytes > 0 ? _settings.Tcp.BufferBytes : 4096;
                int maxClients = _settings.Tcp.MaxClients > 0 ? _settings.Tcp.MaxClients : 8;
                string bind = string.IsNullOrEmpty(_settings.Tcp.BindAddress) ? "0.0.0.0" : _settings.Tcp.BindAddress;

                _server = new TCPServer(bind, port, bufBytes, EthernetAdapterType.EthernetUnknownAdapter, maxClients);
                _server.SocketStatusChange += OnSocketStatusChange;

                var rc = _server.WaitForConnectionAsync(OnClientConnected);
                ErrorLog.Notice("[tcp] listening on {0}:{1} (max={2}, buf={3}) - waitForConn={4}",
                    bind, port, maxClients, bufBytes, rc);

                // Subscribe to state changes so we can broadcast them.
                _state.LedChanged    += OnLedChanged;
                _state.VolumeChanged += OnVolumeChanged;
                _state.MuteChanged   += OnMuteChanged;
                _state.ButtonEvent   += OnButtonEvent;
            }
            catch (Exception e)
            {
                ErrorLog.Error("[tcp] Start failed: {0}", e.Message);
            }
        }

        // ---- Crestron callbacks ----

        private void OnSocketStatusChange(TCPServer server, uint clientIndex, SocketStatus serverSocketStatus)
        {
            // Useful for tracing client churn during deploy verification.
            try
            {
                ErrorLog.Notice("[tcp] client {0} -> {1}", clientIndex, serverSocketStatus);
            }
            catch { }
        }

        private void OnClientConnected(TCPServer server, uint clientIndex)
        {
            if (_disposing) return;
            try
            {
                if (clientIndex == 0)
                {
                    // 0 means accept itself failed - re-arm and bail.
                    ErrorLog.Warn("[tcp] WaitForConnectionAsync returned clientIndex=0");
                    server.WaitForConnectionAsync(OnClientConnected);
                    return;
                }

                string addr = SafeClientAddress(server, clientIndex);
                ErrorLog.Notice("[tcp] client {0} connected from {1}", clientIndex, addr);

                lock (_lock)
                {
                    _accum[clientIndex] = new StringBuilder(256);
                }

                // Greet immediately so the client can identify us without
                // sending a single byte.
                SendHello(clientIndex);

                // Start reading from this client.
                server.ReceiveDataAsync(clientIndex, OnReceiveData);

                // Re-arm the accept slot for the next inbound connection.
                server.WaitForConnectionAsync(OnClientConnected);
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[tcp] OnClientConnected: {0}", e.Message);
                try { server.WaitForConnectionAsync(OnClientConnected); } catch { }
            }
        }

        private void OnReceiveData(TCPServer server, uint clientIndex, int bytesReceived)
        {
            if (_disposing) return;
            try
            {
                if (bytesReceived <= 0)
                {
                    DropClient(server, clientIndex, "remote closed");
                    return;
                }

                var buf = server.GetIncomingDataBufferForSpecificClient(clientIndex);
                if (buf == null || buf.Length == 0)
                {
                    DropClient(server, clientIndex, "null/empty buffer");
                    return;
                }

                // Append bytes to this client's line accumulator, then drain
                // complete lines (terminated by \n; \r tolerated).
                StringBuilder sb;
                lock (_lock)
                {
                    if (!_accum.TryGetValue(clientIndex, out sb))
                    {
                        sb = new StringBuilder(256);
                        _accum[clientIndex] = sb;
                    }
                }

                string chunk = Encoding.UTF8.GetString(buf, 0, bytesReceived);
                sb.Append(chunk);

                while (true)
                {
                    string line;
                    if (!TryExtractLine(sb, out line)) break;
                    if (line.Length == 0) continue;
                    HandleCommand(server, clientIndex, line);
                }

                // Queue the next read.
                server.ReceiveDataAsync(clientIndex, OnReceiveData);
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[tcp] OnReceiveData(client={0}): {1}", clientIndex, e.Message);
                DropClient(server, clientIndex, "rx exception");
            }
        }

        // Pulls the first \n-terminated line out of `sb`, trimming a trailing
        // \r. Returns false if no full line is available yet.
        private static bool TryExtractLine(StringBuilder sb, out string line)
        {
            line = null;
            for (int i = 0; i < sb.Length; i++)
            {
                if (sb[i] == '\n')
                {
                    int len = i;
                    if (len > 0 && sb[len - 1] == '\r') len--;
                    line = sb.ToString(0, len);
                    sb.Remove(0, i + 1);
                    return true;
                }
            }
            return false;
        }

        // ---- command dispatch ----

        private void HandleCommand(TCPServer server, uint clientIndex, string line)
        {
            JObject obj;
            try
            {
                obj = JObject.Parse(line);
            }
            catch (Exception e)
            {
                SendOne(server, clientIndex, BuildError("bad json: " + e.Message));
                return;
            }

            string cmd = (string)obj["cmd"];
            if (string.IsNullOrEmpty(cmd))
            {
                SendOne(server, clientIndex, BuildError("missing 'cmd'"));
                return;
            }
            cmd = cmd.ToLower();

            try
            {
                switch (cmd)
                {
                    case "hello":
                        SendHello(clientIndex);
                        break;
                    case "state":
                        SendOne(server, clientIndex, BuildStateEvent());
                        break;
                    case "ping":
                        SendOne(server, clientIndex, BuildJson(new
                        {
                            @event = "pong",
                            at_utc = DateTime.UtcNow.ToString("o")
                        }));
                        break;
                    case "led":
                    {
                        string name = (string)obj["name"];
                        bool on = ReadBool(obj["on"]);
                        if (string.IsNullOrEmpty(name)) { SendOne(server, clientIndex, BuildError("led: missing 'name'")); break; }
                        _state.SetLed(name, on);
                        break;
                    }
                    case "vol":
                    case "volume":
                    {
                        var lvl = obj["level"];
                        if (lvl == null) { SendOne(server, clientIndex, BuildError("vol: missing 'level'")); break; }
                        int level = (int)lvl;
                        _state.SetVolumePercent(level);
                        break;
                    }
                    case "mute":
                    {
                        bool on = ReadBool(obj["on"]);
                        _state.SetMuted(on);
                        break;
                    }
                    case "emit":
                    {
                        // Diagnostic shim - inject a synthetic button event
                        // the same way a real panel press would. Useful while
                        // the SDK slot-registration firmware bug is in effect.
                        string name = (string)obj["name"];
                        bool pressed = ReadBool(obj["pressed"]);
                        if (string.IsNullOrEmpty(name)) { SendOne(server, clientIndex, BuildError("emit: missing 'name'")); break; }
                        _state.RecordButtonEvent(name, pressed);
                        break;
                    }
                    default:
                        SendOne(server, clientIndex, BuildError("unknown cmd '" + cmd + "'"));
                        break;
                }
            }
            catch (Exception e)
            {
                SendOne(server, clientIndex, BuildError("cmd '" + cmd + "' failed: " + e.Message));
            }
        }

        private static bool ReadBool(JToken tok)
        {
            if (tok == null) return false;
            if (tok.Type == JTokenType.Boolean) return (bool)tok;
            if (tok.Type == JTokenType.Integer) return ((int)tok) != 0;
            string s = (string)tok;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim().ToLower();
            return s == "on" || s == "1" || s == "true";
        }

        // ---- broadcast helpers ----

        private void OnLedChanged(string name, bool on)
        {
            Broadcast(BuildJson(new { @event = "led", name = name, on = on }));
        }

        private void OnVolumeChanged(int level)
        {
            Broadcast(BuildJson(new { @event = "volume", level = level }));
        }

        private void OnMuteChanged(bool muted)
        {
            Broadcast(BuildJson(new { @event = "mute", on = muted }));
        }

        private void OnButtonEvent(string name, bool pressed)
        {
            Broadcast(BuildJson(new
            {
                @event = "button",
                name = name,
                pressed = pressed,
                at_utc = DateTime.UtcNow.ToString("o")
            }));
        }

        private void SendHello(uint clientIndex)
        {
            var s = _server;
            if (s == null) return;
            var names = new List<string>();
            foreach (var n in ButtonNames.All()) names.Add(n);
            SendOne(s, clientIndex, BuildJson(new
            {
                @event = "hello",
                version = Version,
                port = _settings.Tcp.Port,
                buttons = names.ToArray()
            }));
            // Also seed the state so the client doesn't have to issue
            // {"cmd":"state"} on connect to learn current LED/volume/mute.
            SendOne(s, clientIndex, BuildStateEvent());
        }

        private string BuildStateEvent()
        {
            var snap = _state.CaptureSnapshot();
            return BuildJson(new
            {
                @event = "state",
                leds = snap.Leds,
                volume = snap.VolumePercent,
                muted = snap.Muted
            });
        }

        private static string BuildError(string message)
        {
            return BuildJson(new { @event = "error", message = message });
        }

        private static string BuildJson(object payload)
        {
            // CF 3.5 anonymous types serialize through Newtonsoft fine; the
            // `@event` field name comes out as "event" once the @-prefix is
            // stripped by the C# compiler.
            return JsonConvert.SerializeObject(payload);
        }

        // Send a single message + newline to one specific client. All sends
        // go through this so the framing is consistent.
        private void SendOne(TCPServer server, uint clientIndex, string json)
        {
            if (server == null || string.IsNullOrEmpty(json)) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                var rc = server.SendData(clientIndex, bytes, bytes.Length);
                if (rc != SocketErrorCodes.SOCKET_OK)
                {
                    ErrorLog.Notice("[tcp] send to {0} failed: {1}", clientIndex, rc);
                    DropClient(server, clientIndex, "send error");
                }
            }
            catch (Exception e)
            {
                ErrorLog.Notice("[tcp] send to {0} threw: {1}", clientIndex, e.Message);
                DropClient(server, clientIndex, "send threw");
            }
        }

        // Send to every client we currently track. The Crestron TCPServer
        // doesn't iterate client indexes for us; we keep our own map via the
        // accumulator dictionary, which is added in OnClientConnected and
        // removed in DropClient.
        private void Broadcast(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            var server = _server;
            if (server == null) return;

            uint[] ids;
            lock (_lock)
            {
                ids = new uint[_accum.Count];
                int i = 0;
                foreach (var kv in _accum) ids[i++] = kv.Key;
            }
            for (int i = 0; i < ids.Length; i++)
            {
                SendOne(server, ids[i], json);
            }
        }

        private void DropClient(TCPServer server, uint clientIndex, string reason)
        {
            lock (_lock) { _accum.Remove(clientIndex); }
            try { server.Disconnect(clientIndex); } catch { }
            try { ErrorLog.Notice("[tcp] dropped client {0} ({1})", clientIndex, reason); } catch { }
        }

        private static string SafeClientAddress(TCPServer server, uint clientIndex)
        {
            try { return server.GetAddressServerAcceptedConnectionFromForSpecificClient(clientIndex); }
            catch { return "?"; }
        }

        public void Dispose()
        {
            _disposing = true;
            try { _state.LedChanged    -= OnLedChanged; }    catch { }
            try { _state.VolumeChanged -= OnVolumeChanged; } catch { }
            try { _state.MuteChanged   -= OnMuteChanged; }   catch { }
            try { _state.ButtonEvent   -= OnButtonEvent; }   catch { }

            var s = _server;
            _server = null;
            if (s != null)
            {
                try { s.Stop(); } catch { }
                try { s.Dispose(); } catch { }
            }
            lock (_lock) { _accum.Clear(); }
        }
    }
}
