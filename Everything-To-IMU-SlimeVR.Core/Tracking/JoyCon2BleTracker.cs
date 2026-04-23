using SlimeImuProtocol.SlimeVR;
using SlimeImuProtocol.Utility;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using static Everything_To_IMU_SlimeVR.TrackerConfig;

namespace Everything_To_IMU_SlimeVR.Tracking {
    /// <summary>
    /// Joy-Con 2 / Switch 2 Pro Controller / NSO GameCube 2 — BLE tracker.
    ///
    /// JSL does not handle Switch 2 controllers (they are BLE, not Bluetooth Classic HID),
    /// so this class talks to them directly via WinRT GATT using the protocol reverse-engineered
    /// by german77 / ndeadly and confirmed working in joycon2cpp + Joy2Win.
    ///
    /// Lifecycle: a JoyCon2Manager hands us an already-resolved BluetoothLEDevice plus the two
    /// characteristics we care about. We:
    ///   1. Subscribe to the input characteristic via WriteClientCharacteristicConfigurationDescriptor
    ///   2. Send the 2-step IMU enable sequence (0x0C subcmd 0x02 then 0x04, FF mask)
    ///   3. Parse the 62-byte notification on each ValueChanged
    ///   4. Run VQF inline + push rotation/accel/battery to SlimeVR via UDPHandler
    /// </summary>
    public class JoyCon2BleTracker : IBodyTracker, IDisposable {
        // GATT characteristic UUIDs on the controller. Both characteristics live under whatever
        // service GetGattServicesAsync returns first that contains them — we don't filter by service.
        public static readonly Guid InputReportUuid = Guid.Parse("ab7de9be-89fe-49ad-828f-118f09df7fd2");
        public static readonly Guid WriteCommandUuid = Guid.Parse("649d4ac9-8eb7-4e6c-af44-1ea54fe5f005");

        // IMU enable sequence — exactly what joycon2cpp / Joy2Win send.
        private static readonly byte[] CmdSensorInit  = { 0x0C, 0x91, 0x01, 0x02, 0x00, 0x04, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00 };
        private static readonly byte[] CmdSensorStart = { 0x0C, 0x91, 0x01, 0x04, 0x00, 0x04, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00 };

        // Scaling factors. Accel ±8G full scale, gyro ±2000 dps full scale, verified against
        // SDL's SDL_hidapi_switch2.c which tests on real hardware:
        //   accel_scale = g * 8 / INT16_MAX
        //   gyro_coeff  = 34.8 rad/s at INT16_MAX (≈1994 dps, rounds to ±2000 dps range)
        // ndeadly's earlier "48000 raw = 360°/s" note implies ±250 dps — wrong by ~8x, would
        // make the tracker respond far too weakly to rotation. Use SDL's constants.
        private const float AccelScaleMsPerUnit = 9.80665f * 8f / 32767f;     // m/s² per raw
        private const float GyroScaleRadSecPerUnit = 34.8f / 32767f;          // rad/s per raw
        // AK09919 magnetometer: ~0.15 µT per LSB (datasheet typical). VQF normalises the mag
        // vector internally so the absolute scale only matters for sanity checks; using the
        // datasheet value keeps debug output in real-world units.
        private const float MagScaleMicroTeslaPerUnit = 0.15f;
        // Earth field is ~25-65 µT. Reject readings outside a permissive window — all-zero
        // bytes (mag disabled / cable interference / first-packet artifacts) and absurd values
        // (saturated near a strong magnet) just fall back to the 6DoF path for that frame.
        private const float MagMinMagnitudeUt = 10f;
        private const float MagMaxMagnitudeUt = 200f;

        // Throttle SlimeVR sends — same reasoning as GenericControllerTracker (avoid UdpClient flooding).
        private static readonly long SendMinIntervalTicks = TimeSpan.TicksPerMillisecond * 5; // 200 Hz cap

        // VQF warm-up matching SensorOrientation: suppress orientation publish until ~200ms stationary.
        private static readonly long WarmupTicksRequired = Stopwatch.Frequency / 5; // 200 ms
        private const float WarmupGyroStationaryThresholdSq = 0.0225f;              // (0.15 rad/s)^2

