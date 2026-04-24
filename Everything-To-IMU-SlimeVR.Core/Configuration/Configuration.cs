using Newtonsoft.Json;
using System;
using System.Numerics;
namespace Everything_To_IMU_SlimeVR
{
    public class Configuration
    {
        private string _oscIpAddress = "127.0.0.1";
        private string _portInput = "9001";
        private List<int> _portOutputs = new List<int>();

        private List<TrackerConfig> _trackerConfigs = new List<TrackerConfig>();
        private List<TrackerConfig> _trackerConfig3ds = new List<TrackerConfig>();
        private Dictionary<string, TrackerConfig> _trackerConfigWiimote = new Dictionary<string, TrackerConfig>();
        private List<TrackerConfig> _trackerConfigNunchuck = new List<TrackerConfig>();
        private Dictionary<string, Vector3> _calibrationConfigurations = new Dictionary<string, Vector3>();
        private DateTime _lastConfigSave = new DateTime();
        private bool _switchingSessions = false;

        private int _pollingRate = 8;
        private byte _wiiPollingRate = 32;
        private Dictionary<string, TrackerConfig> _trackerConfigUdpHaptics = new Dictionary<string, TrackerConfig>();
        private Dictionary<string, ControllerMountConfig> _controllerMounts = new Dictionary<string, ControllerMountConfig>();
        private bool _simulatesThighs;
        private bool _audioHapticsActive = true;
        private string _language = "en";
        private string _theme = "Dark";
        private List<string> _joyCon2KnownAddresses = new List<string>();
        private bool _notificationsEnabled = true;
        private float _batteryLowThreshold = 0.15f;
        // Companion listeners (3DS / Wiimote) and OSC receiver bind loopback by default.
        // Enabling this allows private-range LAN sources — needed when the 3DS or WiiClient
        // companion runs on a separate device. OSC LAN is separate because exposing it has
        // different implications (VRChat usually loopback anyway).
        private bool _acceptCompanionsFromLan = false;
        private bool _acceptOscFromLan = false;

        public List<TrackerConfig> TrackerConfigs { get => _trackerConfigs; set => _trackerConfigs = value; }
        public List<TrackerConfig> TrackerConfigs3ds { get => _trackerConfig3ds; set => _trackerConfig3ds = value; }
        public Dictionary<string, TrackerConfig> TrackerConfigWiimote { get => _trackerConfigWiimote; set => _trackerConfigWiimote = value; }
        public List<TrackerConfig> TrackerConfigNunchuck { get => _trackerConfigNunchuck; set => _trackerConfigNunchuck = value; }
        public Dictionary<string, TrackerConfig> TrackerConfigUdpHaptics { get => _trackerConfigUdpHaptics; set => _trackerConfigUdpHaptics = value; }
        public Dictionary<string, ControllerMountConfig> ControllerMounts { get => _controllerMounts; set => _controllerMounts = value ?? new(); }
        public Dictionary<string, Vector3> CalibrationConfigurations { get => _calibrationConfigurations; set => _calibrationConfigurations = value; }
        public DateTime LastCalibration { get => _lastConfigSave; set => _lastConfigSave = value; }
        public int PollingRate { get => _pollingRate; set => _pollingRate = value; }
        public bool SwitchingSessions { get => _switchingSessions; set => _switchingSessions = value; }
        public static Configuration? Instance { get; private set; }
        public byte WiiPollingRate { get => _wiiPollingRate; set => _wiiPollingRate = value; }
        public bool SimulatesThighs { get => _simulatesThighs; set => _simulatesThighs = value; }
        public bool AudioHapticsActive { get => _audioHapticsActive; set => _audioHapticsActive = value; }
        public string OscIpAddress { get => _oscIpAddress; set => _oscIpAddress = value; }
        public string PortInput { get => _portInput; set => _portInput = value; }
        public List<int> PortOutputs { get => _portOutputs; set => _portOutputs = value; }
        public string Language { get => _language; set => _language = value; }
        public string Theme { get => _theme; set => _theme = value; }
        public List<string> JoyCon2KnownAddresses { get => _joyCon2KnownAddresses; set => _joyCon2KnownAddresses = value ?? new(); }
        public bool NotificationsEnabled { get => _notificationsEnabled; set => _notificationsEnabled = value; }
        public float BatteryLowThreshold { get => _batteryLowThreshold; set => _batteryLowThreshold = Math.Clamp(value, 0.01f, 0.5f); }
        public bool AcceptCompanionsFromLan { get => _acceptCompanionsFromLan; set => _acceptCompanionsFromLan = value; }
        public bool AcceptOscFromLan { get => _acceptOscFromLan; set => _acceptOscFromLan = value; }

