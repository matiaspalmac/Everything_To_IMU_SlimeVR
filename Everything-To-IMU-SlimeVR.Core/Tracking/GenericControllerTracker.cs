using SlimeImuProtocol.SlimeVR;
using SlimeImuProtocol.Utility;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using static Everything_To_IMU_SlimeVR.TrackerConfig;

namespace Everything_To_IMU_SlimeVR.Tracking
{
    public class GenericControllerTracker : IDisposable, IBodyTracker
    {
        private string _debug;
        private int _index;
        private int _id;
        private string macSpoof;
        private SensorOrientation _sensorOrientation;
        private byte[] _macAddressBytes;
        private UDPHandler udpHandler;
        private Vector3 _rotationCalibration;
        private float _calibratedHeight;
        private bool _ready;
        private bool _disconnected;
        private bool _simulateThighs = true;
        private bool _useWaistTrackerForYaw;
        private bool _usingWiimoteKnees = true;
        private float _trackerEuler;
        private float _lastEulerPositon;
        private Quaternion _rotation;
        private Vector3 _euler;
        private Vector3 _gyro;
        private Vector3 _acceleration;
        private bool _waitForRelease;
        private string _rememberedStringId;
        private RotationReferenceType _yawReferenceTypeValue;
        Stopwatch buttonPressTimer = new Stopwatch();
        private HapticNodeBinding _hapticNodeBinding;
        private bool isAlreadyVibrating;
        private bool identifying;
        private bool updatingAlready;
        private RotationReferenceType _extensionYawReferenceTypeValue;

        public bool SupportsHaptics => true;
        public bool SupportsIMU => true;

        // Protocol telemetry surfaced to UI (DebugPage). Values come from UDPHandler counters.
        public long PacketsSent => udpHandler?.PacketsSent ?? 0;
        public long SendFailures => udpHandler?.SendFailures ?? 0;
        public bool ServerReachable => udpHandler?.ServerReachable ?? false;

        // Battery as 0..1 (fallback to 1.0 if the DLL doesn't expose the export)
        private float _lastBatteryFraction = 1f;
        private DateTime _lastBatteryPush = DateTime.MinValue;
        public float BatteryLevel => _lastBatteryFraction;

        // Per-tracker sample rate: incremented on each Update call, snapshot once per second.
        private long _sampleCount;
        private DateTime _lastRateWindow = DateTime.UtcNow;
        private double _lastRate;
        public double Hz
        {
            get
            {
                var elapsed = (DateTime.UtcNow - _lastRateWindow).TotalSeconds;
                if (elapsed >= 1.0)
                {
                    _lastRate = _sampleCount / elapsed;
                    _sampleCount = 0;
                    _lastRateWindow = DateTime.UtcNow;
                }
                return _lastRate;
            }
        }

        // LED state sync: tracker health reflected on the controller's own light bar.
        private int _lastLedArgb = unchecked((int)0xFF808080);
        private DateTime _lastLedPush = DateTime.MinValue;
        public int LastLedArgb => _lastLedArgb;

        // Jitter EWMA: average angular delta between consecutive published quaternions, in
        // degrees. At rest a healthy tracker should sit well below 0.1°; higher values under
        // stationary conditions indicate either magnetometer interference (if 9DoF), high
        // sensor noise, or an unstable BLE link. Tau ≈ 20 samples.
        private Quaternion _prevPublishedRotation = Quaternion.Identity;
        private float _jitterEwmaDeg;
        public float JitterDegrees => _jitterEwmaDeg;

        // Raw IMU rate = JSL callbacks/sec *before* our send-rate cap. More useful diagnostic
        // than the capped Hz — tells us whether the underlying HID/USB stream is delivering
        // at the chip's rated rate. Compared to Hz (capped at 200), ImuSampleRate reveals
        // when the controller is under-polling (e.g. bad Bluetooth link → 60 Hz instead of
        // expected 250).
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

        public event EventHandler<string> OnTrackerError;
        Stopwatch holdTimer = new Stopwatch();
        private DateTime _hapticEndTime;
        private bool waitingForButton1Release;
        private bool waitingForButton2Release;
        private bool waitingForButton3Release;