        public enum Variant { JoyConLeft, JoyConRight, ProController, NsoGameCube, Unknown }

        private readonly BluetoothLEDevice _device;
        private readonly GattCharacteristic _inputChar;
        private readonly GattCharacteristic _writeChar;
        private readonly Variant _variant;
        private readonly int _index;
        private readonly int _id;
        private readonly ulong _bluetoothAddress;
        private readonly string _rememberedStringId;
        private string _macSpoof;
        private byte[] _macAddressBytes;
        private UDPHandler _udpHandler;
        private VQFWrapper _vqf;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly List<float> _averageSampleSeconds = new();
        private long _lastSampleTicks;
        private long _lastSendTicks;
        private long _warmupStableStartTicks = -1;
        private bool _warmupComplete;
        private bool _ready;
        private bool _disconnected;
        private bool _disposed;
        private string _debug = string.Empty;
        private Vector3 _accel;
        private Vector3 _gyroRad;
        private Vector3 _mag;            // µT, body frame
        private bool _magValid;          // last sample within plausible Earth-field window
        private long _magUsedSamples;    // diagnostic: how many 9D updates we have actually fed
        private Quaternion _rotation = Quaternion.Identity;
        private float _lastEulerPosition;
        private float _trackerEuler;
        private Vector3 _euler;
        private Vector3 _rotationCalibration;
        private float _lastBatteryFraction = 1f;
        private DateTime _lastBatteryPush = DateTime.MinValue;
        private RotationReferenceType _yawReferenceTypeValue = RotationReferenceType.HmdRotation;
        private RotationReferenceType _extensionYawReferenceTypeValue = RotationReferenceType.HmdRotation;
        private HapticNodeBinding _hapticNodeBinding;
        private long _sampleCount;
        private DateTime _lastRateWindow = DateTime.UtcNow;
        private double _lastRate;

        public event EventHandler<string> OnTrackerError;

        public bool SupportsHaptics => false; // BLE rumble unimplemented; protocol untested for haptics.
        public bool SupportsIMU => true;
        public string Debug { get => _debug; set => _debug = value; }
        public bool Ready { get => _ready; set => _ready = value; }
        public bool Disconnected { get => _disconnected; set => _disconnected = value; }
        public int Id { get => _id; set { } }
        public string MacSpoof { get => _macSpoof; set => _macSpoof = value; }
        public Vector3 Euler { get => _euler; set => _euler = value; }
        public float LastHmdPositon { get => _lastEulerPosition; set => _lastEulerPosition = value; }
        public HapticNodeBinding HapticNodeBinding { get => _hapticNodeBinding; set => _hapticNodeBinding = value; }
        public Vector3 RotationCalibration { get => _rotationCalibration; set => _rotationCalibration = value; }
        public RotationReferenceType YawReferenceTypeValue { get => _yawReferenceTypeValue; set => _yawReferenceTypeValue = value; }
        public RotationReferenceType ExtensionYawReferenceTypeValue { get => _extensionYawReferenceTypeValue; set => _extensionYawReferenceTypeValue = value; }
        public Variant ControllerVariant => _variant;
        public ulong BluetoothAddress => _bluetoothAddress;
        public float BatteryLevel => _lastBatteryFraction;

        public double Hz {
            get {
                var elapsed = (DateTime.UtcNow - _lastRateWindow).TotalSeconds;
                if (elapsed >= 1.0) {
                    _lastRate = _sampleCount / elapsed;
                    _sampleCount = 0;
                    _lastRateWindow = DateTime.UtcNow;
                }
                return _lastRate;
            }
        }

        public JoyCon2BleTracker(BluetoothLEDevice device, GattCharacteristic inputChar, GattCharacteristic writeChar, Variant variant, int index) {
            _device = device;
            _inputChar = inputChar;
            _writeChar = writeChar;
            _variant = variant;
            _index = index;
            _id = index + 1;
            _bluetoothAddress = device.BluetoothAddress;
            _rememberedStringId = $"JC2:{variant}:{_bluetoothAddress:X12}";
            _ = InitializeAsync();
        }

