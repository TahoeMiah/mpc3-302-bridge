using System;
using System.IO;
using System.Text;
using Crestron.SimplSharp;
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
        [JsonProperty("Tcp")]
        public TcpSettings Tcp = new TcpSettings();

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
                    var text = File.ReadAllText(UserPath, Encoding.UTF8);
                    var s = JsonConvert.DeserializeObject<AppSettings>(text);
                    if (s != null)
                    {
                        if (s.Tcp == null)    s.Tcp    = new TcpSettings();
                        if (s.Volume == null) s.Volume = new VolumeSettings();
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
    }
}
