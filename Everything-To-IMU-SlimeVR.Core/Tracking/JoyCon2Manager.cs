using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Everything_To_IMU_SlimeVR.Tracking {
    /// <summary>
    /// Discovers Switch 2 controllers (Joy-Con 2 / Pro 2 / NSO GC 2) via BLE advertisement scanning
    /// and spawns one <see cref="JoyCon2BleTracker"/> per physical device.
    ///
    /// The watcher pattern matches what joycon2cpp does: filter adverts in the Received callback by
    /// Nintendo's manufacturer ID + the Switch 2 data prefix, then resolve the device by its BD
    /// address and discover the GATT characteristics by UUID. No Windows pairing dialog needed —
    /// these controllers respond to GATT requests directly off the back of an active advert.
    /// </summary>
    public static class JoyCon2Manager {
        private const ushort NintendoCompanyId = 0x0553;
        private static readonly byte[] Switch2DataPrefix = { 0x01, 0x00, 0x03, 0x7E };

        // Per-controller cooldown (the chip itself locks itself out for several minutes when you
        // hammer it with reconnect attempts; 5s is enough to avoid that for normal flows).
        private static readonly TimeSpan ReconnectCooldown = TimeSpan.FromSeconds(5);

        private static readonly object _lock = new();
        private static readonly ConcurrentDictionary<ulong, DateTime> _recentAttempts = new();
        private static readonly List<JoyCon2BleTracker> _trackers = new();
        private static BluetoothLEAdvertisementWatcher _watcher;
        private static int _spawnedCount;

        public static IReadOnlyList<JoyCon2BleTracker> Trackers {
            get { lock (_lock) return _trackers.ToArray(); }
        }

        public static event EventHandler<JoyCon2BleTracker> TrackerAdded;
        public static event EventHandler<string> OnError;

        public static void Start() {
            lock (_lock) {
                if (_watcher != null) return;
                _watcher = new BluetoothLEAdvertisementWatcher {
                    ScanningMode = BluetoothLEScanningMode.Active
                };
                _watcher.Received += OnAdvertReceived;
                _watcher.Stopped += (_, args) => {
                    OnError?.Invoke(null, $"BLE watcher stopped: {args.Error}");
                };
                try {
                    _watcher.Start();
                } catch (Exception ex) {
                    OnError?.Invoke(null, $"BLE watcher start failed: {ex.Message}");
                    _watcher = null;
                }
            }
        }

        public static void Stop() {
            lock (_lock) {
                try { _watcher?.Stop(); } catch { }
                _watcher = null;
                foreach (var t in _trackers) {
                    try { t.Dispose(); } catch { }
                }
                _trackers.Clear();
                _recentAttempts.Clear();
                _spawnedCount = 0;
            }
        }

        /// <summary>
        /// Removes any tracker that has flagged itself disconnected. Returns the freed indices so
        /// the caller (GenericTrackerManager) can also drop them from its aggregate list.
        /// </summary>
        public static List<JoyCon2BleTracker> ReapDisconnected() {
            var dropped = new List<JoyCon2BleTracker>();
            lock (_lock) {
                for (int i = _trackers.Count - 1; i >= 0; i--) {
                    var t = _trackers[i];
                    if (t.Disconnected) {
                        dropped.Add(t);
                        _trackers.RemoveAt(i);
                        try { t.Dispose(); } catch { }
                    }
                }
            }
            return dropped;
        }

        private static void OnAdvertReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args) {
            // Cheap path: ignore adverts that don't carry Nintendo + Switch-2 prefix manufacturer data.
            JoyCon2BleTracker.Variant variant = JoyCon2BleTracker.Variant.Unknown;
            bool match = false;
            foreach (var section in args.Advertisement.ManufacturerData) {
                if (section.CompanyId != NintendoCompanyId) continue;
                var data = section.Data.ToArray();
                if (data.Length < Switch2DataPrefix.Length) continue;
                bool prefixOk = true;
                for (int i = 0; i < Switch2DataPrefix.Length; i++) {
                    if (data[i] != Switch2DataPrefix[i]) { prefixOk = false; break; }
                }
                if (!prefixOk) continue;
                match = true;
                // Byte index 4 (just past the prefix) hints at variant on Joy-Con 2 family. Mapping
                // is empirical; default to Unknown when in doubt so the user can categorise via UI.
                if (data.Length >= 5) {
                    variant = data[4] switch {
                        0x05 => JoyCon2BleTracker.Variant.JoyConRight,
                        0x06 => JoyCon2BleTracker.Variant.JoyConLeft,
                        0x09 => JoyCon2BleTracker.Variant.ProController,
                        0x0A => JoyCon2BleTracker.Variant.NsoGameCube,
                        _ => JoyCon2BleTracker.Variant.Unknown,
                    };
                }
                break;
            }
            if (!match) return;

            ulong addr = args.BluetoothAddress;
            // Reject during cooldown OR if we've already got a live tracker for this device.
            if (_recentAttempts.TryGetValue(addr, out var last) && DateTime.UtcNow - last < ReconnectCooldown) return;
            lock (_lock) {
                if (_trackers.Any(t => t.BluetoothAddress == addr && !t.Disconnected)) return;
            }
            _recentAttempts[addr] = DateTime.UtcNow;

            _ = ConnectAsync(addr, variant);
        }

        private static async Task ConnectAsync(ulong address, JoyCon2BleTracker.Variant variant) {
            BluetoothLEDevice device = null;
            try {
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
                if (device == null) {
                    OnError?.Invoke(null, $"FromBluetoothAddressAsync returned null for {address:X12}");
                    return;
                }
                // Throughput-optimized connection params drop BLE interval to ~7.5ms which is what
                // the IMU stream needs to keep up. Without this the controller still works but the
                // sample rate hovers around 30Hz instead of the ~120Hz the hardware actually pushes.
                try {
                    var prefs = BluetoothLEPreferredConnectionParameters.ThroughputOptimized;
                    device.RequestPreferredConnectionParameters(prefs);
                } catch { /* not fatal — older BT stacks reject this */ }

                var servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                if (servicesResult.Status != GattCommunicationStatus.Success) {
                    OnError?.Invoke(null, $"GATT services unreachable: {servicesResult.Status}");
                    device.Dispose();
                    return;
                }
                GattCharacteristic inputChar = null, writeChar = null;
                foreach (var service in servicesResult.Services) {
                    var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    if (charsResult.Status != GattCommunicationStatus.Success) continue;
                    foreach (var ch in charsResult.Characteristics) {
                        if (ch.Uuid == JoyCon2BleTracker.InputReportUuid) inputChar = ch;
                        else if (ch.Uuid == JoyCon2BleTracker.WriteCommandUuid) writeChar = ch;
                    }
                    if (inputChar != null && writeChar != null) break;
                }
                if (inputChar == null || writeChar == null) {
                    OnError?.Invoke(null, $"Required GATT chars missing on {address:X12}");
                    device.Dispose();
                    return;
                }

                int idx;
                JoyCon2BleTracker tracker;
                lock (_lock) {
                    idx = _spawnedCount++;
                    tracker = new JoyCon2BleTracker(device, inputChar, writeChar, variant, idx);
                    _trackers.Add(tracker);
                }
                tracker.OnTrackerError += (s, e) => OnError?.Invoke(s, e);
                TrackerAdded?.Invoke(null, tracker);
            } catch (Exception ex) {
                OnError?.Invoke(null, $"ConnectAsync({address:X12}): {ex.Message}");
                try { device?.Dispose(); } catch { }
            }
        }
    }
}
