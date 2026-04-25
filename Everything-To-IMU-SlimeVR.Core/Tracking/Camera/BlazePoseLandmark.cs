using System.Numerics;

namespace Everything_To_IMU_SlimeVR.Tracking.Camera {
    /// <summary>
    /// MediaPipe BlazePose 33-landmark indices. Anatomical labels (LEFT = user's left).
    /// </summary>
    public enum BlazePoseLandmarkIndex {
        Nose = 0,
        LeftEyeInner = 1, LeftEye = 2, LeftEyeOuter = 3,
        RightEyeInner = 4, RightEye = 5, RightEyeOuter = 6,
        LeftEar = 7, RightEar = 8,
        MouthLeft = 9, MouthRight = 10,
        LeftShoulder = 11, RightShoulder = 12,
        LeftElbow = 13, RightElbow = 14,
        LeftWrist = 15, RightWrist = 16,
        LeftPinky = 17, RightPinky = 18,
        LeftIndex = 19, RightIndex = 20,
        LeftThumb = 21, RightThumb = 22,
        LeftHip = 23, RightHip = 24,
        LeftKnee = 25, RightKnee = 26,
        LeftAnkle = 27, RightAnkle = 28,
        LeftHeel = 29, RightHeel = 30,
        LeftFootIndex = 31, RightFootIndex = 32
    }

    /// <summary>
    /// Single 3D landmark. Position in metres, hip-origin (BlazePose worldLandmarks convention).
    /// MediaPipe coord space: +X right, +Y down, +Z toward camera. Convert to SlimeVR before use.
    /// </summary>
    public readonly struct BlazePoseLandmark {
        public readonly Vector3 Position;
        public readonly float Visibility;
        public readonly float Presence;

        public BlazePoseLandmark(Vector3 position, float visibility, float presence) {
            Position = position;
            Visibility = visibility;
            Presence = presence;
        }
    }

    /// <summary>
    /// One-frame snapshot of all 33 BlazePose landmarks plus inference timing.
    /// </summary>
    public sealed class BlazePoseFrame {
        public BlazePoseLandmark[] Landmarks { get; init; } = new BlazePoseLandmark[33];
        public long FrameNumber { get; init; }
        public double InferenceMs { get; init; }
        public double CaptureTimestampSeconds { get; init; }

        public BlazePoseLandmark this[BlazePoseLandmarkIndex idx] => Landmarks[(int)idx];
    }
}
