using System;
using System.Numerics;

namespace Everything_To_IMU_SlimeVR.Tracking.Camera {
    /// <summary>
    /// Converts raw <see cref="BlazePoseFrame"/> output into a stable, hip-relative,
    /// SlimeVR-coord-space frame ready for bone solving.
    ///
    /// Pipeline (per call):
    ///   1. Apply 1€ filter to each of the 33 landmarks per axis (X, Y, Z).
    ///   2. Hold previous filtered value when visibility &lt; <see cref="HoldVisibilityThreshold"/>
    ///      (usually 0.5). Prevents hallucinated landmarks from poisoning the bone solver.
    ///   3. Convert MediaPipe coords → SlimeVR coords:
    ///        MP : +X right, +Y down, +Z toward camera
    ///        SV : +X right, +Y up,   -Z forward (right-handed, Y-up)
    ///        formula: (x, -y, -z)
    ///   4. Subtract midHip = midpoint(LEFT_HIP, RIGHT_HIP) to make everything hip-relative.
    ///
    /// Optionally a body-height scale can be applied so positions become approximately metric.
    /// For SlimeVR rotations this is unnecessary (unit-invariant) but useful when sending hip
    /// position to the server.
    /// </summary>
    public sealed class LandmarkProcessor {
        private const int LandmarkCount = 33;

        public double MinCutoff { get; }
        public double Beta { get; }
        public double DCutoff { get; }
        public float HoldVisibilityThreshold { get; set; } = 0.5f;
        public float HeightScale { get; set; } = 1.0f; // multiplier applied after hip-relative

        private readonly OneEuroFilter[] _filters; // 33 landmarks × 3 axes = 99 filters
        private readonly BlazePoseLandmark[] _lastFiltered;
        private bool _hasPrevious;

        public LandmarkProcessor(double minCutoff = 1.0, double beta = 0.007, double dCutoff = 1.0) {
            MinCutoff = minCutoff;
            Beta = beta;
            DCutoff = dCutoff;
            _filters = new OneEuroFilter[LandmarkCount * 3];
            for (int i = 0; i < _filters.Length; i++) {
                _filters[i] = new OneEuroFilter(minCutoff, beta, dCutoff);
            }
            _lastFiltered = new BlazePoseLandmark[LandmarkCount];
        }

        public void Reset() {
            for (int i = 0; i < _filters.Length; i++) _filters[i].Reset();
            _hasPrevious = false;
        }

        /// <summary>
        /// Process one raw frame from <see cref="BlazePoseInference"/>. Returns a new
        /// <see cref="BlazePoseFrame"/> with filtered + coord-converted + hip-relative
        /// landmarks. Visibility/presence are passed through unchanged.
        /// </summary>
        public BlazePoseFrame Process(BlazePoseFrame raw) {
            if (raw == null) return null;

            var processed = new BlazePoseLandmark[LandmarkCount];
            double t = raw.CaptureTimestampSeconds;

            for (int i = 0; i < LandmarkCount; i++) {
                var src = raw.Landmarks[i];
                Vector3 pos;

                if (src.Visibility < HoldVisibilityThreshold && _hasPrevious) {
                    // Hold previous filtered value — but advance filter timestamps so derivative
                    // doesn't spike when visibility recovers next frame.
                    var prev = _lastFiltered[i].Position;
                    _filters[i * 3 + 0].Filter(prev.X, t);
                    _filters[i * 3 + 1].Filter(prev.Y, t);
                    _filters[i * 3 + 2].Filter(prev.Z, t);
                    pos = prev;
                } else {
                    float fx = (float)_filters[i * 3 + 0].Filter(src.Position.X, t);
                    float fy = (float)_filters[i * 3 + 1].Filter(src.Position.Y, t);
                    float fz = (float)_filters[i * 3 + 2].Filter(src.Position.Z, t);
                    pos = new Vector3(fx, fy, fz);
                }

                _lastFiltered[i] = new BlazePoseLandmark(pos, src.Visibility, src.Presence);
                processed[i] = _lastFiltered[i];
            }
            _hasPrevious = true;

            // Coord convert + hip-relative.
            var leftHip = processed[(int)BlazePoseLandmarkIndex.LeftHip].Position;
            var rightHip = processed[(int)BlazePoseLandmarkIndex.RightHip].Position;
            var midHip = (leftHip + rightHip) * 0.5f;

            for (int i = 0; i < LandmarkCount; i++) {
                var p = processed[i].Position;
                var converted = MediaPipeToSlimeVR(p - midHip) * HeightScale;
                processed[i] = new BlazePoseLandmark(converted, processed[i].Visibility, processed[i].Presence);
            }

            return new BlazePoseFrame {
                Landmarks = processed,
                FrameNumber = raw.FrameNumber,
                InferenceMs = raw.InferenceMs,
                CaptureTimestampSeconds = raw.CaptureTimestampSeconds
            };
        }

        /// <summary>
        /// Convert MediaPipe coords → SlimeVR / Unity / OpenXR right-handed Y-up:
        /// negate Y (down → up) and Z (toward-camera → forward = -Z).
        /// </summary>
        public static Vector3 MediaPipeToSlimeVR(Vector3 mp) =>
            new Vector3(mp.X, -mp.Y, -mp.Z);

        /// <summary>
        /// Convert a quaternion expressed in MediaPipe coords to SlimeVR coords.
        /// The same axis negation applies; for a quaternion (w, x, y, z) that's (w, x, -y, -z).
        /// </summary>
        public static Quaternion MediaPipeQuatToSlimeVR(Quaternion q) =>
            new Quaternion(q.X, -q.Y, -q.Z, q.W);
    }
}