        private async Task InitializeAsync() {
            try {
                // Stable MAC = the Bluetooth address itself. SlimeVR keys body-slot assignments on this,
                // so it must survive across runs and re-pairings — the BT address does both.
                _macAddressBytes = BitConverter.GetBytes(_bluetoothAddress).Take(6).ToArray();
                _macSpoof = $"JoyCon2-{_bluetoothAddress:X12}";

                var imuHint = FirmwareConstants.ImuType.UNKNOWN; // Joy-Con 2 IMU chip not publicly identified yet.
                string firmwareString = $"0.7.2-EverythingToIMU-{_variant}";
                _udpHandler = new UDPHandler(firmwareString, _macAddressBytes,
                    FirmwareConstants.BoardType.CUSTOM, imuHint, FirmwareConstants.McuType.UNKNOWN,
                    FirmwareConstants.MagnetometerStatus.ENABLED, 1);
                _udpHandler.Active = true;

                // Register the notify callback FIRST so we don't lose the first packets fired right after
                // the sensor START command. ValueChanged is invoked on a thread-pool thread by WinRT.
                _inputChar.ValueChanged += OnInputCharValueChanged;

                var notifyStatus = await _inputChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (notifyStatus != GattCommunicationStatus.Success) {
                    OnTrackerError?.Invoke(this, $"WriteCCCD failed: {notifyStatus}");
                    return;
                }

                // joycon2cpp pattern: INIT + 500ms + START. Both WriteWithoutResponse.
                await SendCommandAsync(CmdSensorInit);
                await Task.Delay(500);
                await SendCommandAsync(CmdSensorStart);

                // Watch for hardware disconnect so the manager can recycle our slot.
                _device.ConnectionStatusChanged += OnConnectionStatusChanged;

                Recalibrate();
                _ready = true;
            } catch (Exception ex) {
                OnTrackerError?.Invoke(this, $"JoyCon2 init: {ex.Message}");
            }
        }

        private async Task SendCommandAsync(byte[] payload) {
            try {
                var buffer = payload.AsBuffer();
                await _writeChar.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse);
            } catch (Exception ex) {
                OnTrackerError?.Invoke(this, $"Write cmd failed: {ex.Message}");
            }
        }

