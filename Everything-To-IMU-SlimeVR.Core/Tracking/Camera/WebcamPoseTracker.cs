using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using SlimeImuProtocol.SlimeVR;
using SlimeImuProtocol.Utility;
using static Everything_To_IMU_SlimeVR.TrackerConfig;

namespace Everything_To_IMU_SlimeVR.Tracking.Camera {
    /// <summary>
    /// Virtual SlimeVR tracker fed by the webcam pose pipeline. One instance per
    /// <see cref="WebcamBone"/>; the manager owns the camera + inference + solver and pushes
    /// solved quaternions in here via <see cref="PushSolvedRotation"/>.
    ///
    /// Wire-level identity: <see cref="UDPHandler"/> with BoardType=CUSTOM, ImuType=UNKNOWN,
    /// McuType=CUSTOM (fake IMU), MagnetometerStatus=NOT_SUPPORTED. MAC = MD5 of
    /// "WebcamPose-{deviceIndex}-{bone}" so the SlimeVR server keys body-slot assignments
    /// stably across restarts.
    /// </summary>
    public sealed class WebcamPoseTracker : IBodyTracker, IDisposable {
        private readonly WebcamBone _bone;
        private readonly int _deviceIndex;
        private readonly int _id;
        private string _macSpoof;
        private byte[] _macAddressBytes;
        private UDPHandler _udpHandler;
        private bool _ready;
        private bool _disconnected;
        private bool _disposed;
        private Quaternion _rotation = Quaternion.Identity;
        private Vector3 _euler;
        private Vector3 _calibration;
        private long _packetsSent;
        private long _sendFailures;
        private float _lastHmdPosition;
        private string _debug = string.Empty;
        private double _hz;
        private float _jitterDegrees;
        private long _lastSendTicks;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly System.Collections.Generic.List<float> _intervalSeconds = new();
        private Quaternion _lastSentRotation = Quaternion.Identity;

        public WebcamBone Bone => _bone;
        public FirmwareConstants.TrackerPosition SlimePosition { get; }

        public int Id { get => _id; set { /* immutable after ctor */ } }
        public bool SupportsHaptics => false;
        public bool SupportsIMU => false;
        public string MacSpoof { get => _macSpoof; set => _macSpoof = value; }
        public Vector3 Euler { get => _euler; set => _euler = value; }
        public float LastHmdPositon { get => _lastHmdPosition; set => _lastHmdPosition = value; }
        public bool Ready { get => _ready; set => _ready = value; }
        public HapticNodeBinding HapticNodeBinding { get; set; }
        public Vector3 RotationCalibration { get => _calibration; set => _calibration = value; }
        public RotationReferenceType YawReferenceTypeValue { get; set; } = RotationReferenceType.TrustDeviceYaw;
        public RotationReferenceType ExtensionYawReferenceTypeValue { get; set; } = RotationReferenceType.TrustDeviceYaw;
        public string Debug => _debug;
        public long PacketsSent => _packetsSent;
        public long SendFailures => _sendFailures;
        public bool ServerReachable => _udpHandler?.Active == true && !_disconnected;
        public double Hz => _hz;
        public double ImuSampleRateHz => 0;
        public float JitterDegrees => _jitterDegrees;
        public float BatteryLevel => 1f; // virtual tracker — no battery
        public int LastLedArgb => 0;
        public bool Disconnected => _disconnected;
        public int Index => _deviceIndex;
        public Vector3 Acceleration => Vector3.Zero;
        public Quaternion Rotation => _rotation;

        public WebcamPoseTracker(WebcamBone bone, int deviceIndex, int trackerId) {
            _bone = bone;
            _deviceIndex = deviceIndex;
            _id = trackerId;
            SlimePosition = BoneSolver.ToSlimePosition(bone);

            string seed = $"WebcamPose-{deviceIndex}-{bone}";
            _macSpoof = HashUtility.CalculateMD5Hash(seed);
            // First 6 bytes of the MD5 hex string used as a stable pseudo-MAC. Same trick as
            // FalseThighTracker so server-side device records are deterministic.
            _macAddressBytes = new byte[] {
                (byte)_macSpoof[0], (byte)_macSpoof[1], (byte)_macSpoof[2],
                (byte)_macSpoof[3], (byte)_macSpoof[4], (byte)_macSpoof[5]
            };

            string firmwareString = $"WebcamPose-{bone}";
            _udpHandler = new UDPHandler(
                firmwareString,
                _macAddressBytes,
                FirmwareConstants.BoardType.CUSTOM,
                FirmwareConstants.ImuType.UNKNOWN,
                FirmwareConstants.McuType.UNKNOWN,
                FirmwareConstants.MagnetometerStatus.NOT_SUPPORTED,
                1);
            _udpHandler.Active = true;
            _ready = true;
        }

        /// <summary>
        /// Manager calls this with the calibrated bone quaternion (already in SlimeVR coords,
        /// already T-pose-delta'd). Sends rotation to SlimeVR via <see cref="UDPHandler.SetSensorBundle"/>
        /// (auto-falls back to two sends on legacy servers).
        /// </summary>
        public async Task PushSolvedRotation(Quaternion quaternion, bool valid) {
            if (!_ready || _disposed) return;
            if (!valid) return; // hold previous publish

            _rotation = quaternion;

            try {
                await _udpHandler.SetSensorBundle(quaternion, Vector3.Zero, 0);
                _packetsSent++;
                long now = _stopwatch.ElapsedTicks;
                if (_lastSendTicks != 0) {
                    float interval = (float)((now - _lastSendTicks) / (double)Stopwatch.Frequency);
                    if (interval > 0) {
                        _intervalSeconds.Add(interval);
                        if (_intervalSeconds.Count > 60) _intervalSeconds.RemoveAt(0);
                        float sum = 0;
                        for (int i = 0; i < _intervalSeconds.Count; i++) sum += _intervalSeconds[i];
                        _hz = _intervalSeconds.Count / Math.Max(sum, 1e-6);
                    }
                }
                _lastSendTicks = now;

                // Jitter EWMA — angular delta vs last-sent quaternion in degrees.
                float dot = Math.Abs(Quaternion.Dot(_lastSentRotation, quaternion));
                if (dot > 1f) dot = 1f;
                float angleRad = 2f * MathF.Acos(dot);
                float angleDeg = angleRad * (180f / MathF.PI);
                _jitterDegrees = _jitterDegrees * 0.9f + angleDeg * 0.1f;
                _lastSentRotation = quaternion;

                if (GenericTrackerManager.DebugOpen) {
                    _debug =
                        $"Bone: {_bone}\r\n" +
                        $"SlimeVR position: {SlimePosition}\r\n" +
                        $"Mac: {_macSpoof.Substring(0, 12)}…\r\n" +
                        $"Hz: {_hz:F1}  Jitter: {_jitterDegrees:F2}°\r\n" +
                        $"Quat: {quaternion}";
                }
            } catch (Exception ex) {
                _sendFailures++;
                _debug = $"Send error: {ex.Message}";
            }
        }

        public void MarkDisconnected() {
            _disconnected = true;
            _ready = false;
        }

        public void Rediscover() { /* No-op — manager owns the camera lifecycle. */ }
        public Vector3 GetCalibration() => _calibration;
        public void Identify() { /* No haptics on a virtual tracker. */ }
        public void HapticIntensityTest() { }
        public void EngageHaptics(int duration, float intensity) { }
        public void DisableHaptics() { }

        public override string ToString() => $"WebcamPose:{_bone}";

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            _ready = false;
            _udpHandler?.Dispose();
            _udpHandler = null;
        }
    }
}