        public GenericControllerTracker(int index, Color colour)
        {
            Initialize(index, colour);
        }
        public async void Initialize(int index, Color colour)
        {
            Task.Run(async () =>
            {
                try
                {
                    _index = index;
                    _id = index + 1;
                    var rememberedColour = colour;
                    int ctype = JSL.JslGetControllerType(index);
                    _rememberedStringId = index + " " + ctype;
                    string friendlyType = ctype switch
                    {
                        1 => "Joy-Con L",
                        2 => "Joy-Con R",
                        3 => "Switch Pro",
                        4 => "DualShock 4",
                        5 => "DualSense",
                        _ => "Controller"
                    };
                    string friendlyId = $"{friendlyType} #{index + 1}";
                    JSL.JslSetLightColour(index, colour.ToArgb());
                    macSpoof = _rememberedStringId + "GenericController";
                    _sensorOrientation = new SensorOrientation(index, SensorOrientation.SensorType.Bluetooth);
                    _sensorOrientation.NewData += (_, _) => { _ = Update(); };
                    // Deterministic 6-byte MAC from SHA256(ctype:index). Locally-administered bit set
                    // (bit 1 of first byte = 1), unicast (bit 0 = 0). Stable across runs so SlimeVR
                    // keeps body-slot assignments even if user plugs controller back in later.
                    var hash = System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes($"EverythingToIMU:{ctype}:{index}"));
                    _macAddressBytes = new byte[6];
                    Array.Copy(hash, 0, _macAddressBytes, 0, 6);
                    _macAddressBytes[0] = (byte)((_macAddressBytes[0] & 0xFE) | 0x02);
                    // DualSense (PS5) is widely reported as a Bosch BMI270-class 6DoF (no
                    // public teardown ID, but the firmware-exposed scale ±2000 dps + ±4G with
                    // 8192 LSB/g matches BMI270's RANGE_2000 | ACC_RANGE_4G config). DualShock 4
                    // is Bosch BMI055 per multiple community sources — SlimeVR's ImuType enum
                    // does not list BMI055, so map DS4 to BMI160 (same Bosch family, closest
                    // dashboard label) instead of misreporting it as BMI270. Switch family is
                    // ST LSM6DS3 verified. None of these have a magnetometer.
                    var imuHint = ctype switch
                    {
                        1 or 2 or 3 => FirmwareConstants.ImuType.LSM6DS3TRC, // Switch L/R/Pro
                        4 => FirmwareConstants.ImuType.BMI160,               // DualShock 4 (BMI055, label as BMI160)
                        5 => FirmwareConstants.ImuType.BMI270,               // DualSense
                        _ => FirmwareConstants.ImuType.UNKNOWN
                    };
                    // Firmware string becomes "Current" field in SlimeVR dashboard. Server also
                    // compares it to latest GitHub release version for the "Update now" prompt.
                    // Pass semver matching current SlimeVR firmware release so the prompt is
                    // suppressed, append short controller hint for identification in logs.
                    string firmwareString = $"0.7.2-EverythingToIMU-{friendlyType.Replace(" ", "")}";
                    udpHandler = new UDPHandler(firmwareString, _macAddressBytes,
                        FirmwareConstants.BoardType.CUSTOM, imuHint, FirmwareConstants.McuType.UNKNOWN,
                        FirmwareConstants.MagnetometerStatus.NOT_SUPPORTED, 1);
                    udpHandler.Active = true;
                    // Note on resets: SlimeVR server handles Reset/Full/Mounting entirely server-side.
                    // It maintains its own offset against our raw rotation stream — trackers do NOT
                    // receive a server→tracker "you should reset" packet. We only SEND
                    // CALIBRATION_ACTION (type 21) when the user presses a physical reset button
                    // on the controller (see Recalibrate / CheckControllerInputs). Applying a
                    // second local offset in response to a server packet would double-offset.
                    Recalibrate();
                    _sensorOrientation.OnExceptionMessage += _sensorOrientation_OnExceptionMessage;
                    _ready = true;
                }
                catch (Exception e)
                {
                    OnTrackerError?.Invoke(this, e.Message);
                }
            });
        }

        private void _sensorOrientation_OnExceptionMessage(object? sender, string e)
        {
            OnTrackerError?.Invoke(sender, e);
        }

