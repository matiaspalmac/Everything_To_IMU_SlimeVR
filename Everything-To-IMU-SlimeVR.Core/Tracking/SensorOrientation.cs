using SlimeImuProtocol.Utility;
using System.Diagnostics;
using System.Numerics;
using static JSL;

namespace Everything_To_IMU_SlimeVR.Tracking {
    internal class SensorOrientation : IDisposable {
        private Quaternion currentOrientation = Quaternion.Identity;
        private float _deltaTime = 0.02f; // Example time step (e.g., 50Hz)
        private float _previousTime;
        private bool _useCustomFusion;

        // Complementary filter parameters
        private float alpha = 0.98f; // Weighting factor for blending
        private IBodyTracker _bodyTracker;
        private int _index;
        private SensorType _sensorType;
        Stopwatch stopwatch = new Stopwatch();
        bool _calibratedRotation = false;
        private bool disposed;
        private Vector3 _magnetometer;
        private float gyroYawRate;
        private float gyroDriftCompensation;
        private float yawRadians;
        private float yawDegrees;
        private Vector3 _accelerometer;
        private Vector3 _gyro;
        private byte _battery;
        private VQFWrapper _vqf;
        private bool _jslStillnessEnabled;
        // Warm-up: suppress orientation emission until VQF has had stationary samples to
        // converge. Prevents the "spin on connect" where downstream sees the first wildly-
        // wrong quaternion before the accelerometer has tilted it down. Threshold ~8.6°/s
        // (squared in rad/s) — generous, picks up clear hand motion without false-tripping
        // on sensor noise.
        //
        // 50 ms is the floor used for Sony pads (factory cal subtracts ZRL up front) and JC2
        // (clean BLE IMU with negligible bias). JC1 over the HID path needs the full 200 ms
        // because SPI cal can fail on busy buses or clones — in that case GyroBiasCalibrator
        // is the only line of defence and it needs ~1.5 s of stillness, but warming up the
        // accel-tilt portion of VQF still benefits from the longer 200 ms gate.
        private static readonly long WarmupTicksRequiredFast = Stopwatch.Frequency / 20;
        private static readonly long WarmupTicksRequiredSlow = Stopwatch.Frequency / 5;
        private long _warmupTicksRequired = WarmupTicksRequiredFast;
        private const float WarmupGyroStationaryThresholdSq = 0.0225f; // (0.15 rad/s)^2
        private long _warmupStableStartTicks = -1;
        private bool _warmupComplete = false;
        private readonly GyroBiasCalibrator _biasCal = new GyroBiasCalibrator();
        public bool GyroBiasCalibrated => _biasCal.HasBias;
        public Vector3 GyroBias => _biasCal.Bias;
        // Single lock guarding _vqf state. HID reader thread (200 Hz) and JSL callback
        // thread can both race on _vqf.UpdateFast / _accelerometer / _gyro under high tracker
        // counts (4 JC1 + phone observed) — VQF's native side is not thread-safe.
        private readonly object _vqfLock = new object();
        private JSL.EventCallback _callback;
        // Pin the one callback we register with JSL so native code never ends up with a
        // dangling function pointer if the first instance is disposed.
        private static JSL.EventCallback? _pinnedCallback;
        List<float> averageSampleTicks = new List<float>();
        private static bool jslHandlerSet = false;

        public Quaternion CurrentOrientation { get => currentOrientation; set => currentOrientation = value; }
        public float YawRadians { get => yawRadians; set => yawRadians = value; }
        public float YawDegrees { get => yawDegrees; set => yawDegrees = value; }
        public Quaternion AXES_OFFSET { get; internal set; }
        public Vector3 Accelerometer { get => _accelerometer; set => _accelerometer = value; }
        public Vector3 Gyro { get => _gyro; set => _gyro = value; }
        public event EventHandler NewData;

        public static event EventHandler<Tuple<int, JSL.JOY_SHOCK_STATE, JSL.JOY_SHOCK_STATE, JSL.IMU_STATE, JSL.IMU_STATE, float>> OnNewJSLData;
        public event EventHandler<string> OnExceptionMessage;

        public SensorOrientation(int index, SensorType sensorType) {
            // Apply AXES_OFFSET * rot
            float angle = -MathF.PI / 2;

            AXES_OFFSET = Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle);
            _index = index;
            _sensorType = sensorType;
            stopwatch.Start();
            if (!jslHandlerSet) {
                _callback = new JSL.EventCallback(OnControllerEvent);
                _pinnedCallback = _callback; // keep alive for native code
                jslHandlerSet = true;
                JSL.JslSetCallback(_callback);
            }
            OnNewJSLData += SensorOrientation_OnNewJSLData;
            JoyCon1HidImuReader.SampleReady += OnJoyCon1HidSample;
        }

