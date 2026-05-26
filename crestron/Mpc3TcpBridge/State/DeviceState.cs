using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Mpc3TcpBridge.State
{
    // In-memory model of everything the bridge tracks. Mutating callers go
    // through the Set* methods, which fire events that TcpServer turns into
    // outbound JSON broadcasts. Readers grab a CaptureSnapshot() copy.
    //
    // Thread safety: a single object lock guards every field. Event
    // handlers fire outside the lock so a subscriber can't deadlock by
    // calling back into DeviceState.
    public sealed class DeviceState
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, bool> _leds = new Dictionary<string, bool>();
        private int _volumePercent;
        private bool _muted;

        public event Action<string, bool> LedChanged;          // (name, on)
        public event Action<int> VolumeChanged;                 // 0..100
        public event Action<bool> MuteChanged;                  // muted
        public event Action<string, bool> ButtonEvent;          // (name, pressed)

        public DeviceState()
        {
            foreach (var name in ButtonNames.All())
                _leds[name] = false;
            _volumePercent = 50;
            _muted = false;
        }

        public bool GetLed(string name)
        {
            lock (_lock)
            {
                bool v;
                return _leds.TryGetValue(name, out v) && v;
            }
        }

        public void SetLed(string name, bool on)
        {
            bool changed;
            lock (_lock)
            {
                bool current;
                changed = !_leds.TryGetValue(name, out current) || current != on;
                _leds[name] = on;
            }
            if (changed)
            {
                var h = LedChanged;
                if (h != null) h(name, on);
            }
        }

        public int GetVolumePercent()
        {
            lock (_lock) { return _volumePercent; }
        }

        public void SetVolumePercent(int v)
        {
            if (v < 0) v = 0;
            if (v > 100) v = 100;
            bool changed;
            lock (_lock)
            {
                changed = (_volumePercent != v);
                _volumePercent = v;
            }
            if (changed)
            {
                var h = VolumeChanged;
                if (h != null) h(v);
            }
        }

        public bool GetMuted()
        {
            lock (_lock) { return _muted; }
        }

        public void SetMuted(bool muted)
        {
            bool changed;
            lock (_lock)
            {
                changed = (_muted != muted);
                _muted = muted;
            }
            if (changed)
            {
                var h = MuteChanged;
                if (h != null) h(muted);
            }
        }

        // Record a button press/release coming off the panel hardware (or
        // off the `mpctcp emit` console command for testing while the panel
        // input firmware bug is in effect). TcpServer subscribes and turns
        // each event into a {"event":"button",...} broadcast.
        public void RecordButtonEvent(string name, bool pressed)
        {
            var h = ButtonEvent;
            if (h != null) h(name, pressed);
        }

        public Snapshot CaptureSnapshot()
        {
            lock (_lock)
            {
                return new Snapshot
                {
                    Leds = new Dictionary<string, bool>(_leds),
                    VolumePercent = _volumePercent,
                    Muted = _muted,
                };
            }
        }
    }

    public sealed class Snapshot
    {
        [JsonProperty("leds")]
        public Dictionary<string, bool> Leds;

        [JsonProperty("volume")]
        public int VolumePercent;

        [JsonProperty("muted")]
        public bool Muted;
    }
}
