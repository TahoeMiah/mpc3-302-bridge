using System;
using System.Collections.Generic;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;
using Mpc3TcpBridge.State;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mpc3TcpBridge.Web
{
    // Minimal HTTP/1.1 server for the panel web UI. Serves a single embedded
    // page at GET /, a JSON snapshot at GET /api/state, an SSE event stream
    // at GET /api/events, and accepts JSON commands at POST /api/cmd (same
    // wire vocabulary as TcpServer's line-oriented format, but framed by an
    // HTTP request instead of \n).
    //
    // SSE clients stay connected and receive every state change as
    // `data: {...}\n\n` lines until they hang up. All other requests are
    // one-shot: respond and close.
    //
    // Built on Crestron's TCPServer the same way TcpServer.cs is. Caveats
    // baked into the parser:
    //   - per-client byte accumulator (request may arrive in multiple chunks)
    //   - end-of-headers = first \r\n\r\n
    //   - POST body length = Content-Length header (no chunked support)
    //   - all responses include Connection: close (except SSE, which stays
    //     open until the client side hangs up)
    public sealed class WebServer : IDisposable
    {
        private const int DefaultPort = 8080;
        private const int DefaultMaxClients = 12;
        private const int BufferBytes = 4096;
        private const int MaxRequestBytes = 65536;

        private readonly DeviceState _state;
        private readonly int _port;
        private readonly string _bind;
        private TCPServer _server;
        private readonly object _lock = new object();
        // clientIndex -> per-client request accumulator + parse state.
        private readonly Dictionary<uint, Conn> _conns = new Dictionary<uint, Conn>();
        // Subset of _conns whose connections have been upgraded to SSE.
        private readonly Dictionary<uint, bool> _sse = new Dictionary<uint, bool>();
        private bool _disposing;

        public WebServer(DeviceState state, int port, string bindAddress)
        {
            _state = state;
            _port = port > 0 ? port : DefaultPort;
            _bind = string.IsNullOrEmpty(bindAddress) ? "0.0.0.0" : bindAddress;
        }

        public int Port { get { return _port; } }

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

        public int SseClientCount
        {
            get { lock (_lock) { return _sse.Count; } }
        }

        public void Start()
        {
            try
            {
                _server = new TCPServer(_bind, _port, BufferBytes,
                    EthernetAdapterType.EthernetUnknownAdapter, DefaultMaxClients);
                _server.SocketStatusChange += OnSocketStatus;

                var rc = _server.WaitForConnectionAsync(OnClientConnected);
                ErrorLog.Notice("[web] listening on {0}:{1} (max={2}) - waitForConn={3}",
                    _bind, _port, DefaultMaxClients, rc);

                _state.LedChanged    += OnLedChanged;
                _state.VolumeChanged += OnVolumeChanged;
                _state.MuteChanged   += OnMuteChanged;
                _state.ButtonEvent   += OnButtonEvent;
            }
            catch (Exception e)
            {
                ErrorLog.Error("[web] Start failed: {0}", e.Message);
            }
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
            if (s != null) { try { s.Stop(); } catch { } }
            lock (_lock) { _conns.Clear(); _sse.Clear(); }
        }

        // ---- Crestron callbacks ----

        private void OnSocketStatus(TCPServer server, uint clientIndex, SocketStatus status)
        {
            // We rely on OnReceiveData(bytesReceived<=0) to detect disconnect;
            // this hook is just for visibility while debugging.
        }

        private void OnClientConnected(TCPServer server, uint clientIndex)
        {
            if (_disposing) return;
            try
            {
                if (clientIndex == 0)
                {
                    server.WaitForConnectionAsync(OnClientConnected);
                    return;
                }
                lock (_lock) { _conns[clientIndex] = new Conn(); }
                server.ReceiveDataAsync(clientIndex, OnReceiveData);
                server.WaitForConnectionAsync(OnClientConnected);
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[web] OnClientConnected: {0}", e.Message);
                try { server.WaitForConnectionAsync(OnClientConnected); } catch { }
            }
        }

        private void OnReceiveData(TCPServer server, uint clientIndex, int bytesReceived)
        {
            if (_disposing) return;
            try
            {
                if (bytesReceived <= 0) { DropClient(clientIndex, "remote closed"); return; }

                Conn conn;
                lock (_lock) { _conns.TryGetValue(clientIndex, out conn); }
                if (conn == null)
                {
                    try { server.Disconnect(clientIndex); } catch { }
                    return;
                }

                // SSE is server-to-client only after upgrade; ignore any
                // garbage the browser sends except for a clean disconnect.
                if (conn.IsSse)
                {
                    server.ReceiveDataAsync(clientIndex, OnReceiveData);
                    return;
                }

                var buf = server.GetIncomingDataBufferForSpecificClient(clientIndex);
                if (buf == null || buf.Length == 0) { DropClient(clientIndex, "null buffer"); return; }

                if (conn.Buffer.Length + bytesReceived > MaxRequestBytes)
                {
                    SendSimple(clientIndex, 413, "Payload Too Large", "text/plain", "request too large");
                    return;
                }
                conn.Buffer.Append(Encoding.UTF8.GetString(buf, 0, bytesReceived));

                if (!TryHandleRequest(clientIndex, conn))
                {
                    // Need more bytes; queue another read.
                    server.ReceiveDataAsync(clientIndex, OnReceiveData);
                }
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[web] OnReceiveData(client={0}): {1}", clientIndex, e.Message);
                DropClient(clientIndex, "rx exception");
            }
        }

        // Returns true once a request has been fully parsed AND a response
        // (or SSE upgrade) issued. Returns false to mean "need more bytes".
        private bool TryHandleRequest(uint clientIndex, Conn conn)
        {
            string raw = conn.Buffer.ToString();
            int headersEnd = raw.IndexOf("\r\n\r\n");
            if (headersEnd < 0) return false;

            int bodyStart = headersEnd + 4;
            string headPart = raw.Substring(0, headersEnd);
            string[] lines = headPart.Split('\n');
            if (lines.Length < 1)
            {
                SendSimple(clientIndex, 400, "Bad Request", "text/plain", "no request line");
                return true;
            }
            string requestLine = lines[0].TrimEnd('\r');
            string[] reqParts = requestLine.Split(' ');
            if (reqParts.Length < 2)
            {
                SendSimple(clientIndex, 400, "Bad Request", "text/plain", "bad request line");
                return true;
            }
            string method = reqParts[0].ToUpper();
            string path = reqParts[1];
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim().ToLower();
                string value = line.Substring(colon + 1).Trim();
                if (name == "content-length")
                {
                    int n;
                    if (Parse.TryInt(value, out n)) contentLength = n;
                }
            }

            // For POST: ensure we have the whole body buffered.
            if (method == "POST")
            {
                int have = raw.Length - bodyStart;
                if (have < contentLength) return false;
            }

            string body = "";
            if (raw.Length > bodyStart)
            {
                body = raw.Substring(bodyStart);
                if (contentLength > 0 && body.Length > contentLength)
                    body = body.Substring(0, contentLength);
            }

            try { DispatchRoute(clientIndex, conn, method, path, body); }
            catch (Exception e)
            {
                SendSimple(clientIndex, 500, "Internal Server Error", "text/plain", "error: " + e.Message);
            }
            return true;
        }

        private void DispatchRoute(uint clientIndex, Conn conn, string method, string path, string body)
        {
            if (method == "GET" && (path == "/" || path == "/index.html"))
            {
                SendSimple(clientIndex, 200, "OK", "text/html; charset=utf-8", Static.IndexHtml);
                return;
            }
            if (method == "GET" && path == "/api/state")
            {
                SendSimple(clientIndex, 200, "OK", "application/json", BuildStateEvent());
                return;
            }
            if (method == "GET" && path == "/api/events")
            {
                StartSse(clientIndex, conn);
                return;
            }
            if (method == "POST" && path == "/api/cmd")
            {
                string result = HandleCommand(body);
                SendSimple(clientIndex, 200, "OK", "application/json", result);
                return;
            }
            if (method == "OPTIONS")
            {
                // Permissive CORS for browser dev tools / external integrations.
                var sb = new StringBuilder();
                sb.Append("HTTP/1.1 204 No Content\r\n");
                sb.Append("Access-Control-Allow-Origin: *\r\n");
                sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
                sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
                sb.Append("Connection: close\r\n\r\n");
                SendRaw(clientIndex, sb.ToString());
                DropClient(clientIndex, "options");
                return;
            }
            SendSimple(clientIndex, 404, "Not Found", "text/plain", "not found: " + method + " " + path);
        }

        private void StartSse(uint clientIndex, Conn conn)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 200 OK\r\n");
            sb.Append("Content-Type: text/event-stream; charset=utf-8\r\n");
            sb.Append("Cache-Control: no-cache, no-transform\r\n");
            sb.Append("Connection: keep-alive\r\n");
            sb.Append("X-Accel-Buffering: no\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("\r\n");
            // Comment line flushes headers in clients that batch.
            sb.Append(": connected\n\n");
            SendRaw(clientIndex, sb.ToString());

            conn.IsSse = true;
            lock (_lock) { _sse[clientIndex] = true; }

            // Seed the new SSE client with the current snapshot so the UI
            // doesn't sit empty until the next change.
            SendSseEvent(clientIndex, BuildStateEvent());

            // Keep an outstanding read so we notice a client disconnect.
            var server = _server;
            if (server != null) { try { server.ReceiveDataAsync(clientIndex, OnReceiveData); } catch { } }

            ErrorLog.Notice("[web] sse client {0} attached ({1} total)", clientIndex, SseClientCount);
        }

        // ---- command dispatch (mirrors TcpServer.HandleCommand) ----

        private string HandleCommand(string body)
        {
            JObject obj;
            try { obj = JObject.Parse(body); }
            catch (Exception e) { return BuildJson(new { ok = false, error = "bad json: " + e.Message }); }

            string cmd = (string)obj["cmd"];
            if (string.IsNullOrEmpty(cmd)) return BuildJson(new { ok = false, error = "missing cmd" });
            cmd = cmd.ToLower();

            try
            {
                switch (cmd)
                {
                    case "led":
                    {
                        string name = (string)obj["name"];
                        bool on = ReadBool(obj["on"]);
                        if (string.IsNullOrEmpty(name))
                            return BuildJson(new { ok = false, error = "led: missing name" });
                        _state.SetLed(name, on);
                        return BuildJson(new { ok = true });
                    }
                    case "vol":
                    case "volume":
                    {
                        var lvl = obj["level"];
                        if (lvl == null) return BuildJson(new { ok = false, error = "vol: missing level" });
                        int level = (int)lvl;
                        _state.SetVolumePercent(level);
                        return BuildJson(new { ok = true });
                    }
                    case "mute":
                    {
                        bool on = ReadBool(obj["on"]);
                        _state.SetMuted(on);
                        return BuildJson(new { ok = true });
                    }
                    case "emit":
                    {
                        string name = (string)obj["name"];
                        bool pressed = ReadBool(obj["pressed"]);
                        if (string.IsNullOrEmpty(name))
                            return BuildJson(new { ok = false, error = "emit: missing name" });
                        _state.RecordButtonEvent(name, pressed);
                        return BuildJson(new { ok = true });
                    }
                    case "state":
                        return BuildStateEvent();
                    default:
                        return BuildJson(new { ok = false, error = "unknown cmd " + cmd });
                }
            }
            catch (Exception e)
            {
                return BuildJson(new { ok = false, error = "cmd failed: " + e.Message });
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

        // ---- state changes -> SSE broadcasts ----

        private void OnLedChanged(string name, bool on)
        {
            BroadcastSse(BuildJson(new { @event = "led", name = name, on = on }));
        }

        private void OnVolumeChanged(int level)
        {
            BroadcastSse(BuildJson(new { @event = "volume", level = level }));
        }

        private void OnMuteChanged(bool muted)
        {
            BroadcastSse(BuildJson(new { @event = "mute", on = muted }));
        }

        private void OnButtonEvent(string name, bool pressed)
        {
            BroadcastSse(BuildJson(new
            {
                @event = "button",
                name = name,
                pressed = pressed,
                at_utc = DateTime.UtcNow.ToString("o")
            }));
        }

        private void BroadcastSse(string payloadJson)
        {
            uint[] ids;
            lock (_lock)
            {
                ids = new uint[_sse.Count];
                int i = 0;
                foreach (var kv in _sse) ids[i++] = kv.Key;
            }
            for (int i = 0; i < ids.Length; i++)
            {
                SendSseEvent(ids[i], payloadJson);
            }
        }

        private void SendSseEvent(uint clientIndex, string json)
        {
            // SSE frame: `data: <line>\n\n`. Our payloads never contain
            // embedded newlines, so a single `data:` line is enough.
            SendRaw(clientIndex, "data: " + json + "\n\n");
        }

        // ---- response helpers ----

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

        private static string BuildJson(object payload)
        {
            return JsonConvert.SerializeObject(payload);
        }

        private void SendSimple(uint clientIndex, int status, string reason, string contentType, string body)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body == null ? "" : body);
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");
            byte[] head = Encoding.UTF8.GetBytes(sb.ToString());
            SendBytes(clientIndex, head);
            if (bodyBytes.Length > 0) SendBytes(clientIndex, bodyBytes);
            DropClient(clientIndex, "response sent");
        }

        private void SendRaw(uint clientIndex, string raw)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(raw);
            SendBytes(clientIndex, bytes);
        }

        private void SendBytes(uint clientIndex, byte[] bytes)
        {
            var server = _server;
            if (server == null) return;
            try
            {
                var rc = server.SendData(clientIndex, bytes, bytes.Length);
                if (rc != SocketErrorCodes.SOCKET_OK)
                {
                    DropClient(clientIndex, "send err " + rc);
                }
            }
            catch (Exception e)
            {
                ErrorLog.Notice("[web] send to {0} threw: {1}", clientIndex, e.Message);
                DropClient(clientIndex, "send threw");
            }
        }

        private void DropClient(uint clientIndex, string reason)
        {
            bool wasSse;
            lock (_lock)
            {
                _conns.Remove(clientIndex);
                wasSse = _sse.Remove(clientIndex);
            }
            if (wasSse)
            {
                try { ErrorLog.Notice("[web] sse client {0} detached ({1})", clientIndex, reason); } catch { }
            }
            var s = _server;
            if (s != null) { try { s.Disconnect(clientIndex); } catch { } }
        }

        private sealed class Conn
        {
            public readonly StringBuilder Buffer = new StringBuilder(1024);
            public bool IsSse;
        }
    }
}