        // Joy-Con 1 / Switch Pro IMU samples coming from our parallel HID reader. Each JSL
        // packet at ~67 Hz carries 3 samples 5 ms apart that JSL itself drops 2 of; this
        // path catches all 3 and feeds them through VQF at the chip's true 200 Hz rate.
        private bool _hidImuReaderTried;
        private bool _hidImuReaderActive;
        private void OnJoyCon1HidSample(object? sender, JoyCon1HidImuReader.Sample s)
        {
            if (s.JslIndex != _index || disposed) return;
            try
            {
                int ctype = JSL.JslGetControllerType(_index);
                var rawAccel = s.AccelG * 10f; // match JSL-pipeline scaling convention
                var rawGyroRad = s.GyroDps.ConvertDegreesToRadians();
                if (ctype == 2)
                {
                    rawAccel = new Vector3(rawAccel.X, -rawAccel.Y, -rawAccel.Z);
                    rawGyroRad = new Vector3(rawGyroRad.X, -rawGyroRad.Y, -rawGyroRad.Z);
                }

                // Online gyro bias correction. Factory cal in JoyCon1SpiCalibration handles
                // the per-unit static ZRL but there's still a small residual + thermal drift
                // that walks the yaw on long sessions (the "ankle ticks down constantly"
                // symptom). _biasCal samples gyro mean during stillness and subtracts on the
                // hot path. Sample takes accel in m/s² — JSL convention is g×10 so that's
                // already m/s² up to a constant factor, accurate enough for the variance gate.
                _biasCal.AddSample(rawAccel, rawGyroRad);
                rawGyroRad = _biasCal.Correct(rawGyroRad);

                // VQF was constructed at JSL's 67 Hz cadence, but we now feed it at ~200 Hz.
                // The dt mismatch is small (~3×) and VQF's tau-based filtering tolerates it
                // far better than skipping the 2 extra samples. A full reinit at 5 ms dt is
                // possible but means losing the rest-bias estimate VQF has already built; the
                // current trade favours preserving that estimate.
                lock (_vqfLock)
                {
                    if (_vqf == null) return;
                    _accelerometer = rawAccel;
                    _gyro = rawGyroRad;
                    Update();
                }
            }
            catch (Exception ex)
            {
                OnExceptionMessage?.Invoke(this, ex.Message + ":" + ex.StackTrace);
            }
        }

