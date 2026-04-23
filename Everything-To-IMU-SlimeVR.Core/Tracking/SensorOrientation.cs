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
        // Warm-up: suppress orientation emission until VQF has had ~200ms of stationary
        // samples to converge. Prevents the "spin on connect" where downstream sees the
        // first wildly-wrong quaternion before the accelerometer has tilted it down.
        // Threshold ~8.6°/s (squared in rad/s) — generous, picks up clear hand motion
        // without false-tripping on sensor noise.
        private static readonly long WarmupTicksRequired = Stopwatch.Frequency / 5; // 200 ms
        private const float WarmupGyroStationaryThresholdSq = 0.0225f; // (0.15 rad/s)^2
        private long _warmupStableStartTicks = -1;
        private bool _warmupComplete = false;
        private readonly GyroBiasCalibrator _biasCal = new GyroBiasCalibrator();
        public bool GyroBiasCalibrated => _biasCal.HasBias;
        public Vector3 GyroBias => _biasCal.Bias;
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
        }

        private void SensorOrientation_OnNewJSLData(object? sender, Tuple<int, JSL.JOY_SHOCK_STATE, JSL.JOY_SHOCK_STATE, JSL.IMU_STATE, JSL.IMU_STATE, float> e) {
            try {
                if (e.Item1 == _index) {
                    var rawAccel = new Vector3(e.Item4.accelX, e.Item4.accelY, e.Item4.accelZ) * 10;
                    var rawGyroRad = (new Vector3(e.Item4.gyroX, e.Item4.gyroY, e.Item4.gyroZ)).ConvertDegreesToRadians();

                    // Controller-type aware axis remap: Right Joy-Con (ctype=2) IMU is mounted
                    // rotated 180° around X vs Left, so Y+Z are physically inverted. Linux
                    // hid-nintendo applies the same fix. Must be done before VQF and before
                    // bias calibration so the bias estimate lives in the corrected frame.
                    int ctype = JSL.JslGetControllerType(_index);
                    if (ctype == 2) // Joy-Con R
                    {
                        rawAccel = new Vector3(rawAccel.X, -rawAccel.Y, -rawAccel.Z);
                        rawGyroRad = new Vector3(rawGyroRad.X, -rawGyroRad.Y, -rawGyroRad.Z);
                    }

                    _accelerometer = rawAccel;
                    _gyro = rawGyroRad;
                    // Note: GyroBiasCalibrator + SetTauAcc(1.5) were applied earlier but caused
                    // direction artifacts ("moving forward = tracker goes backward"). Root cause:
                    // VQF already has internal rest-bias estimation (motionBiasEstEnabled=true by
                    // default); adding a second bias subtractor on top fought VQF's own estimator.
                    // tauAcc=1.5 compounded it by letting accel influence tilt too aggressively
                    // during smooth linear motion, producing swim-like reverse drift. Reverted to
                    // VQF defaults — they're already tuned for the common handheld case.

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
                                } else if (now - _warmupStableStartTicks >= WarmupTicksRequired) {
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
        }
    }
}
