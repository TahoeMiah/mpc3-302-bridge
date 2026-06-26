using System;
using System.Text;
using Crestron.SimplSharp;
using Mpc3TcpBridge.Config;
using Mpc3TcpBridge.State;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mpc3TcpBridge.Mqtt
{
    // Owns the MqttClient and bridges the panel's DeviceState onto MQTT.
    //
    // Topic layout (under <BaseTopic>/<DeviceId>/):
    //   status                            availability ("online" | "offline", LWT)
    //   led/<name>/state                  retained LED state ON/OFF
    //   led/<name>/set                    subscribe - command an LED on/off
    //   volume/state                      0..100 percent (retained)
    //   volume/set                        subscribe - set volume
    //   mute/state                        ON/OFF (retained)
    //   mute/set                          subscribe - toggle mute
    //   button/<name>/event               "pressed" / "released" (not retained)
    //
    // If Mqtt.HaDiscovery is true, also publishes Home Assistant MQTT discovery
    // configs (switches for each LED + mute, a number for volume, and 24 device-
    // automation triggers) under <DiscoveryPrefix>/ so the panel appears in HA
    // with no manual YAML. Set it false for a plain generic-MQTT bridge.
    public sealed class MqttBridge : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly DeviceState _state;
        private readonly MqttClient _mqtt;
        private readonly string _baseTopic;          // <base>/<deviceId>
        private readonly string _availabilityTopic;
        private readonly string _discoveryPrefix;
        private readonly string _deviceId;
        private readonly bool _haDiscovery;
        private bool _wired;

        public MqttBridge(AppSettings settings, DeviceState state)
        {
            _settings = settings;
            _state = state;
            _deviceId = string.IsNullOrEmpty(settings.DeviceId) ? "mpc3-302" : settings.DeviceId;
            _discoveryPrefix = string.IsNullOrEmpty(settings.Mqtt.DiscoveryPrefix)
                ? "homeassistant" : settings.Mqtt.DiscoveryPrefix;
            _baseTopic = TrimSlash(settings.Mqtt.BaseTopic) + "/" + _deviceId;
            _availabilityTopic = _baseTopic + "/status";
            _haDiscovery = settings.Mqtt.HaDiscovery;

            _mqtt = new MqttClient(
                settings.Mqtt.Host,
                settings.Mqtt.Port,
                _deviceId,
                settings.Mqtt.Username,
                settings.Mqtt.Password,
                settings.Mqtt.KeepAliveSeconds,
                _availabilityTopic,
                "offline",
                true);
            _mqtt.Connected += OnMqttConnected;
            _mqtt.MessageReceived += OnMqttMessage;
        }

        public bool IsConnected { get { return _mqtt != null && _mqtt.IsConnected; } }

        public void Start()
        {
            if (_settings.Mqtt == null || !_settings.Mqtt.Enabled)
            {
                ErrorLog.Notice("[mqtt] disabled in settings - skipping");
                return;
            }
            if (string.IsNullOrEmpty(_settings.Mqtt.Host))
            {
                ErrorLog.Notice("[mqtt] host empty - set it in the web config (/config) then restart");
                return;
            }
            _state.LedChanged    += OnLocalLedChanged;
            _state.VolumeChanged += OnLocalVolumeChanged;
            _state.MuteChanged   += OnLocalMuteChanged;
            _state.ButtonEvent   += OnLocalButtonEvent;
            _wired = true;
            _mqtt.Start();
            ErrorLog.Notice("[mqtt] starting -> {0}:{1} base={2} haDiscovery={3}",
                _settings.Mqtt.Host, _settings.Mqtt.Port, _baseTopic, _haDiscovery);
        }

        private void OnMqttConnected(bool up)
        {
            if (!up) return;
            try
            {
                // Subscribe to all command topics.
                _mqtt.Subscribe(_baseTopic + "/volume/set");
                _mqtt.Subscribe(_baseTopic + "/mute/set");
                foreach (var name in ButtonNames.All())
                    _mqtt.Subscribe(_baseTopic + "/led/" + name + "/set");

                // Mark online (retained) and optionally publish HA discovery.
                _mqtt.PublishString(_availabilityTopic, "online", true);
                if (_haDiscovery) PublishDiscovery();

                // Replay current state so consumers see the truth immediately.
                PublishAllState();
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mqtt] on-connect: {0}", e.Message);
            }
        }

        private void OnMqttMessage(string topic, byte[] payload)
        {
            try
            {
                if (!topic.StartsWith(_baseTopic + "/")) return;
                var suffix = topic.Substring(_baseTopic.Length + 1);
                var body = Encoding.UTF8.GetString(payload, 0, payload.Length).Trim();

                if (suffix == "volume/set")
                {
                    int level;
                    if (Parse.TryInt(body, out level))
                        _state.SetVolumePercent(level);
                }
                else if (suffix == "mute/set")
                {
                    _state.SetMuted(IsOn(body));
                }
                else if (suffix.StartsWith("led/") && suffix.EndsWith("/set"))
                {
                    var name = suffix.Substring(4, suffix.Length - 4 - 4);
                    _state.SetLed(name, IsOn(body));
                }
                else
                {
                    ErrorLog.Notice("[mqtt] ignoring unknown topic {0}", topic);
                }
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[mqtt] on-message {0}: {1}", topic, e.Message);
            }
        }

        // ---- outbound state ----

        private void OnLocalLedChanged(string name, bool on)
        {
            _mqtt.PublishString(_baseTopic + "/led/" + name + "/state", on ? "ON" : "OFF", true);
        }

        private void OnLocalVolumeChanged(int percent)
        {
            _mqtt.PublishString(_baseTopic + "/volume/state", percent.ToString(), true);
        }

        private void OnLocalMuteChanged(bool muted)
        {
            _mqtt.PublishString(_baseTopic + "/mute/state", muted ? "ON" : "OFF", true);
        }

        private void OnLocalButtonEvent(string name, bool pressed)
        {
            // Not retained - one-shot events.
            _mqtt.PublishString(
                _baseTopic + "/button/" + name + "/event",
                pressed ? "pressed" : "released",
                false);
        }

        private void PublishAllState()
        {
            var snap = _state.CaptureSnapshot();
            foreach (var kv in snap.Leds)
                _mqtt.PublishString(_baseTopic + "/led/" + kv.Key + "/state", kv.Value ? "ON" : "OFF", true);
            _mqtt.PublishString(_baseTopic + "/volume/state", snap.VolumePercent.ToString(), true);
            _mqtt.PublishString(_baseTopic + "/mute/state", snap.Muted ? "ON" : "OFF", true);
        }

        // ---- HA discovery (optional) ----

        private void PublishDiscovery()
        {
            var device = BuildDeviceBlock();
            foreach (var name in ButtonNames.All())
                PublishLedSwitchDiscovery(name, device);
            PublishVolumeDiscovery(device);
            PublishMuteDiscovery(device);
            foreach (var name in ButtonNames.All())
            {
                PublishButtonTriggerDiscovery(name, "pressed",  device);
                PublishButtonTriggerDiscovery(name, "released", device);
            }
        }

        private JObject BuildDeviceBlock()
        {
            return new JObject(
                new JProperty("identifiers", new JArray(_deviceId)),
                new JProperty("name", _settings.FriendlyName),
                new JProperty("manufacturer", "Crestron"),
                new JProperty("model", "MPC3-302"),
                new JProperty("sw_version", "Mpc3TcpBridge"));
        }

        private void PublishLedSwitchDiscovery(string name, JObject device)
        {
            string label;
            if (name == ButtonNames.Power)     label = "Power LED";
            else if (name == ButtonNames.Mute) label = "Mute LED";
            else
            {
                var n = name.StartsWith("btn") ? name.Substring(3).TrimStart('0') : name;
                label = "Button " + n + " LED";
            }

            var cfg = new JObject(
                new JProperty("name", label),
                new JProperty("unique_id", _deviceId + "_led_" + name),
                new JProperty("object_id", _deviceId + "_led_" + name),
                new JProperty("state_topic",   _baseTopic + "/led/" + name + "/state"),
                new JProperty("command_topic", _baseTopic + "/led/" + name + "/set"),
                new JProperty("payload_on",  "ON"),
                new JProperty("payload_off", "OFF"),
                new JProperty("optimistic", false),
                new JProperty("availability_topic", _availabilityTopic),
                new JProperty("device", device));
            var topic = _discoveryPrefix + "/switch/" + _deviceId + "/led_" + name + "/config";
            _mqtt.PublishString(topic, cfg.ToString(Formatting.None), true);
        }

        private void PublishVolumeDiscovery(JObject device)
        {
            var cfg = new JObject(
                new JProperty("name", "Volume"),
                new JProperty("unique_id", _deviceId + "_volume"),
                new JProperty("object_id", _deviceId + "_volume"),
                new JProperty("state_topic",   _baseTopic + "/volume/state"),
                new JProperty("command_topic", _baseTopic + "/volume/set"),
                new JProperty("min", 0),
                new JProperty("max", 100),
                new JProperty("step", 1),
                new JProperty("unit_of_measurement", "%"),
                new JProperty("mode", "slider"),
                new JProperty("availability_topic", _availabilityTopic),
                new JProperty("device", device));
            var topic = _discoveryPrefix + "/number/" + _deviceId + "/volume/config";
            _mqtt.PublishString(topic, cfg.ToString(Formatting.None), true);
        }

        private void PublishMuteDiscovery(JObject device)
        {
            var cfg = new JObject(
                new JProperty("name", "Mute"),
                new JProperty("unique_id", _deviceId + "_mute"),
                new JProperty("object_id", _deviceId + "_mute"),
                new JProperty("state_topic",   _baseTopic + "/mute/state"),
                new JProperty("command_topic", _baseTopic + "/mute/set"),
                new JProperty("payload_on",  "ON"),
                new JProperty("payload_off", "OFF"),
                new JProperty("availability_topic", _availabilityTopic),
                new JProperty("device", device));
            var topic = _discoveryPrefix + "/switch/" + _deviceId + "/mute/config";
            _mqtt.PublishString(topic, cfg.ToString(Formatting.None), true);
        }

        private void PublishButtonTriggerDiscovery(string name, string edge, JObject device)
        {
            var cfg = new JObject(
                new JProperty("automation_type", "trigger"),
                new JProperty("topic", _baseTopic + "/button/" + name + "/event"),
                new JProperty("type", "button_short_" + (edge == "pressed" ? "press" : "release")),
                new JProperty("subtype", name),
                new JProperty("payload", edge),
                new JProperty("device", device));
            var topic = _discoveryPrefix + "/device_automation/" + _deviceId + "/" + name + "_" + edge + "/config";
            _mqtt.PublishString(topic, cfg.ToString(Formatting.None), true);
        }

        // ---- utils ----

        private static bool IsOn(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            return string.Equals(s, "ON", StringComparison.OrdinalIgnoreCase)
                || s == "1"
                || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string TrimSlash(string s)
        {
            if (string.IsNullOrEmpty(s)) return "mpc3";
            return s.TrimEnd('/');
        }

        public void Dispose()
        {
            try
            {
                if (_mqtt != null)
                {
                    _mqtt.PublishString(_availabilityTopic, "offline", true);
                    _mqtt.Dispose();
                }
            }
            catch { }
            if (_wired)
            {
                _state.LedChanged    -= OnLocalLedChanged;
                _state.VolumeChanged -= OnLocalVolumeChanged;
                _state.MuteChanged   -= OnLocalMuteChanged;
                _state.ButtonEvent   -= OnLocalButtonEvent;
            }
        }
    }
}
