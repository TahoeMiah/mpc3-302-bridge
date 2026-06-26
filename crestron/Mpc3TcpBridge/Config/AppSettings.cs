using System;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronIO;
using Newtonsoft.Json;

namespace Mpc3TcpBridge.Config
{
    // Settings loader. Reads /user/appsettings.json if present (the standard
    // Crestron user-writable path), otherwise applies defaults so the program
    // is functional on a fresh processor with nothing pre-staged.
    //
    // To override on a deployed unit:
    //   echo '{"Tcp":{"Port":9000}}' | pscp - admin@<mpc>:/user/appsettings.json
    //   plink admin@<mpc> progres -P:01
    public sealed class AppSettings
    {
        // Identity. DeviceId is the MQTT topic component and HA unique_id base -
        // keep it stable after first install. FriendlyName is the display label.
        [JsonProperty("DeviceId")]
        public string DeviceId = "mpc3-302";

        [JsonProperty("FriendlyName")]
        public string FriendlyName = "MPC3 Controller";

        [JsonProperty("Tcp")]
        public TcpSettings Tcp = new TcpSettings();

        [JsonProperty("Web")]
        public WebSettings Web = new WebSettings();

        [JsonProperty("Mqtt")]
        public MqttSettings Mqtt = new MqttSettings();

        [JsonProperty("Volume")]
        public VolumeSettings Volume = new VolumeSettings();

        public sealed class TcpSettings
        {
            [JsonProperty("Port")]
            public int Port = 8023;

            // "0.0.0.0" or empty means bind on every adapter.
            [JsonProperty("BindAddress")]
            public string BindAddress = "0.0.0.0";

            [JsonProperty("MaxClients")]
            public int MaxClients = 8;

            [JsonProperty("BufferBytes")]
            public int BufferBytes = 4096;
        }

        public sealed class WebSettings
        {
            // Port for the HTTP panel UI (GET /, GET /api/state, GET
            // /api/events SSE, POST /api/cmd). Set to 0 to disable the
            // web server entirely.
            [JsonProperty("Port")]
            public int Port = 8080;

            [JsonProperty("BindAddress")]
            public string BindAddress = "0.0.0.0";
        }

        public sealed class MqttSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;        // off until a broker is configured

            [JsonProperty("Host")]
            public string Host = "";

            [JsonProperty("Port")]
            public int Port = 1883;

            [JsonProperty("Username")]
            public string Username = "";

            [JsonProperty("Password")]
            public string Password = "";

            // All topics for this device live under <BaseTopic>/<DeviceId>/...
            [JsonProperty("BaseTopic")]
            public string BaseTopic = "mpc3";

            // Publish Home Assistant MQTT discovery configs. Set false for a
            // plain generic-MQTT bridge with no HA-specific topics.
            [JsonProperty("HaDiscovery")]
            public bool HaDiscovery = true;

            // HA's MQTT discovery prefix - only change if you've overridden
            // discovery_prefix in HA. Ignored when HaDiscovery is false.
            [JsonProperty("DiscoveryPrefix")]
            public string DiscoveryPrefix = "homeassistant";

            [JsonProperty("KeepAliveSeconds")]
            public int KeepAliveSeconds = 30;
        }

        public sealed class VolumeSettings
        {
            [JsonProperty("DefaultLevel")]
            public int DefaultLevel = 50;
        }

        private const string UserPath = @"\User\appsettings.json";

        public static AppSettings LoadOrDefault()
        {
            try
            {
                if (File.Exists(UserPath))
                {
                    string text;
                    using (var sr = new StreamReader(UserPath, Encoding.UTF8))
                    {
                        text = sr.ReadToEnd();
                    }
                    var s = JsonConvert.DeserializeObject<AppSettings>(text);
                    if (s != null)
                    {
                        if (s.Tcp == null)    s.Tcp    = new TcpSettings();
                        if (s.Web == null)    s.Web    = new WebSettings();
                        if (s.Mqtt == null)   s.Mqtt   = new MqttSettings();
                        if (s.Volume == null) s.Volume = new VolumeSettings();
                        if (string.IsNullOrEmpty(s.DeviceId))     s.DeviceId     = "mpc3-302";
                        if (string.IsNullOrEmpty(s.FriendlyName)) s.FriendlyName = "MPC3 Controller";
                        ErrorLog.Notice("[settings] loaded {0}", UserPath);
                        return s;
                    }
                }
            }
            catch (Exception e)
            {
                ErrorLog.Warn("[settings] load failed, using defaults: {0}", e.Message);
            }
            ErrorLog.Notice("[settings] using defaults (no {0})", UserPath);
            return new AppSettings();
        }

        // Persist current settings to \User\appsettings.json. The Crestron
        // filesystem has no File.Replace, so write a sibling .tmp first and then
        // delete+copy - the .tmp survives a partial write at least.
        public void Save()
        {
            var tmp = UserPath + ".tmp";
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            using (var writer = new StreamWriter(tmp, false, Encoding.UTF8))
            {
                writer.Write(json);
                writer.Flush();
            }
            try { if (File.Exists(UserPath)) File.Delete(UserPath); } catch { }
            File.Copy(tmp, UserPath);
            try { File.Delete(tmp); } catch { }
            ErrorLog.Notice("[settings] saved to {0}", UserPath);
        }
    }
}