        private void SensorOrientation_OnNewJSLData(object? sender, Tuple<int, JSL.JOY_SHOCK_STATE, JSL.JOY_SHOCK_STATE, JSL.IMU_STATE, JSL.IMU_STATE, float> e) {
            try {
                if (e.Item1 == _index) {
                    int ctype = JSL.JslGetControllerType(_index);

                    // Joy-Con 1 / Switch Pro: try to attach our parallel HID reader once. If
                    // it succeeds, we get all 3 samples per HID packet via OnJoyCon1HidSample
                    // and JSL's IMU values become redundant — skip them here so we don't
                    // double-feed VQF.
                    if ((ctype == 1 || ctype == 2 || ctype == 3) && !_hidImuReaderTried)
                    {
                        _hidImuReaderTried = true;
                        _hidImuReaderActive = JoyCon1HidImuReader.TryStart(_index);
                        // JC1 HID path bypasses JSL's calibrated stream — SPI cal is best
                        // effort and may fail on a busy bus or a clone. Stretch warm-up to
                        // 200 ms so VQF's accel-tilt portion settles even on uncalibrated
                        // input, and so GyroBiasCalibrator gets at least one stillness
                        // window in before downstream sees the first quaternion.
                        if (_hidImuReaderActive) _warmupTicksRequired = WarmupTicksRequiredSlow;
                        // JSL's stillness-based bias estimator (gamepad-motion-helpers
                        // auto-cal) was previously gated to Sony pads only — Switch family
                        // got nothing on top of factory cal. That left every JC1 / Pro
                        // running on whatever residual bias VQF could learn, which on long
                        // sessions walks the yaw (the "ankle ticks down" symptom). Enabling
                        // it for ctype 1/2/3 too is safe even when our HID path also has
                        // GyroBiasCalibrator on top — both estimators converge to zero when
                        // the static cal is already correct.
                        if (!_jslStillnessEnabled)
                        {
                            _jslStillnessEnabled = true;
                            try { JSL.JslSetAutomaticCalibration(_index, true); } catch { }
                        }
                    }
                    if (_hidImuReaderActive) return;

                    var rawAccel = new Vector3(e.Item4.accelX, e.Item4.accelY, e.Item4.accelZ) * 10;
                    var rawGyroRad = (new Vector3(e.Item4.gyroX, e.Item4.gyroY, e.Item4.gyroZ)).ConvertDegreesToRadians();

                    // Controller-type aware axis remap: Right Joy-Con (ctype=2) IMU is mounted
                    // rotated 180° around X vs Left, so Y+Z are physically inverted. Linux
                    // hid-nintendo applies the same fix. Must be done before VQF and before
                    // bias calibration so the bias estimate lives in the corrected frame.
                    if (ctype == 2) // Joy-Con R
                    {
                        rawAccel = new Vector3(rawAccel.X, -rawAccel.Y, -rawAccel.Z);
                        rawGyroRad = new Vector3(rawGyroRad.X, -rawGyroRad.Y, -rawGyroRad.Z);
                    }

                    // Sony factory cal: JSL does not parse feature report 0x05/0x02 for
                    // DualShock 4 / DualSense (only the Switch family gets factory cal
                    // applied internally). Apply here — bias + per-axis sensitivity for gyro,
                    // bias + per-axis sensitivity for accel — all in JSL's already-converted
                    // dps and g domains. ctype 4 = DS4, 5 = DualSense. Wii/3DS/Joy-Con use
                    // their own pipelines.
                    if (ctype == 4 || ctype == 5)
                    {
                        var cal = SonyImuCalibration.GetCalibration(_index);
                        if (cal.Valid)
                        {
                            // Gyro: subtract ZRL bias, then per-axis sens trim (unitless ≈1.0).
                            var biasRad = cal.GyroBiasDps.ConvertDegreesToRadians();
                            rawGyroRad = (rawGyroRad - biasRad) * cal.GyroScale;

                            // Accel: rawAccel is JSL's g value × 10 (legacy scaling from line
                            // below). Bias is in g, so multiply by 10 to match before
                            // subtracting. Scale is dimensionless.
                            var biasAccelScaled = cal.AccelBiasG * 10f;
                            rawAccel = (rawAccel - biasAccelScaled) * cal.AccelScale;
                        }

                        // JSL ships with a stillness-based bias estimator (GamepadMotion's
                        // auto-cal path) that's off by default. Turn it on once per device
                        // so drift over long sessions is corrected on top of the static
                        // factory bias we just applied. Safe to enable even when our cal
                        // succeeded — JSL's estimator converges to zero if the static bias
                        // is already correct.
                        if (!_jslStillnessEnabled)
                        {
                            _jslStillnessEnabled = true;
                            try { JSL.JslSetAutomaticCalibration(_index, true); } catch { }
                        }
                    }

                    // Online gyro bias correction. For Sony pads SonyImuCalibration already
                    // subtracted factory ZRL above; for Switch (ctype 1/2/3) JSL applies its
                    // own SPI cal internally before delivering this sample. _biasCal layers
                    // an online stillness-based bias estimate on top to catch residual / thermal
                    // drift that even factory cal can't predict. The earlier "moving forward =
                    // tracker backward" artefact came from also tweaking SetTauAcc(1.5) which
                    // let accel dominate tilt during linear motion; with VQF defaults left
                    // alone, the bias subtraction is safe and converges to zero where unneeded.
                    _biasCal.AddSample(rawAccel, rawGyroRad);
                    rawGyroRad = _biasCal.Correct(rawGyroRad);

                    lock (_vqfLock)
                    {
                        _accelerometer = rawAccel;
                        _gyro = rawGyroRad;

                        if (_vqf == null) {
                            if (averageSampleTicks.Count < 1000) {
                                averageSampleTicks.Add(e.Item6);
                            } else {
                                _vqf = new VQFWrapper(averageSampleTicks.Average());
                                averageSampleTicks.Clear();
                            }
                        } else {
                            Update();
                        }
                    }
                }
            } catch (Exception ex) {
                OnExceptionMessage?.Invoke(this, ex.Message + ":" + ex.StackTrace);
            }
        }

        static void OnControllerEvent(int deviceId, JSL.JOY_SHOCK_STATE state, JSL.JOY_SHOCK_STATE state2, JSL.IMU_STATE imuState, JSL.IMU_STATE imuState2, float delta) {
            try {
                OnNewJSLData?.Invoke(new object(), new Tuple<int, JOY_SHOCK_STATE, JOY_SHOCK_STATE, IMU_STATE, IMU_STATE, float>(deviceId, state, state2, imuState, imuState2, delta));
            } catch (Exception ex) {
            }
        }

        public enum SensorType {
            Bluetooth = 0,
            ThreeDs = 1,
            Wiimote = 2,
            Nunchuck = 3
        }
        // Update method to simulate gyroscope and accelerometer data fusion
        public void Update() {
            try {
                if (!disposed) {
                    switch (_sensorType) {
                        case SensorType.Bluetooth:
                            _vqf.UpdateFast(_gyro, _accelerometer);
                            currentOrientation = _vqf.GetQuat6DFast();
                            if (!_warmupComplete) {
                                long now = stopwatch.ElapsedTicks;
                                if (_gyro.LengthSquared() > WarmupGyroStationaryThresholdSq || _warmupStableStartTicks < 0) {
                                    _warmupStableStartTicks = now;
                                } else if (now - _warmupStableStartTicks >= _warmupTicksRequired) {
                                    _warmupComplete = true;
                                }
                                if (!_warmupComplete) break;
                            }
                            NewData?.Invoke(this, EventArgs.Empty);
                            break;
                    }
                }
            } catch (Exception ex) {
                OnExceptionMessage?.Invoke(this, ex.Message + ":" + ex.StackTrace);
            }
        }

        public void Dispose() {
            disposed = true;
            try { JoyCon1HidImuReader.SampleReady -= OnJoyCon1HidSample; } catch { }
            if (_hidImuReaderActive)
            {
                try { JoyCon1HidImuReader.Stop(_index); } catch { }
            }
        }
    }
}
