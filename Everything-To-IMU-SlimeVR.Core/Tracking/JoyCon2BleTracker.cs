using SlimeImuProtocol.SlimeVR;
using SlimeImuProtocol.Utility;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
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
        public static readonly Guid ResponseNotifyUuid = Guid.Parse("c765a961-d9d8-4d36-a20a-5315b111836a");

        // IMU enable sequence — exactly what joycon2cpp / Joy2Win send.
        private static readonly byte[] CmdSensorInit  = { 0x0C, 0x91, 0x01, 0x02, 0x00, 0x04, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00 };
        private static readonly byte[] CmdSensorStart = { 0x0C, 0x91, 0x01, 0x04, 0x00, 0x04, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00 };
        // Vibration preset templates per ndeadly commands.md. Byte 8 = preset id (1 = single
        // pulse, 3 = paired double pulse, 4 = sound, 5 = paired vibration). Use 1 for the
        // generic Identify pulse; SlimeVR's Identify gesture is a short single buzz.
        private static byte[] BuildRumblePreset(byte preset) => new byte[] {
            0x0A, 0x91, 0x01, 0x02, 0x00, 0x04, 0x00, 0x00, preset, 0x00, 0x00, 0x00
        };

        // Scaling factors. Verified against Joy2Win (Python ref impl, BLE captures):
        //   Accel: 4096 raw = 1G → ±8G at int16 range.
        //   Gyro : 6048 raw = 360°/s → ±1950 dps at int16 (SDL's ±2000 dps is correct).
        // Memory note "48000 raw = 360°/s" was wrong; Joy2Win empirically shows 6048.
        private const float AccelScaleMsPerUnit = 9.80665f / 4096f;                     // m/s² per raw
        private const float GyroScaleRadSecPerUnit = (float)(Math.PI / 180.0 * 360.0 / 6048.0); // rad/s per raw (360°/s per 6048 units)
        // AK09919 magnetometer: ~0.15 µT per LSB (datasheet typical). VQF normalises the mag
        // vector internally so the absolute scale only matters for sanity checks; using the
        // datasheet value keeps debug output in real-world units.
        private const float MagScaleMicroTeslaPerUnit = 0.15f;
        // Earth field is ~25-65 µT. Reject readings outside a permissive window — all-zero
        // bytes (mag disabled / cable interference / first-packet artifacts) and absurd values
        // (saturated near a strong magnet) just fall back to the 6DoF path for that frame.
        // Post-bias validity window. Earth field sits in 25-65 µT, so 10-120 is a loose
        // envelope that accepts local anomalies (steel desks, speaker magnets) without
        // letting through saturated readings (magnet directly on the controller). Pre-bias
        // magnitudes sat at ~150-200 µT, so this gate would have rejected every frame on
        // uncalibrated data — the bias subtraction earlier in the pipeline is what makes a
        // tight window workable.
        private const float MagMinMagnitudeUt = 10f;
        private const float MagMaxMagnitudeUt = 120f;

        // Throttle SlimeVR sends — same reasoning as GenericControllerTracker (avoid UdpClient flooding).
        private static readonly long SendMinIntervalTicks = TimeSpan.TicksPerMillisecond * 5; // 200 Hz cap

        // VQF warm-up matching SensorOrientation: suppress orientation publish until ~200ms stationary.
        // 50 ms — Joy-Con 2 IMU bias is small enough out of the box that the longer 200 ms
        // gate just delayed pose availability. VQF still converges internally.
        private static readonly long WarmupTicksRequired = Stopwatch.Frequency / 20;
        private const float WarmupGyroStationaryThresholdSq = 0.0225f;              // (0.15 rad/s)^2

        public enum Variant { JoyConLeft, JoyConRight, ProController, NsoGameCube, Unknown }

        private readonly BluetoothLEDevice _device;
        private readonly GattCharacteristic _inputChar;
        private readonly GattCharacteristic _writeChar;
        private Variant _variant;
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
        private string _lastPacketHex = string.Empty;
        private int _lastPacketLen;
        private Vector3 _mag;            // µT, body frame, post-bias when _magBiasValid
        private bool _magValid;          // last sample within plausible Earth-field window
        private long _magUsedSamples;    // diagnostic: how many 9D updates we have actually fed
        // Factory hard-iron bias stored in flash at 0x13100 (3× f32 LE, µT). Populated by
        // ResolveMagBiasViaFlashAsync. Subtract from raw*scale to recentre around Earth field.
        // conhid reads this address but never applies it; we do. _magBiasValid gates usage so
        // trackers that fail the read (comms error, BLE glitch) fall back to raw readings.
        private Vector3 _magBias;
        private bool _magBiasValid;
        private string _magBiasStatus = "pending";   // debug: pending / flash-ok / flash-fail / autocal-ok
        // Runtime autocalibrate fallback when factory flash read fails. Collect the first N
        // raw-µT samples while gyro is near zero (controller still), take the mean as bias.
        // Not perfect (doesn't touch soft-iron) but centres |M| around Earth field well enough
        // for VQF's heading update, which cares about direction not magnitude.
        // Manual reset on JC2: Home or Capture press-edge → RESET_FULL. No yaw-only variant —
        // tester preference is a single gesture for the full recenter. Bytes per ndeadly
        // hid_reports.md: Home = byte 0x05 bit 0x10, Capture = byte 0x05 bit 0x20.
        private bool _homeHeld;
        private bool _captureHeld;
        private Vector3 _autoCalSum;
        private int _autoCalCount;
        private const int AutoCalTargetSamples = 500;
        // Motion-based sampling: only accept frames where the controller is actively rotating
        // (gyro above threshold). Averaging over rotational samples cancels the Earth-field
        // component (which points the same way in world frame regardless of chip orientation)
        // and leaves the body-frame hard-iron bias as the residual. A "still-sample" version
        // of this autocal would subtract Earth field too, leaving the 9D fusion with no actual
        // heading reference — which is exactly what a tester on 2026-04-24 saw (|M| ≈ 3 µT
        // post-still-autocal). Threshold picked to reject hand tremor but accept a gentle
        // figure-eight wave — user doesn't need to perform a violent rotation.
        private const float AutoCalMinGyroRadSec = 0.5f;   // ~28 °/s — deliberate motion
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
        // Raw IMU callback rate (before send throttle). More useful diagnostic than the send
        // Hz — if BLE connection interval falls out, this drops below 50 and we know the
        // link is bad before the user sees lag.
        private long _imuSampleCount;
        private DateTime _imuLastRateWindow = DateTime.UtcNow;
        private double _imuLastRate;
        public double ImuSampleRateHz
        {
            get
            {
                var elapsed = (DateTime.UtcNow - _imuLastRateWindow).TotalSeconds;
                if (elapsed >= 1.0)
                {
                    _imuLastRate = _imuSampleCount / elapsed;
                    _imuSampleCount = 0;
                    _imuLastRateWindow = DateTime.UtcNow;
                }
                return _imuLastRate;
            }
        }
        // Jitter EWMA — angular delta between consecutive published quaternions.
        private Quaternion _prevPublishedRotation = Quaternion.Identity;
        private float _jitterEwmaDeg;
        public float JitterDegrees => _jitterEwmaDeg;

        public event EventHandler<string> OnTrackerError;

        // Rumble shipped via the simple-preset CMD 0x0A path. HD Rumble (output report 0x01,
        // packed LRA frame data) is documented but unimplemented — preset is enough for
        // SlimeVR's Identify pulse and the generic haptic test.
        public bool SupportsHaptics => true;
        public bool SupportsIMU => true;
        public long PacketsSent => _udpHandler?.PacketsSent ?? 0;
        public long SendFailures => _udpHandler?.SendFailures ?? 0;
        public bool ServerReachable => _udpHandler?.ServerReachable ?? false;
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
                    FirmwareConstants.BoardType.WRANGLER, imuHint, FirmwareConstants.McuType.WRANGLER,
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

                // Best-effort flash reads: variant at 0x13012 (2 bytes), mag hard-iron bias at
                // 0x13100 (12 bytes). Batched in a single subscribe/dispatch cycle because back-
                // to-back subscribe/unsubscribe on Windows' BLE notify descriptor sometimes
                // drops the second read silently. Background — IMU streams immediately.
                _ = Task.Run(ResolveFlashCalibrationAsync);

                // Watch for hardware disconnect so the manager can recycle our slot.
                _device.ConnectionStatusChanged += OnConnectionStatusChanged;

                _ = Recalibrate();
                _ready = true;
            } catch (Exception ex) {
                OnTrackerError?.Invoke(this, $"JoyCon2 init: {ex.Message}");
            }
        }

        /// <summary>
        /// Batched factory-flash read: subscribes once to the response-notify characteristic,
        /// issues (1) variant PID read at 0x13012 and (2) mag hard-iron bias read at 0x13100,
        /// then unsubscribes. Each request has a dedicated TaskCompletionSource keyed on the
        /// echoed request address so responses can't be cross-claimed. 500 ms pacing between
        /// writes keeps the controller's command queue from dropping the second request —
        /// observed empirically on the Switch 2 firmware.
        /// </summary>
        private async Task ResolveFlashCalibrationAsync() {
            GattCharacteristic responseChar = null;
            TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler = null;
            var variantTcs = new TaskCompletionSource<byte[]>();
            var magBiasTcs = new TaskCompletionSource<byte[]>();
            try {
                // Uncached so we see fresh attribute values on reconnect (cached handle can
                // lag after OS side pairing changes).
                var services = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                if (services.Status != GattCommunicationStatus.Success) { _magBiasStatus = "gatt-fail"; return; }
                foreach (var svc in services.Services) {
                    var chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    if (chars.Status != GattCommunicationStatus.Success) continue;
                    foreach (var ch in chars.Characteristics) {
                        if (ch.Uuid == ResponseNotifyUuid) { responseChar = ch; break; }
                    }
                    if (responseChar != null) break;
                }
                if (responseChar == null) { _magBiasStatus = "no-response-char"; return; }

                handler = (_, args) => {
                    try {
                        var reader = DataReader.FromBuffer(args.CharacteristicValue);
                        var bytes = new byte[reader.UnconsumedBufferLength];
                        reader.ReadBytes(bytes);
                        // Accept cmd echo 0x02 status 0x01. Match by request address echoed
                        // inside the response body. Address is uint32 LE, appears somewhere
                        // in the 8..16 byte header window — scan for either request's addr.
                        if (bytes.Length < 14 || bytes[0] != 0x02 || bytes[1] != 0x01) return;
                        bool matchesVariant = ContainsAddrLe(bytes, 0x00013012);
                        bool matchesMagBias = ContainsAddrLe(bytes, 0x00013100);
                        if (matchesVariant) variantTcs.TrySetResult(bytes);
                        if (matchesMagBias) magBiasTcs.TrySetResult(bytes);
                    } catch { }
                };
                responseChar.ValueChanged += handler;

                var notifyStatus = await responseChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (notifyStatus != GattCommunicationStatus.Success) { _magBiasStatus = "notify-fail"; return; }

                // Request 1: variant PID @ 0x13012 length 2
                byte[] variantCmd = {
                    0x02, 0x91, 0x01, 0x01, 0x00, 0x06, 0x00, 0x00,
                    0x12, 0x30, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00
                };
                await SendCommandAsync(variantCmd);
                var variantWin = await Task.WhenAny(variantTcs.Task, Task.Delay(2000));
                if (variantWin == variantTcs.Task) {
                    var pid = ExtractPidFromFlashResponse(variantTcs.Task.Result);
                    if (pid.HasValue) {
                        _variant = pid.Value switch {
                            0x2066 => Variant.JoyConRight,
                            0x2067 => Variant.JoyConLeft,
                            0x2068 => Variant.JoyConRight,
                            0x2069 => Variant.ProController,
                            0x2073 => Variant.NsoGameCube,
                            _ => Variant.Unknown,
                        };
                    }
                }

                // Request 2: mag hard-iron bias @ 0x13100 length 12 (3× f32 LE). Retried with
                // exponential backoff — with 8 controllers paired, Windows' BLE stack serializes
                // responses across the shared response characteristic and a single 3 s window
                // often loses to other controllers' traffic. 3 attempts × 8 s each covers the
                // common-case saturation without stalling reconnect indefinitely.
                byte[] magBiasCmd = {
                    0x02, 0x91, 0x01, 0x01, 0x00, 0x06, 0x00, 0x00,
                    0x00, 0x31, 0x01, 0x00, 0x0C, 0x00, 0x00, 0x00
                };
                bool biasLoaded = false;
                for (int attempt = 0; attempt < 3 && !biasLoaded; attempt++) {
                    await Task.Delay(500 + attempt * 500); // 500, 1000, 1500 ms between tries
                    await SendCommandAsync(magBiasCmd);
                    var magBiasWin = await Task.WhenAny(magBiasTcs.Task, Task.Delay(8000));
                    if (magBiasWin == magBiasTcs.Task) {
                        var bias = ExtractMagBiasFromFlashResponse(magBiasTcs.Task.Result);
                        if (bias.HasValue) {
                            // Factory bias takes priority over any autocal that may have
                            // already completed during the retry window. Overwrite regardless
                            // of previous _magBiasValid state.
                            _magBias = bias.Value;
                            _magBiasValid = true;
                            _magBiasStatus = "flash-ok";
                            biasLoaded = true;
                        } else {
                            _magBiasStatus = $"parse-fail-{attempt + 1}";
                            // Reset TCS for a retry — the bytes we got weren't what we wanted.
                            magBiasTcs = new TaskCompletionSource<byte[]>();
                        }
                    } else {
                        _magBiasStatus = $"timeout-{attempt + 1}";
                    }
                }
            } catch (Exception ex) {
                _magBiasStatus = "exception";
                OnTrackerError?.Invoke(this, $"Flash calibration: {ex.Message}");
            } finally {
                if (responseChar != null && handler != null) {
                    try { responseChar.ValueChanged -= handler; } catch { }
                    try {
                        await responseChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.None);
                    } catch { }
                }
            }
        }

        /// <summary>
        /// Checks whether the flash-read response body contains the 4-byte little-endian
        /// address we asked for. Search window is bounded because the address echo lives in
        /// the response header, not far from the start.
        /// </summary>
        private static bool ContainsAddrLe(byte[] bytes, uint addr) {
            byte b0 = (byte)(addr & 0xFF);
            byte b1 = (byte)((addr >> 8) & 0xFF);
            byte b2 = (byte)((addr >> 16) & 0xFF);
            byte b3 = (byte)((addr >> 24) & 0xFF);
            int limit = Math.Min(bytes.Length - 4, 24);
            for (int i = 4; i <= limit; i++) {
                if (bytes[i] == b0 && bytes[i + 1] == b1 && bytes[i + 2] == b2 && bytes[i + 3] == b3) return true;
            }
            return false;
        }

        /// <summary>
        /// The 12-byte bias payload (3× f32 LE) sits past the response header + addr/length
        /// echo. Same as <see cref="ExtractPidFromFlashResponse"/>, we scan a small window
        /// for three plausible floats (|value| < 500 µT per axis — well above any sane hard-
        /// iron but below a NaN/garbage read) and accept the first triple that fits.
        /// </summary>
        private static Vector3? ExtractMagBiasFromFlashResponse(byte[] bytes) {
            if (bytes.Length < 20) return null;
            for (int off = 8; off + 12 <= bytes.Length && off <= Math.Min(bytes.Length - 12, 24); off++) {
                float bx = BitConverter.ToSingle(bytes, off);
                float by = BitConverter.ToSingle(bytes, off + 4);
                float bz = BitConverter.ToSingle(bytes, off + 8);
                if (!float.IsFinite(bx) || !float.IsFinite(by) || !float.IsFinite(bz)) continue;
                if (MathF.Abs(bx) > 500f || MathF.Abs(by) > 500f || MathF.Abs(bz) > 500f) continue;
                if (bx == 0f && by == 0f && bz == 0f) continue; // all-zero is an unprogrammed page
                return new Vector3(bx, by, bz);
            }
            return null;
        }

        /// <summary>
        /// The 2-byte PID lives somewhere past the 8-byte response header + addr/length echo.
        /// Exact offset isn't well documented in ndeadly's notes — the safest read is to scan
        /// the small window where the data block could sit and accept the first match against
        /// the known Switch 2 PID set (SDL <c>usb_ids.h</c>).
        /// </summary>
        private static ushort? ExtractPidFromFlashResponse(byte[] bytes) {
            if (bytes.Length < 14) return null;
            for (int off = 8; off + 2 <= bytes.Length && off <= Math.Min(bytes.Length - 2, 24); off++) {
                ushort candidate = BitConverter.ToUInt16(bytes, off);
                if (candidate == 0x2066 || candidate == 0x2067 || candidate == 0x2068
                 || candidate == 0x2069 || candidate == 0x2073) {
                    return candidate;
                }
            }
            return null;
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
                _lastPacketLen = len;
                {
                    int from = 0x10, to = Math.Min(len, 0x40);
                    var sb = new System.Text.StringBuilder((to - from) * 3);
                    for (int i = from; i < to; i++) sb.Append(bytes[i].ToString("X2")).Append(' ');
                    _lastPacketHex = sb.ToString();
                }

                // Buttons at byte 0x04..0x07 per ndeadly hid_reports.md. Home (0x05 bit 0x10)
                // and Capture (0x05 bit 0x20) both trigger RESET_FULL on press-edge. Fire-and-
                // forget — any BLE glitch during the send is safe to swallow.
                if (len > 0x05) {
                    bool homeNow = (bytes[0x05] & 0x10) != 0;
                    bool captureNow = (bytes[0x05] & 0x20) != 0;

                    if (homeNow && !_homeHeld) {
                        _homeHeld = true;
                        try { _ = _udpHandler?.SendButton(FirmwareConstants.UserActionType.RESET_FULL); } catch { }
                    } else if (!homeNow) {
                        _homeHeld = false;
                    }

                    if (captureNow && !_captureHeld) {
                        _captureHeld = true;
                        try { _ = _udpHandler?.SendButton(FirmwareConstants.UserActionType.RESET_FULL); } catch { }
                    } else if (!captureNow) {
                        _captureHeld = false;
                    }
                }

                // IMU: int16 LE at documented offsets (motion timestamp 0x2A, accel 0x30, gyro 0x36).
                // Axis: identity passthrough. Joy2Win's (-x,-z,+y) remap was for DSU/gamepad
                // orientation; for VQF body-frame (Z-up, gravity on +Z at rest) we need the
                // chip's native frame. Tester data confirmed chip's +raw_z is physical up,
                // so identity gives gravity on +output.Z as VQF expects.
                short axRaw = BitConverter.ToInt16(bytes, 0x30);
                short ayRaw = BitConverter.ToInt16(bytes, 0x32);
                short azRaw = BitConverter.ToInt16(bytes, 0x34);
                short gxRaw = BitConverter.ToInt16(bytes, 0x36);
                short gyRaw = BitConverter.ToInt16(bytes, 0x38);
                short gzRaw = BitConverter.ToInt16(bytes, 0x3A);

                _accel = new Vector3(axRaw * AccelScaleMsPerUnit,
                                     ayRaw * AccelScaleMsPerUnit,
                                     azRaw * AccelScaleMsPerUnit);
                _gyroRad = new Vector3(gxRaw * GyroScaleRadSecPerUnit,
                                       gyRaw * GyroScaleRadSecPerUnit,
                                       gzRaw * GyroScaleRadSecPerUnit);
                // User-tunable per-MAC gyro trim (Joy-Con 2 has no factory cal we can read,
                // unlike the JSL controllers, so let the user nudge it manually if a specific
                // device drifts).
                float gyroTrim = Configuration.Instance?.GetGyroScaleTrim(_macSpoof) ?? 1.0f;
                if (gyroTrim != 1.0f) _gyroRad *= gyroTrim;

                // Magnetometer at 0x19 (3 × int16 LE per ndeadly hid_reports.md). Feature bit 7
                // is enabled by our 0xFF mask in CmdSensorInit/Start so the controller streams it.
                // Pipeline: raw int16 → µT via 0.15 scale → subtract factory hard-iron bias
                // (read from flash 0x13100) → validate against Earth-field window. Without the
                // bias subtraction, shell-embedded magnets / steel leave |M| ~3× Earth field and
                // the gate rejects every sample. Post-bias expected magnitude: ~25-65 µT.
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
                    // Runtime autocalibrate: fallback when factory flash read fails. Only accept
                    // samples while the controller is ACTIVELY ROTATING (gyro above threshold).
                    // Earth-field vector is constant in world frame, so averaging over rotated
                    // samples cancels it and leaves the body-frame hard-iron offset as the
                    // residual. A still-sample mean would subtract the Earth field too and
                    // leave VQF with |M| ≈ 0 — no heading reference. User must wave the
                    // controller in a figure-8 or shake for a few seconds during startup.
                    if (!_magBiasValid && _autoCalCount < AutoCalTargetSamples
                        && _gyroRad.Length() > AutoCalMinGyroRadSec) {
                        _autoCalSum += magUt;
                        _autoCalCount++;
                        if (_autoCalCount >= AutoCalTargetSamples) {
                            _magBias = _autoCalSum / AutoCalTargetSamples;
                            _magBiasValid = true;
                            _magBiasStatus = "autocal-ok";
                        }
                    }
                    if (_magBiasValid) magUt -= _magBias;
                    float magMag = magUt.Length();
                    // Always keep the last reading for debug display, even if gated out, so
                    // "|M|" on the debug page reflects the current frame instead of stale data.
                    _mag = magUt;
                    if (magMag > MagMinMagnitudeUt && magMag < MagMaxMagnitudeUt) {
                        _magValid = true;
                    }
                }

                // Battery voltage in mV at 0x1F (uint16 LE). Source: ndeadly pcap-verified docs
                // + Joycon2forMac (both say "mV, divide by 1000 for volts"). Joy2Win's raw/4095
                // formula appears empirically incorrect. Map Li-ion cell window 3000..4200 mV → 0..1.
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

                _imuSampleCount++;
                if (_magValid) {
                    _vqf.UpdateIdentity9D(_gyroRad, _accel, _mag);
                    _rotation = _vqf.GetQuat9DFast();
                    _magUsedSamples++;
                } else {
                    _vqf.UpdateIdentity(_gyroRad, _accel);
                    _rotation = _vqf.GetQuat6DFast();
                }
                // Jitter EWMA — same formula as JSL path so values are directly comparable
                // in the debug page.
                float qdot = Math.Clamp(Math.Abs(Quaternion.Dot(_rotation, _prevPublishedRotation)), 0f, 1f);
                float angleDeg = (float)(2.0 * Math.Acos(qdot) * 180.0 / Math.PI);
                _jitterEwmaDeg = _jitterEwmaDeg * 0.95f + angleDeg * 0.05f;
                _prevPublishedRotation = _rotation;

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
                // HMD-yaw fallback: if SteamVR is not running, GetHMDRotation returns a quat built
                // from a zero matrix → NaN. That would nuke the outbound packet. Detect via NaN
                // on _trackerEuler and fall back to publishedRotation so the tracker still works
                // standalone (no HMD) — SlimeVR body tracking without a headset is a supported flow.
                bool hmdYawValid = false;
                if (GenericTrackerManager.DebugOpen || _yawReferenceTypeValue != RotationReferenceType.TrustDeviceYaw) {
                    try {
                        var trackerRotation = OpenVRReader.GetTrackerRotation(_yawReferenceTypeValue);
                        _trackerEuler = trackerRotation.GetYawFromQuaternion();
                        hmdYawValid = !float.IsNaN(_trackerEuler) && !float.IsInfinity(_trackerEuler);
                    } catch { _trackerEuler = 0f; hmdYawValid = false; }
                    if (!hmdYawValid) _trackerEuler = 0f;
                    _lastEulerPosition = -_trackerEuler;
                    _euler = publishedRotation.QuaternionToEuler();
                }
                // Rotation + accel via SetSensorBundle. Internally gated on the server's
                // advertised PROTOCOL_BUNDLE_SUPPORT: uses BUNDLE (type 100) when the server
                // has replied to our FEATURE_FLAGS with that bit, otherwise falls back to
                // two separate sends. _accel already in m/s².
                var bundleRot = (_yawReferenceTypeValue == RotationReferenceType.TrustDeviceYaw || !hmdYawValid)
                    ? publishedRotation
                    : new Vector3(_euler.X, _euler.Y, _lastEulerPosition).ToQuaternion();
                await _udpHandler.SetSensorBundle(bundleRot, _accel, 0);

                if ((DateTime.UtcNow - _lastBatteryPush).TotalSeconds > 30) {
                    try { await _udpHandler.SetSensorBattery(_lastBatteryFraction * 100f, 3.7f); } catch { }
                    _lastBatteryPush = DateTime.UtcNow;
                }

                {
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
                        $"MagBias: X:{_magBias.X:F1}, Y:{_magBias.Y:F1}, Z:{_magBias.Z:F1}  (loaded:{_magBiasValid}, src:{_magBiasStatus})\r\n" +
                        $"Battery: {_lastBatteryFraction * 100f:F0}%\r\n" +
                        $"Pkt len: {_lastPacketLen}\r\n" +
                        $"Bytes[10..40]: {_lastPacketHex}\r\n";
                }
            } catch (Exception ex) {
                OnTrackerError?.Invoke(this, $"Publish: {ex.Message}");
            }
        }

        public Vector3 GetCalibration() => -_rotation.QuaternionToEuler();

        public async Task Recalibrate() {
            await Task.Delay(TrackerTimings.RecalibrateSettleMsBle);
            _rotationCalibration = GetCalibration();
            try { await _udpHandler.SendButton(FirmwareConstants.UserActionType.RESET_FULL); } catch { }
        }

        public void Rediscover() {
            try { _udpHandler?.Rehandshake(); } catch (Exception ex) { OnTrackerError?.Invoke(this, ex.Message); }
        }

        public void Identify() {
            // Single short vibration buzz — preset 1. CMD 0x0A is fire-and-forget; controller
            // returns to idle on its own after the preset finishes (~150 ms). We swallow
            // exceptions so a UI Identify click never crashes if the BLE link blipped.
            _ = SendCommandAsync(BuildRumblePreset(0x01));
        }

        public void HapticIntensityTest()
        {
            // Cycle through 3 preset levels with 400 ms gaps so the user can compare.
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (byte preset in new byte[] { 0x01, 0x03, 0x05 })
                    {
                        await SendCommandAsync(BuildRumblePreset(preset));
                        await Task.Delay(400);
                    }
                }
                catch { }
            });
        }

        public void EngageHaptics(int duration, float intensity)
        {
            // Joy-Con 2 simple presets are fixed-duration; pick one whose strength roughly
            // matches the requested intensity. HD Rumble would be needed for true variable
            // duration / amplitude. Future work.
            byte preset = intensity switch
            {
                >= 75f => 0x05, // paired vibration (strong)
                >= 30f => 0x03, // double pulse (medium)
                _ => 0x01,      // single pulse (light)
            };
            _ = SendCommandAsync(BuildRumblePreset(preset));
        }

        public void DisableHaptics()
        {
            // Presets self-terminate; no explicit stop needed. Send preset 0 for completeness
            // (documented as "nothing" so it's a safe no-op on the controller side).
            _ = SendCommandAsync(BuildRumblePreset(0x00));
        }

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
