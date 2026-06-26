using System;
using System.Collections.Generic;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;
using Crestron.SimplSharpPro.CrestronThread;

namespace Mpc3TcpBridge.Mqtt
{
    // Minimal MQTT 3.1.1 client targeted at .NET 3.5 / Mono / Crestron CF.
    // Ported verbatim from the sibling mpc3-ha-bridge, whose hand-rolled client
    // is proven on this exact MPC3-302 hardware/firmware. (Same role as the
    // fasteddy516/SimplMQTT SimplSharp client, but dependency-free and CF-safe.)
    //
    // Implements only what this bridge needs:
    //   - CONNECT with optional credentials + last-will
    //   - PUBLISH at QoS 0 (optionally retained)
    //   - SUBSCRIBE at QoS 0
    //   - PINGREQ on a keepalive ticker
    //   - Auto-reconnect with backoff
    //
    // Not implemented: QoS 1/2, MQTT 5 properties, TLS. If you need TLS,
    // terminate it elsewhere (e.g. an MQTT-over-TLS reverse proxy on the broker
    // host) or swap this class for M2Mqtt with the SimpleSharp build.
    public sealed class MqttClient : IDisposable
    {
        public delegate void MessageHandler(string topic, byte[] payload);

        public event Action<bool> Connected; // true on session established, false on disconnect
        public event MessageHandler MessageReceived;

        private readonly string _host;
        private readonly int _port;
        private readonly string _clientId;
        private readonly string _username;
        private readonly string _password;
        private readonly int _keepAliveSeconds;
        private readonly string _willTopic;
        private readonly byte[] _willPayload;
        private readonly bool _willRetain;

        private TCPClient _tcp;
        private Thread _rxThread;
        private CTimer _keepAlive;
        private CTimer _reconnect;
        private readonly object _writeLock = new object();
        private readonly object _stateLock = new object();
        private bool _isConnected;
        private bool _disposing;
        private ushort _nextPacketId = 1;

        // Pending subscriptions awaiting SUBACK; key is packet id, value is
        // the list of topic filters that were subscribed in that packet (we
        // only ever send one filter per packet for simplicity).
        private readonly Dictionary<ushort, string> _pendingSubs = new Dictionary<ushort, string>();
        // Subscriptions we want active. Replayed on reconnect.
        private readonly List<string> _subscriptions = new List<string>();

        public MqttClient(string host, int port, string clientId, string username, string password,
                          int keepAliveSeconds, string willTopic, string willPayload, bool willRetain)
        {
            _host = host;
            _port = port <= 0 ? 1883 : port;
            _clientId = string.IsNullOrEmpty(clientId) ? ("mpc3-" + Environment.TickCount) : clientId;
            _username = username ?? string.Empty;
            _password = password ?? string.Empty;
            _keepAliveSeconds = keepAliveSeconds <= 0 ? 30 : keepAliveSeconds;
            _willTopic = willTopic;
            _willPayload = string.IsNullOrEmpty(willPayload) ? null : Encoding.UTF8.GetBytes(willPayload);
            _willRetain = willRetain;
        }

        public bool IsConnected
        {
            get { lock (_stateLock) { return _isConnected; } }
        }

        public void Start()
        {
            ScheduleReconnect(500);
        }

        public bool Publish(string topic, byte[] payload, bool retain)
        {
            if (!IsConnected) return false;
            if (payload == null) payload = new byte[0];
            try
            {
                var topicBytes = Encoding.UTF8.GetBytes(topic);
                var remaining = 2 + topicBytes.Length + payload.Length; // QoS 0 - no packet id
                var packet = BuildHeader(0x30 | (retain ? 0x01 : 0x00), remaining);
                var buf = new byte[packet.Length + remaining];
                Buffer.BlockCopy(packet, 0, buf, 0, packet.Length);
                int o = packet.Length;
                buf[o++] = (byte)(topicBytes.Length >> 8);
                buf[o++] = (byte)(topicBytes.Length & 0xFF);
                Buffer.BlockCopy(topicBytes, 0, buf, o, topicBytes.Length); o += topicBytes.Length;
                Buffer.BlockCopy(payload, 0, buf, o, payload.Length);
                return SendBytes(buf);
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mqtt] publish {0}: {1}", topic, e.Message);
                return false;
            }
        }

        public bool PublishString(string topic, string payload, bool retain)
        {
            return Publish(topic, payload == null ? new byte[0] : Encoding.UTF8.GetBytes(payload), retain);
        }

