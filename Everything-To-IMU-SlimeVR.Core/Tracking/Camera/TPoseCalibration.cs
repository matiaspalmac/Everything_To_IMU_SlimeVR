using System;
using System.Numerics;

namespace Everything_To_IMU_SlimeVR.Tracking.Camera {
    /// <summary>
    /// Per-bone rest-pose offset. Captures the current bone quaternions when the user is in
    /// a static pose (e.g. T-pose, standing still), then on each subsequent frame outputs the
    /// delta from rest:
    ///
    ///     q_out = q_current * conjugate(q_rest)
    ///
    /// This is the same pattern SlimeVR's IMU trackers use for mounting calibration —
    /// "current orientation relative to rest" makes the body's natural pose align with the
    /// avatar's identity rotation.
    ///
    /// Auto-capture: monitors landmark stillness across all 6 bones; when all bones have
    /// been valid AND their inter-frame angular delta has stayed below
    /// <see cref="StillnessThresholdDegrees"/> for <see cref="StillnessDurationSeconds"/>,
    /// fires a capture automatically.
    /// </summary>
    public sealed class TPoseCalibration {
        private const int BoneCount = 6;

        private readonly Quaternion[] _rest = new Quaternion[BoneCount];
        private readonly bool[] _restCaptured = new bool[BoneCount];
        private readonly Quaternion[] _lastEmitted = new Quaternion[BoneCount];
        private readonly Quaternion[] _lastSeen = new Quaternion[BoneCount];
        private readonly bool[] _hasLastSeen = new bool[BoneCount];

        private double _stillnessStartTime;
        private bool _stillnessTracking;

        public bool Calibrated { get; private set; }
        public float StillnessThresholdDegrees { get; set; } = 1.5f;
        public double StillnessDurationSeconds { get; set; } = 1.5;
        public bool AutoCaptureEnabled { get; set; } = true;

        /// <summary>Fires once when an automatic capture lands.</summary>
        public event Action AutoCaptured;

        public TPoseCalibration() {
            for (int i = 0; i < BoneCount; i++) {
                _rest[i] = Quaternion.Identity;
                _lastEmitted[i] = Quaternion.Identity;
                _lastSeen[i] = Quaternion.Identity;
            }
        }

        /// <summary>Manually capture the current frame as the rest pose. All 6 bones must be valid.</summary>
        public bool CaptureFromCurrent(in SolvedBoneFrame frame) {
            for (int i = 0; i < BoneCount; i++) {
                var bone = (WebcamBone)i;
                if (!frame.IsValid(bone)) return false;
            }
            for (int i = 0; i < BoneCount; i++) {
                _rest[i] = frame[(WebcamBone)i];
                _restCaptured[i] = true;
            }
            Calibrated = true;
            return true;
        }

        public void Reset() {
            for (int i = 0; i < BoneCount; i++) {
                _rest[i] = Quaternion.Identity;
                _restCaptured[i] = false;
                _lastEmitted[i] = Quaternion.Identity;
                _hasLastSeen[i] = false;
            }
            Calibrated = false;
            _stillnessTracking = false;
        }

        /// <summary>
        /// Apply rest-pose delta. Hemisphere-pinned against the previously emitted quat per
        /// bone so EMA / slerp paths stay short. If a bone's input is invalid, holds the
        /// previously emitted value.
        /// </summary>
        public SolvedBoneFrame Apply(in SolvedBoneFrame frame, double timestampSeconds) {
            // Stillness tracking for auto-capture, regardless of calibrated state.
            if (AutoCaptureEnabled && !Calibrated) {
                MaybeAutoCapture(in frame, timestampSeconds);
            }

            var outQ = new Quaternion[BoneCount];
            var outValid = new bool[BoneCount];

            for (int i = 0; i < BoneCount; i++) {
                var bone = (WebcamBone)i;
                if (!frame.IsValid(bone)) {
                    outQ[i] = _lastEmitted[i];
                    outValid[i] = false;
                    continue;
                }
                Quaternion current = frame[bone];
                Quaternion delta = Calibrated && _restCaptured[i]
                    ? Quaternion.Multiply(current, Quaternion.Conjugate(_rest[i]))
                    : current;
                delta = BoneSolver.EnforceHemisphere(_lastEmitted[i], delta);
                _lastEmitted[i] = delta;
                outQ[i] = delta;
                outValid[i] = true;
            }

            return new SolvedBoneFrame(
                outQ[0], outValid[0],
                outQ[1], outValid[1],
                outQ[2], outValid[2],
                outQ[3], outValid[3],
                outQ[4], outValid[4],
                outQ[5], outValid[5],
                frame.HipPosition);
        }

        private void MaybeAutoCapture(in SolvedBoneFrame frame, double timestampSeconds) {
            // Require all 6 bones to be present this frame.
            for (int i = 0; i < BoneCount; i++) {
                if (!frame.IsValid((WebcamBone)i)) {
                    _stillnessTracking = false;
                    return;
                }
            }

            // Compute max angular delta vs last seen.
            float maxAngleDeg = 0f;
            if (_hasLastSeen[0]) {
                for (int i = 0; i < BoneCount; i++) {
                    Quaternion seen = _lastSeen[i];
                    Quaternion now = frame[(WebcamBone)i];
                    float dot = MathF.Abs(Quaternion.Dot(seen, now));
                    if (dot > 1f) dot = 1f;
                    float angleRad = 2f * MathF.Acos(dot);
                    float angleDeg = angleRad * (180f / MathF.PI);
                    if (angleDeg > maxAngleDeg) maxAngleDeg = angleDeg;
                }
            }

            for (int i = 0; i < BoneCount; i++) {
                _lastSeen[i] = frame[(WebcamBone)i];
                _hasLastSeen[i] = true;
            }

            bool isStill = maxAngleDeg < StillnessThresholdDegrees;
            if (!isStill) {
                _stillnessTracking = false;
                return;
            }
            if (!_stillnessTracking) {
                _stillnessTracking = true;
                _stillnessStartTime = timestampSeconds;
                return;
            }
            if (timestampSeconds - _stillnessStartTime >= StillnessDurationSeconds) {
                if (CaptureFromCurrent(in frame)) {
                    AutoCaptured?.Invoke();
                }
                _stillnessTracking = false;
            }
        }
    }
}