        private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args) {
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) {
                _disconnected = true;
            }
        }

        private void OnInputCharValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) {
            if (_disposed) return;
            try {
                var reader = DataReader.FromBuffer(args.CharacteristicValue);
                int len = (int)reader.UnconsumedBufferLength;
                if (len < 0x3C) return; // need at least up to gyro Z
                var bytes = new byte[len];
                reader.ReadBytes(bytes);

                // IMU: int16 LE at the offsets documented by ndeadly. Motion block starts at
                // 0x2A (timestamp), accel at 0x30, gyro at 0x36. Axis convention follows SDL's
                // battle-tested SDL_hidapi_switch2.c: output = (raw_x, raw_z, -raw_y). This
                // leaves the data in VQF's body frame (Z up), so we use the Identity VQF
                // entry points that skip the JSL-specific (X, -Z, Y) remap.
                short axRaw = BitConverter.ToInt16(bytes, 0x30);
                short ayRaw = BitConverter.ToInt16(bytes, 0x32);
                short azRaw = BitConverter.ToInt16(bytes, 0x34);
                short gxRaw = BitConverter.ToInt16(bytes, 0x36);
                short gyRaw = BitConverter.ToInt16(bytes, 0x38);
                short gzRaw = BitConverter.ToInt16(bytes, 0x3A);

                _accel = new Vector3(axRaw * AccelScaleMsPerUnit,
                                      azRaw * AccelScaleMsPerUnit,
                                     -ayRaw * AccelScaleMsPerUnit);
                _gyroRad = new Vector3(gxRaw * GyroScaleRadSecPerUnit,
                                        gzRaw * GyroScaleRadSecPerUnit,
                                       -gyRaw * GyroScaleRadSecPerUnit);

                // Magnetometer at 0x19 (3 × int16 LE per ndeadly hid_reports.md). Feature bit 7
                // is enabled by our 0xFF mask in CmdSensorInit/Start so the controller streams it.
                // We sanity-check the magnitude against the Earth-field window — all-zero or
                // saturated readings (cable, magnet near controller) drop us back to 6DoF for
                // that frame instead of feeding garbage into VQF and corrupting the fused yaw.
                _magValid = false;
                if (len >= 0x1F) {
                    short mxRaw = BitConverter.ToInt16(bytes, 0x19);
                    short myRaw = BitConverter.ToInt16(bytes, 0x1B);
                    short mzRaw = BitConverter.ToInt16(bytes, 0x1D);
                    // Mag chip (AK09919) is co-mounted with the IMU (ICM-42670-P) on the PCB,
                    // so apply the same (raw_x, raw_z, -raw_y) remap SDL's IMU path uses.
                    // Untested — if yaw locks to the wrong direction, flip signs here.
                    var magUt = new Vector3(mxRaw * MagScaleMicroTeslaPerUnit,
                                             mzRaw * MagScaleMicroTeslaPerUnit,
                                            -myRaw * MagScaleMicroTeslaPerUnit);
                    float magMag = magUt.Length();
                    if (magMag > MagMinMagnitudeUt && magMag < MagMaxMagnitudeUt) {
                        _mag = magUt;
                        _magValid = true;
                    }
                }

                // Battery voltage in mV at 0x1F (uint16 LE) per ndeadly/switch2_controller_research.
                // Earlier jc2cpp README placed it at 0x1C — wrong, that position is magnetometer Y.
                // Map Li-ion cell window 3000..4200 mV → 0..1.
                if (len >= 0x21) {
                    int batMv = BitConverter.ToUInt16(bytes, 0x1F);
                    if (batMv > 0) {
                        _lastBatteryFraction = Math.Clamp((batMv - 3000) / 1200f, 0f, 1f);
                    }
                }

                // Adaptive VQF init using the average inter-sample dt of the first 200 packets,
                // mirroring SensorOrientation's strategy for the JSL path.
                long now = _stopwatch.ElapsedTicks;
                if (_vqf == null) {
                    if (_lastSampleTicks > 0) {
                        float dt = (float)((now - _lastSampleTicks) / (double)Stopwatch.Frequency);
                        if (dt > 0 && dt < 0.1f) _averageSampleSeconds.Add(dt);
                    }
                    _lastSampleTicks = now;
                    if (_averageSampleSeconds.Count >= 200) {
                        _vqf = new VQFWrapper(_averageSampleSeconds.Average());
                        _averageSampleSeconds.Clear();
                    }
                    return; // not yet emitting orientation
                }

                if (_magValid) {
                    _vqf.UpdateIdentity9D(_gyroRad, _accel, _mag);
                    _rotation = _vqf.GetQuat9DFast();
                    _magUsedSamples++;
                } else {
                    _vqf.UpdateIdentity(_gyroRad, _accel);
                    _rotation = _vqf.GetQuat6DFast();
                }

                if (!_warmupComplete) {
                    if (_gyroRad.LengthSquared() > WarmupGyroStationaryThresholdSq || _warmupStableStartTicks < 0) {
                        _warmupStableStartTicks = now;
                    } else if (now - _warmupStableStartTicks >= WarmupTicksRequired) {
                        _warmupComplete = true;
                    }
                    if (!_warmupComplete) return;
                }

                _ = PublishAsync();
            } catch (Exception ex) {
                OnTrackerError?.Invoke(this, $"OnInput: {ex.Message}");
            }
        }

        private async Task PublishAsync() {
            if (!_ready || _udpHandler == null) return;
            long nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - _lastSendTicks < SendMinIntervalTicks) return;
            _lastSendTicks = nowTicks;
            _sampleCount++;
            try {
                _rotation = Quaternion.Normalize(_rotation);
                // Apply user-persisted mount yaw (0/90/180/270) before sending so the same
                // physical device keeps its SlimeVR-side orientation across reconnects.
                var mountYaw = Configuration.Instance?.GetMountYawQuaternion(_macSpoof) ?? Quaternion.Identity;
                var publishedRotation = Quaternion.Normalize(mountYaw * _rotation);
                if (GenericTrackerManager.DebugOpen || _yawReferenceTypeValue != RotationReferenceType.TrustDeviceYaw) {
                    var trackerRotation = OpenVRReader.GetTrackerRotation(_yawReferenceTypeValue);
                    _trackerEuler = trackerRotation.GetYawFromQuaternion();
                    _lastEulerPosition = -_trackerEuler;
                    _euler = publishedRotation.QuaternionToEuler();
                }
                // Match GenericControllerTracker: send raw accel in g-units (server removes gravity).
                // _accel is in m/s² from the SDL-style scale; convert back to G for the UDP packet.
                await _udpHandler.SetSensorAcceleration(_accel / 9.80665f, 0);
                if (_yawReferenceTypeValue == RotationReferenceType.TrustDeviceYaw) {
                    await _udpHandler.SetSensorRotation(publishedRotation, 0);
                } else {
                    await _udpHandler.SetSensorRotation(new Vector3(_euler.X, _euler.Y, _lastEulerPosition).ToQuaternion(), 0);
                }

                if ((DateTime.UtcNow - _lastBatteryPush).TotalSeconds > 30) {
                    try { await _udpHandler.SetSensorBattery(_lastBatteryFraction * 100f, 3.7f); } catch { }
                    _lastBatteryPush = DateTime.UtcNow;
                }

                if (GenericTrackerManager.DebugOpen) {
                    string fusion = _magValid ? "9D" : "6D";
                    _debug =
                        $"Device Id: {_macSpoof}\r\n" +
                        $"Variant: {_variant}\r\n" +
                        $"BD Addr: {_bluetoothAddress:X12}\r\n" +
                        $"Hz: {Hz:F1}  Fusion: {fusion}  9D samples: {_magUsedSamples}\r\n" +
                        $"Euler: X:{_euler.X:F2}, Y:{_euler.Y:F2}, Z:{_rotation.Z:F2}\r\n" +
                        $"Gyro:  X:{_gyroRad.X:F3}, Y:{_gyroRad.Y:F3}, Z:{_gyroRad.Z:F3}\r\n" +
                        $"Accel: X:{_accel.X:F2}, Y:{_accel.Y:F2}, Z:{_accel.Z:F2}\r\n" +
                        $"Mag:   X:{_mag.X:F1}, Y:{_mag.Y:F1}, Z:{_mag.Z:F1}  |M|={_mag.Length():F1} uT (valid:{_magValid})\r\n" +
                        $"Battery: {_lastBatteryFraction * 100f:F0}%\r\n";
                }
            } catch (Exception ex) {
                OnTrackerError?.Invoke(this, $"Publish: {ex.Message}");
            }
        }

        public Vector3 GetCalibration() => -_rotation.QuaternionToEuler();

        public async void Recalibrate() {
            await Task.Delay(3000);
            _rotationCalibration = GetCalibration();
            try { await _udpHandler.SendButton(FirmwareConstants.UserActionType.RESET_FULL); } catch { }
        }

        public void Rediscover() {
            try { _udpHandler?.Rehandshake(); } catch (Exception ex) { OnTrackerError?.Invoke(this, ex.Message); }
        }

        public void Identify() {
            // No haptic primitive yet; keep a no-op so the UI Identify button does not throw.
        }

        public void HapticIntensityTest() { /* not implemented */ }
        public void EngageHaptics(int duration, float intensity) { /* not implemented */ }
        public void DisableHaptics() { /* not implemented */ }

        public override string ToString() => _rememberedStringId;

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            _ready = false;
            _disconnected = true;
            try { _inputChar.ValueChanged -= OnInputCharValueChanged; } catch { }
            try { _device.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
            try { _vqf?.Dispose(); } catch { }
            try { _udpHandler?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
        }
    }
}