        public void Subscribe(string topicFilter)
        {
            lock (_subscriptions)
            {
                if (!_subscriptions.Contains(topicFilter))
                    _subscriptions.Add(topicFilter);
            }
            if (IsConnected) SendSubscribe(topicFilter);
        }

        private void SendSubscribe(string topicFilter)
        {
            try
            {
                ushort pid = NextPacketId();
                lock (_pendingSubs) _pendingSubs[pid] = topicFilter;

                var topicBytes = Encoding.UTF8.GetBytes(topicFilter);
                int remaining = 2 + 2 + topicBytes.Length + 1; // packet id + topic len + topic + qos byte
                var header = BuildHeader(0x82, remaining);     // SUBSCRIBE w/ reserved flags = 0010
                var buf = new byte[header.Length + remaining];
                Buffer.BlockCopy(header, 0, buf, 0, header.Length);
                int o = header.Length;
                buf[o++] = (byte)(pid >> 8);
                buf[o++] = (byte)(pid & 0xFF);
                buf[o++] = (byte)(topicBytes.Length >> 8);
                buf[o++] = (byte)(topicBytes.Length & 0xFF);
                Buffer.BlockCopy(topicBytes, 0, buf, o, topicBytes.Length); o += topicBytes.Length;
                buf[o++] = 0x00; // requested QoS 0
                SendBytes(buf);
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mqtt] subscribe {0}: {1}", topicFilter, e.Message);
            }
        }

        private ushort NextPacketId()
        {
            lock (_writeLock)
            {
                var id = _nextPacketId++;
                if (_nextPacketId == 0) _nextPacketId = 1;
                return id;
            }
        }

        private bool SendBytes(byte[] data)
        {
            lock (_writeLock)
            {
                if (_tcp == null) return false;
                try
                {
                    var s = _tcp.SendData(data, data.Length);
                    if (s != SocketErrorCodes.SOCKET_OK)
                    {
                        ErrorLog.Warn("[mqtt] send failed: {0}", s);
                        OnLinkDropped();
                        return false;
                    }
                    return true;
                }
                catch (Exception e)
                {
                    ErrorLog.Warn("[mqtt] send threw: {0}", e.Message);
                    OnLinkDropped();
                    return false;
                }
            }
        }

        private static byte[] BuildHeader(int controlByte, int remainingLength)
        {
            // MQTT remaining-length is a variable-byte encoding: 1..4 bytes.
            var lenBytes = new List<byte>(4);
            int v = remainingLength;
            do
            {
                int digit = v % 128;
                v = v / 128;
                if (v > 0) digit |= 0x80;
                lenBytes.Add((byte)digit);
            } while (v > 0);

            var hdr = new byte[1 + lenBytes.Count];
            hdr[0] = (byte)controlByte;
            for (int i = 0; i < lenBytes.Count; i++) hdr[1 + i] = lenBytes[i];
            return hdr;
        }

        // ---- connection lifecycle ----

        private void ScheduleReconnect(int delayMs)
        {
            if (_disposing) return;
            try
            {
                if (_reconnect != null) _reconnect.Dispose();
            }
            catch { }
            _reconnect = new CTimer(_ => TryConnect(), null, delayMs);
        }

        private void TryConnect()
        {
            if (_disposing) return;
            try
            {
                CleanupSocket();
                if (string.IsNullOrEmpty(_host))
                {
                    ErrorLog.Notice("[mqtt] no broker host set - skipping connect");
                    ScheduleReconnect(30000);
                    return;
                }
                _tcp = new TCPClient(_host, _port, 8192);
                var s = _tcp.ConnectToServer();
                if (s != SocketErrorCodes.SOCKET_OK)
                {
                    ErrorLog.Warn("[mqtt] tcp connect {0}:{1} failed: {2}", _host, _port, s);
                    ScheduleReconnect(5000);
                    return;
                }

                if (!SendConnect())
                {
                    ScheduleReconnect(5000);
                    return;
                }
                // Wait for CONNACK synchronously - 5s is generous.
                if (!ReadConnack(5000))
                {
                    ErrorLog.Warn("[mqtt] no/bad CONNACK from {0}", _host);
                    ScheduleReconnect(5000);
                    return;
                }

                SetConnected(true);
                ErrorLog.Notice("[mqtt] connected to {0}:{1} as {2}", _host, _port, _clientId);

                // Replay subscriptions and start the read/keepalive loops.
                lock (_subscriptions)
                {
                    foreach (var f in _subscriptions) SendSubscribe(f);
                }

                _rxThread = new Thread(RxLoop, null);

                if (_keepAlive != null) { try { _keepAlive.Dispose(); } catch { } }
                int periodMs = (_keepAliveSeconds * 1000) / 2;
                if (periodMs < 2000) periodMs = 2000;
                _keepAlive = new CTimer(_ => SendPing(), null, periodMs, periodMs);
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mqtt] TryConnect: {0}", e.Message);
                ScheduleReconnect(5000);
            }
        }

        private bool SendConnect()
        {
            var clientBytes = Encoding.UTF8.GetBytes(_clientId);
            byte connectFlags = 0x02; // clean session
            byte[] willTopicBytes = null, willPayloadBytes = null;
            byte[] userBytes = null, passBytes = null;

            if (!string.IsNullOrEmpty(_willTopic) && _willPayload != null)
            {
                connectFlags |= 0x04;                       // will flag
                if (_willRetain) connectFlags |= 0x20;       // will retain
                willTopicBytes = Encoding.UTF8.GetBytes(_willTopic);
                willPayloadBytes = _willPayload;
            }
            if (!string.IsNullOrEmpty(_username))
            {
                connectFlags |= 0x80;
                userBytes = Encoding.UTF8.GetBytes(_username);
                if (!string.IsNullOrEmpty(_password))
                {
                    connectFlags |= 0x40;
                    passBytes = Encoding.UTF8.GetBytes(_password);
                }
            }

            // Variable header: protocol name + level + flags + keepalive = 10 bytes
            // Payload: client id (+ will topic + will payload + user + pass)
            int remaining = 10 + 2 + clientBytes.Length;
            if (willTopicBytes != null) remaining += 2 + willTopicBytes.Length + 2 + willPayloadBytes.Length;
            if (userBytes != null) remaining += 2 + userBytes.Length;
            if (passBytes != null) remaining += 2 + passBytes.Length;

            var header = BuildHeader(0x10, remaining);
            var buf = new byte[header.Length + remaining];
            int o = 0;
            Buffer.BlockCopy(header, 0, buf, o, header.Length); o += header.Length;
            // Protocol name "MQTT"
            buf[o++] = 0x00; buf[o++] = 0x04;
            buf[o++] = (byte)'M'; buf[o++] = (byte)'Q'; buf[o++] = (byte)'T'; buf[o++] = (byte)'T';
            buf[o++] = 0x04; // protocol level 4 (MQTT 3.1.1)
            buf[o++] = connectFlags;
            buf[o++] = (byte)(_keepAliveSeconds >> 8);
            buf[o++] = (byte)(_keepAliveSeconds & 0xFF);
            o = AppendString(buf, o, clientBytes);
            if (willTopicBytes != null)
            {
                o = AppendString(buf, o, willTopicBytes);
                o = AppendString(buf, o, willPayloadBytes);
            }
            if (userBytes != null) o = AppendString(buf, o, userBytes);
            if (passBytes != null) o = AppendString(buf, o, passBytes);
            return SendBytes(buf);
        }

        private static int AppendString(byte[] buf, int offset, byte[] s)
        {
            buf[offset++] = (byte)(s.Length >> 8);
            buf[offset++] = (byte)(s.Length & 0xFF);
            Buffer.BlockCopy(s, 0, buf, offset, s.Length);
            return offset + s.Length;
        }

        private bool ReadConnack(int timeoutMs)
        {
            int elapsed = 0; const int step = 50;
            while (elapsed < timeoutMs)
            {
                if (_tcp.DataAvailable) break;
                CrestronEnvironment.Sleep(step); elapsed += step;
            }
            if (!_tcp.DataAvailable) return false;
            int n = _tcp.ReceiveData();
            if (n < 4) return false;
            var buf = _tcp.IncomingDataBuffer;
            // Expected: 0x20 0x02 <flags> <rc>. RC=0 means accepted.
            if (buf[0] != 0x20) return false;
            return buf[3] == 0x00;
        }

        private void SendPing()
        {
            if (!IsConnected) return;
            // PINGREQ = 0xC0 0x00
            SendBytes(new byte[] { 0xC0, 0x00 });
        }

        // ---- receive loop & packet decode ----

        private object RxLoop(object _)
        {
            // Crestron CrestronThread API uses an object->object delegate;
            // we ignore the inbound state and just return null at the end.
            var buf = new List<byte>(1024);
            try
            {
                while (!_disposing && IsConnected && _tcp != null)
                {
                    int n;
                    try { n = _tcp.ReceiveData(); }
                    catch { n = 0; }
                    if (n <= 0)
                    {
                        OnLinkDropped();
                        break;
                    }
                    var data = _tcp.IncomingDataBuffer;
                    for (int i = 0; i < n; i++) buf.Add(data[i]);
                    // Drain as many full packets as possible from the buffer.
                    while (TryDrainPacket(buf)) { /* keep going */ }
                }
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mqtt] rx loop: {0}", e.Message);
                OnLinkDropped();
            }
            return null;
        }

        private bool TryDrainPacket(List<byte> buf)
        {
            if (buf.Count < 2) return false;
            byte ctl = buf[0];

            // Decode remaining-length variable-byte int starting at buf[1].
            int remaining = 0, multiplier = 1, idx = 1;
            while (true)
            {
                if (idx >= buf.Count) return false; // need more bytes
                byte d = buf[idx++];
                remaining += (d & 0x7F) * multiplier;
                if ((d & 0x80) == 0) break;
                multiplier *= 128;
                if (multiplier > 128 * 128 * 128)
                {
                    // Malformed - reset stream.
                    ErrorLog.Warn("[mqtt] malformed remaining-length, resetting");
                    buf.Clear();
                    return false;
                }
            }
            if (buf.Count - idx < remaining) return false; // need more bytes

            int type = (ctl >> 4) & 0x0F;
            var payload = new byte[remaining];
            buf.CopyTo(idx, payload, 0, remaining);
            // Drop the consumed bytes.
            buf.RemoveRange(0, idx + remaining);

            switch (type)
            {
                case 3: HandlePublish(ctl, payload); break;
                case 9: HandleSuback(payload); break;
                case 13: /* PINGRESP */ break;
                default:
                    // CONNACK handled separately during ReadConnack; PUBACK
                    // etc not used at QoS 0. Anything else is unexpected.
                    break;
            }
            return true;
        }

        private void HandlePublish(byte controlByte, byte[] vbody)
        {
            try
            {
                int qos = (controlByte >> 1) & 0x03;
                int i = 0;
                int topicLen = (vbody[i++] << 8) | vbody[i++];
                var topic = Encoding.UTF8.GetString(vbody, i, topicLen);
                i += topicLen;
                if (qos > 0)
                {
                    // Skip packet identifier - we don't ack at QoS 1/2.
                    i += 2;
                }
                int payloadLen = vbody.Length - i;
                var payload = new byte[payloadLen];
                if (payloadLen > 0) Buffer.BlockCopy(vbody, i, payload, 0, payloadLen);

                var h = MessageReceived;
                if (h != null)
                {
                    try { h(topic, payload); }
                    catch (Exception e) { ErrorLog.Warn("[mqtt] subscriber threw on {0}: {1}", topic, e.Message); }
                }
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mqtt] decode publish: {0}", e.Message);
            }
        }

        private void HandleSuback(byte[] vbody)
        {
            if (vbody.Length < 3) return;
            ushort pid = (ushort)((vbody[0] << 8) | vbody[1]);
            byte rc = vbody[2];
            lock (_pendingSubs)
            {
                string topic;
                if (_pendingSubs.TryGetValue(pid, out topic))
                {
                    _pendingSubs.Remove(pid);
                    if (rc >= 0x80)
                        ErrorLog.Warn("[mqtt] broker rejected subscribe to {0} (rc=0x{1:X2})", topic, rc);
                }
            }
        }

        // ---- helpers ----

        private void SetConnected(bool value)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = (_isConnected != value);
                _isConnected = value;
            }
            if (changed)
            {
                var h = Connected;
                if (h != null) try { h(value); } catch { }
            }
        }

        private void OnLinkDropped()
        {
            if (!IsConnected) return;
            SetConnected(false);
            ErrorLog.Notice("[mqtt] link dropped, will reconnect");
            try { if (_keepAlive != null) _keepAlive.Dispose(); } catch { }
            CleanupSocket();
            ScheduleReconnect(3000);
        }

        private void CleanupSocket()
        {
            // Hold _writeLock so a concurrent Publish/Subscribe doesn't try to
            // SendData on a disposed socket. SendBytes also takes this lock,
            // so they serialize.
            lock (_writeLock)
            {
                try
                {
                    if (_tcp != null)
                    {
                        try { _tcp.DisconnectFromServer(); } catch { }
                        try { _tcp.Dispose(); } catch { }
                    }
                }
                finally { _tcp = null; }
            }
        }

        public void Dispose()
        {
            _disposing = true;
            try { if (_keepAlive != null) _keepAlive.Dispose(); } catch { }
            try { if (_reconnect != null) _reconnect.Dispose(); } catch { }
            SetConnected(false);
            CleanupSocket();
        }
    }
}