        public bool GetGlobalState(int code)
        {
            int connections = GenericTrackerManager.ControllerCount;
            for (int i = 0; i < connections; i++)
            {
                var buttons = JSL.JslGetSimpleState(i).buttons;
                if ((buttons & code) != 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasButton(JSL.JOY_SHOCK_STATE state, int code) => (state.buttons & code) != 0;

        // Per-tracker send rate cap: DualSense IMU fires at ~250 Hz, but SlimeVR firmware
        // itself sends at ~100-150 Hz. Anything higher floods the shared UdpClient send queue
        // and under GC pauses / socket buffer stalls we can exceed the server's ~3 s timeout
        // window, making the tracker appear "disconnected" intermittently.
        private long _lastSendTicks;
        // 5ms = 200 Hz. DualSense IMU nominal ~250 Hz — decimating from 250→200 keeps <1 sample
        // variance. Previously 8ms (125 Hz) decimated aggressively. Server accepts higher rates
        // fine; only issue was UdpClient flooding under GC pauses, mitigated by our rate cap +
        // ValueTask hot path + SIO_UDP_CONNRESET fix.
        private static readonly long SendMinIntervalTicks = TimeSpan.TicksPerMillisecond * 5;

        public async Task<bool> Update()
        {
            if (!_ready) return false;
            if (updatingAlready) return true;
            // Hard rate cap before reentrancy flag so we don't even allocate the state machine.
            var nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - _lastSendTicks < SendMinIntervalTicks) return true;
            _lastSendTicks = nowTicks;
            updatingAlready = true;
            _sampleCount++;
            try
            {
                if (udpHandler == null) { updatingAlready = false; return false; }
                _rotation = Quaternion.Normalize(_sensorOrientation.CurrentOrientation);
                // Apply user-persisted mount yaw (0/90/180/270) so the physical controller can
                // be strapped in any 90° orientation and the configured offset follows the MAC
                // across reconnects.
                var mountYaw = Configuration.Instance?.GetMountYawQuaternion(macSpoof) ?? Quaternion.Identity;
                var publishedRotation = Quaternion.Normalize(mountYaw * _rotation);
                // Jitter EWMA: angle between this publish and the last one. At rest this is
                // the noise floor; under motion it spikes but the EWMA averages down again.
                _imuSampleCount++;
                float qdot = Math.Clamp(Math.Abs(Quaternion.Dot(publishedRotation, _prevPublishedRotation)), 0f, 1f);
                float angleDeg = (float)(2.0 * Math.Acos(qdot) * 180.0 / Math.PI);
                _jitterEwmaDeg = _jitterEwmaDeg * 0.95f + angleDeg * 0.05f;
                _prevPublishedRotation = publishedRotation;
                if (GenericTrackerManager.DebugOpen || _yawReferenceTypeValue != RotationReferenceType.TrustDeviceYaw)
                {
                    var trackerRotation = OpenVRReader.GetTrackerRotation(YawReferenceTypeValue);
                    _trackerEuler = trackerRotation.GetYawFromQuaternion();
                    _lastEulerPositon = -_trackerEuler;
                    _euler = publishedRotation.QuaternionToEuler();
                    _gyro = _sensorOrientation.Gyro;
                    _acceleration = _sensorOrientation.Accelerometer;
                }
                // SlimeVR's ACCELERATION packet expects m/s² (matches the SlimeVR-Tracker-ESP
                // firmware contract). _sensorOrientation.Accelerometer holds JSL's `g` value
                // multiplied by 10 (see SensorOrientation.cs line 80, legacy from pre-fork
                // code). Multiplying by 0.980665 collapses ÷10 (back to g) × 9.80665 (g→m/s²)
                // into one step. Old code sent raw g, which the server treated as m/s² and
                // therefore saw linear acceleration ~9.8× too small — explains why fast
                // direction changes felt mushy in the skeleton solver.
                await udpHandler.SetSensorAcceleration(_sensorOrientation.Accelerometer * 0.980665f, 0);
                if (_yawReferenceTypeValue == RotationReferenceType.TrustDeviceYaw)
                {
                    await udpHandler.SetSensorRotation(publishedRotation, 0);
                }
                else
                {
                    await udpHandler.SetSensorRotation(new Vector3(_euler.X, _euler.Y, _lastEulerPositon).ToQuaternion(), 0);
                }
                if (GenericTrackerManager.DebugOpen)
                {
                    _debug =
                    $"Device Id: {macSpoof}\r\n" +
                    $"Euler Rotation:\r\n" +
                    $"X:{_euler.X}, Y:{_euler.Y}, Z:{_rotation.Z}" +
                    $"\r\nGyro:\r\n" +
                    $"X:{_gyro.X}, Y:{_gyro.Y}, Z:{_gyro.Z}" +
                    $"\r\nAcceleration:\r\n" +
                    $"X:{_acceleration.X}, Y:{_acceleration.Y}, Z:{_acceleration.Z}\r\n" +
                    $"Yaw Reference Rotation:\r\n" +
                    $"Y:{_trackerEuler}\r\n";
                }
                // Battery: JSL has no battery API, read directly via HID. Cached 30s internally.
                if ((DateTime.UtcNow - _lastBatteryPush).TotalSeconds > 30)
                {
                    var fraction = HidBatteryReader.GetBatteryFraction(_index);
                    if (fraction is float f)
                    {
                        _lastBatteryFraction = f;
                        try
                        {
                            await udpHandler.SetSensorBattery(_lastBatteryFraction * 100f, 3.7f);
                        }
                        catch { /* best-effort */ }
                    }
                    _lastBatteryPush = DateTime.UtcNow;
                }

                // LED state sync: reflect app health on the controller's own light bar.
                // Throttled to 1 Hz (the HID feature report is not cheap and the colour change
                // is only meaningful on visible state transitions).
                if ((DateTime.UtcNow - _lastLedPush).TotalMilliseconds > 1000)
                {
                    int target = ComputeLedColor();
                    if (target != _lastLedArgb)
                    {
                        try { JSL.JslSetLightColour(_index, target); _lastLedArgb = target; } catch { }
                    }
                    _lastLedPush = DateTime.UtcNow;
                }

                var state = JSL.JslGetSimpleState(_index);
                if (HasButton(state, 0x20000))
                {
                    if (!_waitForRelease)
                    {
                        buttonPressTimer.Start();
                        _waitForRelease = true;
                    }
                }
                else
                {
                    _waitForRelease = false;
                    buttonPressTimer.Reset();
                }
                if (buttonPressTimer.ElapsedMilliseconds >= 3000)
                {
                    buttonPressTimer.Reset();
                }
                CheckControllerInputs(state);
            }
            catch (Exception e)
            {
                OnTrackerError?.Invoke(this, e.StackTrace + "\r\n" + e.Message);
            }
            finally
            {
                updatingAlready = false;
            }
            return _ready;
        }
        public async void Recalibrate()
        {
            await Task.Delay(5000);
            _calibratedHeight = OpenVRReader.GetHMDHeight();
            _rotationCalibration = GetCalibration();
            await udpHandler.SendButton(FirmwareConstants.UserActionType.RESET_FULL);
        }
        public void Rediscover()
        {
            // Forces the UDPHandler's internal watchdog loop to re-run the handshake.
            try { udpHandler?.Rehandshake(); }
            catch (Exception ex) { OnTrackerError?.Invoke(this, ex.Message); }
        }

        public void Dispose()
        {
            _ready = false;
            _disconnected = true;
            // Properly release UDP + sensor subscriptions so re-enumeration doesn't leak
            // sockets or fire events on a dead tracker (which would NRE through _udpHandler).
            try { _sensorOrientation?.Dispose(); } catch { }
            try { udpHandler?.Dispose(); } catch { }
            udpHandler = null!;
        }

        public Vector3 GetCalibration()
        {
            return -(_sensorOrientation.CurrentOrientation).QuaternionToEuler();
        }

        /// <summary>
        /// Returns an ARGB colour that encodes the current tracker state for the controller's
        /// light bar. Gray = connecting, amber = needs calibration, indigo = streaming, red = low battery.
        /// </summary>
        private int ComputeLedColor()
        {
            if (!_ready) return unchecked((int)0xFF606060); // dim gray = connecting
            if (_lastBatteryFraction > 0f && _lastBatteryFraction < 0.15f)
                return unchecked((int)0xFFED6A5A); // red = low battery (shared VizX brand)
            // Streaming state: indigo if calibrated, amber if not.
            // We don't have direct calibration-done visibility here, so use a simple proxy:
            // if udpHandler is active and ready flag set, treat as streaming.
            return unchecked((int)0xFF6E8CF0); // indigo = streaming healthy
        }

        public void CheckControllerInputs(JSL.JOY_SHOCK_STATE state)
        {
            // Trigger
            if (HasButton(state, 0x00100) || HasButton(state, 0x00200))
            {
                udpHandler.SendTrigger(1, 0);
            }
            // Grip
            if (HasButton(state, 0x20000))
            {
                udpHandler.SendGrip(1, 0);
            }
            // B1
            if (HasButton(state, 0x04000) || HasButton(state, 0x00008))
            {
                if (!waitingForButton1Release)
                {
                    udpHandler.SendControllerButton(FirmwareConstants.ControllerButton.BUTTON_1_HELD, 0);
                    waitingForButton1Release = true;
                }
            }
            else
            {
                if (waitingForButton1Release)
                {
                    udpHandler.SendControllerButton(FirmwareConstants.ControllerButton.BUTTON_1_UNHELD, 0);
                    waitingForButton1Release = false;
                }
            }
            // B2
            if (HasButton(state, 0x01000) || HasButton(state, 0x00002))
            {
                if (!waitingForButton2Release)
                {
                    udpHandler.SendControllerButton(FirmwareConstants.ControllerButton.BUTTON_2_HELD, 0);
                    waitingForButton2Release = true;
                }
            }
            else
            {
                if (waitingForButton2Release)
                {
                    udpHandler.SendControllerButton(FirmwareConstants.ControllerButton.BUTTON_2_UNHELD, 0);
                    waitingForButton2Release = false;
                }
            }
            // Menu/Recenter
            if (HasButton(state, 0x00020) || HasButton(state, 0x00010))
            {
                if (!waitingForButton3Release)
                {
                    udpHandler.SendControllerButton(FirmwareConstants.ControllerButton.MENU_RECENTER_HELD, 0);
                    waitingForButton3Release = true;
                }
            }
            else
            {
                if (waitingForButton3Release)
                {
                    udpHandler.SendControllerButton(FirmwareConstants.ControllerButton.MENU_RECENTER_UNHELD, 0);
                    waitingForButton3Release = false;
                }
            }
            // Thumbstick L/R
            float x = MathF.Abs(state.stickLX) > MathF.Abs(state.stickRX) ? state.stickLX * -1 : state.stickRX;
            float y = MathF.Abs(state.stickLY) > MathF.Abs(state.stickRY) ? state.stickLY : state.stickRY * -1;
            udpHandler.SetThumbstick(new Vector2(y, x), 0);
        }

        public void Identify()
        {
            identifying = true;
            EngageHaptics(300, 100);
            identifying = false;
        }

        public void EngageHaptics(int duration, float intensity)
        {
            _hapticEndTime = DateTime.Now.AddMilliseconds(duration);
            if (!isAlreadyVibrating)
            {
                isAlreadyVibrating = true;
                _ = Task.Run(async () =>
                {
                    JSL.JslSetRumble(_index, (int)(100 * intensity), (int)(intensity * 100f));
                    while (DateTime.Now < _hapticEndTime)
                    {
                        await Task.Delay(10);
                    }
                    JSL.JslSetRumble(_index, 0, 0);
                    isAlreadyVibrating = false;
                    identifying = false;
                });
            }
        }

        public void DisableHaptics()
        {
            if (!identifying)
            {
                isAlreadyVibrating = false;
                JSL.JslSetRumble(_index, 0, 0);
            }
        }

        public override string ToString()
        {
            try
            {
                int ctype = JSL.JslGetControllerType(_index);
                string friendlyType = ctype switch
                {
                    1 => "Joy-Con L",
                    2 => "Joy-Con R",
                    3 => "Switch Pro",
                    4 => "DualShock 4",
                    5 => "DualSense",
                    _ => "Controller"
                };
                return $"{friendlyType} #{_index + 1}";
            }
            catch
            {
                return "Controller " + _index;
            }
        }

        public void HapticIntensityTest()
        {
            //throw new NotImplementedException();
        }

        public string Debug { get => _debug; set => _debug = value; }
        public bool Ready { get => _ready; set => _ready = value; }
        public bool Disconnected { get => _disconnected; set => _disconnected = value; }
        public int Id { get => _id; set => _id = value; }
        public string MacSpoof { get => macSpoof; set => macSpoof = value; }
        public Vector3 Euler { get => _euler; set => _euler = value; }
        public Vector3 Gyro { get => _gyro; set => _gyro = value; }
        public Vector3 Acceleration { get => _acceleration; set => _acceleration = value; }
        public float LastHmdPositon { get => _lastEulerPositon; set => _lastEulerPositon = value; }
        public bool SimulateThighs { get => _simulateThighs; set => _simulateThighs = value; }
        public bool UseWaistTrackerForYaw { get => _useWaistTrackerForYaw; set => _useWaistTrackerForYaw = value; }
        public RotationReferenceType YawReferenceTypeValue { get => _yawReferenceTypeValue; set => _yawReferenceTypeValue = value; }
        public bool UsingWiimoteKnees { get => _usingWiimoteKnees; set => _usingWiimoteKnees = value; }
        public HapticNodeBinding HapticNodeBinding { get => _hapticNodeBinding; set => _hapticNodeBinding = value; }
        public RotationReferenceType ExtensionYawReferenceTypeValue { get => _extensionYawReferenceTypeValue; set => _extensionYawReferenceTypeValue = value; }
        public Vector3 RotationCalibration { get => _rotationCalibration; set => _rotationCalibration = value; }
    }
}