        public void RememberJoyCon2Address(ulong address)
        {
            string hex = address.ToString("X12");
            if (_joyCon2KnownAddresses.Contains(hex)) return;
            _joyCon2KnownAddresses.Add(hex);
            try { SaveDebounced(); } catch { }
        }

        public void SaveConfig()
        {
            _lastConfigSave = DateTime.UtcNow;
            string savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            File.WriteAllText(savePath, JsonConvert.SerializeObject(this));
        }

        // Debounce interval — slider drags, rapid clicks coalesce into one write. Max data loss
        // window on crash = this value. 2s keeps SSD write volume low while limiting exposure.
        private const int SaveDebounceMs = 2000;
        [JsonIgnore] private System.Threading.Timer? _saveDebounceTimer;
        [JsonIgnore] private readonly object _saveDebounceLock = new();

        /// <summary>
        /// Coalesced save — call freely on every UI mutation. Actual disk write happens once the
        /// stream of calls quiets for <see cref="SaveDebounceMs"/>. For irrecoverable state (exit,
        /// crash handler) call <see cref="SaveConfig"/> directly.
        /// </summary>
        public void SaveDebounced()
        {
            lock (_saveDebounceLock)
            {
                if (_saveDebounceTimer == null)
                {
                    _saveDebounceTimer = new System.Threading.Timer(_ =>
                    {
                        try { SaveDebounced(); } catch { }
                    });
                }
                _saveDebounceTimer.Change(SaveDebounceMs, System.Threading.Timeout.Infinite);
            }
        }

        /// <summary>
        /// Flush any pending debounced save immediately. Call on shutdown.
        /// </summary>
        public void FlushPendingSave()
        {
            lock (_saveDebounceLock)
            {
                _saveDebounceTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }
            try { SaveDebounced(); } catch { }
        }
        public TimeSpan TimeSinceLastConfig()
        {
            return DateTime.UtcNow - _lastConfigSave;
        }

        public int GetMountYawDegrees(string macKey)
        {
            if (string.IsNullOrEmpty(macKey)) return 0;
            return _controllerMounts.TryGetValue(macKey, out var c) ? c.YawDegrees : 0;
        }

        /// <summary>
        /// Rotates the stored mount yaw by <paramref name="deltaDegrees"/> and normalises to
        /// [0, 360). Returns the new value. Persists the config file immediately so the change
        /// survives a crash before the next graceful save.
        /// </summary>
        public int BumpMountYaw(string macKey, int deltaDegrees)
        {
            if (string.IsNullOrEmpty(macKey)) return 0;
            if (!_controllerMounts.TryGetValue(macKey, out var c))
            {
                c = new ControllerMountConfig();
                _controllerMounts[macKey] = c;
            }
            c.YawDegrees = ((c.YawDegrees + deltaDegrees) % 360 + 360) % 360;
            try { SaveDebounced(); } catch { }
            return c.YawDegrees;
        }

        /// <summary>
        /// Builds the Z-axis rotation quaternion corresponding to the mount yaw. Multiply the
        /// tracker's fused rotation by this before sending to SlimeVR.
        /// </summary>
        public System.Numerics.Quaternion GetMountYawQuaternion(string macKey)
        {
            int deg = GetMountYawDegrees(macKey);
            if (deg == 0) return System.Numerics.Quaternion.Identity;
            float rad = (float)(deg * Math.PI / 180.0);
            return System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, rad);
        }

        public float GetGyroScaleTrim(string macKey)
        {
            if (string.IsNullOrEmpty(macKey)) return 1.0f;
            return _controllerMounts.TryGetValue(macKey, out var c) ? c.GyroScaleTrim : 1.0f;
        }

        public void SetGyroScaleTrim(string macKey, float trim)
        {
            if (string.IsNullOrEmpty(macKey)) return;
            // Hard clamp prevents the slider from being driven to nonsense if it's ever wired
            // to free input (e.g. textbox). The fusion filter goes wild outside this window.
            trim = Math.Clamp(trim, 0.5f, 1.5f);
            if (!_controllerMounts.TryGetValue(macKey, out var c))
            {
                c = new ControllerMountConfig();
                _controllerMounts[macKey] = c;
            }
            c.GyroScaleTrim = trim;
            try { SaveDebounced(); } catch { }
        }
        public static Configuration LoadConfig()
        {
            string openPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (File.Exists(openPath))
            {
                Configuration values = null;
                try
                {
                    values = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(openPath));
                } catch
                {
                }
                Instance = values;
                return Instance = (values == null ? new Configuration()
                {
                    PortOutputs = new List<int>() {
                        9002,
                    }
                } : values);
            } else
            {
                return Instance = new Configuration()
                {
                    PortOutputs = new List<int>() {
                        9002,
                    }
                };
            }
        }
    }
}
