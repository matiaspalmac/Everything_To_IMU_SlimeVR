using System;
using System.Numerics;
using SlimeImuProtocol.SlimeVR;

namespace Everything_To_IMU_SlimeVR.Tracking.Camera {
    /// <summary>
    /// One slot in the body skeleton emitted to SlimeVR.
    /// </summary>
    public enum WebcamBone {
        Hip = 0,
        Chest = 1,
        LeftUpperLeg = 2,
        RightUpperLeg = 3,
        LeftFoot = 4,
        RightFoot = 5,
    }

    /// <summary>
    /// One frame of solved bone orientations. Quaternions are world-space pre-calibration —
    /// caller layers <see cref="TPoseCalibration"/> on top to convert to delta-from-rest.
    /// </summary>
    public readonly struct SolvedBoneFrame {
        public readonly Quaternion HipQ;
        public readonly Quaternion ChestQ;
        public readonly Quaternion LeftUpperLegQ;
        public readonly Quaternion RightUpperLegQ;
        public readonly Quaternion LeftFootQ;
        public readonly Quaternion RightFootQ;
        public readonly Vector3 HipPosition;
        public readonly bool HipValid;
        public readonly bool ChestValid;
        public readonly bool LeftUpperLegValid;
        public readonly bool RightUpperLegValid;
        public readonly bool LeftFootValid;
        public readonly bool RightFootValid;

        public SolvedBoneFrame(
            Quaternion hip, bool hipV,
            Quaternion chest, bool chestV,
            Quaternion lUpperLeg, bool lUpperLegV,
            Quaternion rUpperLeg, bool rUpperLegV,
            Quaternion lFoot, bool lFootV,
            Quaternion rFoot, bool rFootV,
            Vector3 hipPos) {
            HipQ = hip; HipValid = hipV;
            ChestQ = chest; ChestValid = chestV;
            LeftUpperLegQ = lUpperLeg; LeftUpperLegValid = lUpperLegV;
            RightUpperLegQ = rUpperLeg; RightUpperLegValid = rUpperLegV;
            LeftFootQ = lFoot; LeftFootValid = lFootV;
            RightFootQ = rFoot; RightFootValid = rFootV;
            HipPosition = hipPos;
        }

        public Quaternion this[WebcamBone bone] => bone switch {
            WebcamBone.Hip => HipQ,
            WebcamBone.Chest => ChestQ,
            WebcamBone.LeftUpperLeg => LeftUpperLegQ,
            WebcamBone.RightUpperLeg => RightUpperLegQ,
            WebcamBone.LeftFoot => LeftFootQ,
            WebcamBone.RightFoot => RightFootQ,
            _ => Quaternion.Identity,
        };

        public bool IsValid(WebcamBone bone) => bone switch {
            WebcamBone.Hip => HipValid,
            WebcamBone.Chest => ChestValid,
            WebcamBone.LeftUpperLeg => LeftUpperLegValid,
            WebcamBone.RightUpperLeg => RightUpperLegValid,
            WebcamBone.LeftFoot => LeftFootValid,
            WebcamBone.RightFoot => RightFootValid,
            _ => false,
        };
    }

    /// <summary>
    /// Converts processed (filtered, hip-relative, SlimeVR-coord) landmarks into the 6 bone
    /// quaternions emitted to SlimeVR. Math:
    ///
    ///   HIP / CHEST  — Method B (orthonormal basis from hip-line + spine direction).
    ///   UPPER_LEG    — Method A (shortest-arc from rest-pose direction to hip→knee vector).
    ///   FOOT         — Method B (basis from heel→ankle (up) + heel→toe (forward)).
    ///
    /// Quaternions are world-space pre-T-pose-calibration. Rest-pose subtraction happens in
    /// <see cref="TPoseCalibration"/>.
    /// </summary>
    public static class BoneSolver {
        // Rest-pose direction for upper legs in SlimeVR coords: pointing straight DOWN.
        private static readonly Vector3 RestUpperLegDir = new Vector3(0, -1, 0);

        private const float MinBoneLength = 1e-4f; // tolerate degenerate bones
        private const float MinVisibility = 0.5f;

        public static SolvedBoneFrame Solve(BlazePoseFrame processed) {
            if (processed == null) return default;
            var lm = processed.Landmarks;

            // Cache landmark positions and visibility for the joints we care about.
            Vector3 PosOf(BlazePoseLandmarkIndex idx) => lm[(int)idx].Position;
            float Vis(BlazePoseLandmarkIndex idx) => lm[(int)idx].Visibility;

            var leftHip = PosOf(BlazePoseLandmarkIndex.LeftHip);
            var rightHip = PosOf(BlazePoseLandmarkIndex.RightHip);
            var leftShoulder = PosOf(BlazePoseLandmarkIndex.LeftShoulder);
            var rightShoulder = PosOf(BlazePoseLandmarkIndex.RightShoulder);
            var leftKnee = PosOf(BlazePoseLandmarkIndex.LeftKnee);
            var rightKnee = PosOf(BlazePoseLandmarkIndex.RightKnee);
            var leftAnkle = PosOf(BlazePoseLandmarkIndex.LeftAnkle);
            var rightAnkle = PosOf(BlazePoseLandmarkIndex.RightAnkle);
            var leftHeel = PosOf(BlazePoseLandmarkIndex.LeftHeel);
            var rightHeel = PosOf(BlazePoseLandmarkIndex.RightHeel);
            var leftToe = PosOf(BlazePoseLandmarkIndex.LeftFootIndex);
            var rightToe = PosOf(BlazePoseLandmarkIndex.RightFootIndex);

            var midHip = (leftHip + rightHip) * 0.5f;
            var midShoulder = (leftShoulder + rightShoulder) * 0.5f;

            // HIP — basis: x=hipLine, y=spine, z=cross(x,y)
            bool hipV = Vis(BlazePoseLandmarkIndex.LeftHip) >= MinVisibility &&
                        Vis(BlazePoseLandmarkIndex.RightHip) >= MinVisibility &&
                        Vis(BlazePoseLandmarkIndex.LeftShoulder) >= MinVisibility &&
                        Vis(BlazePoseLandmarkIndex.RightShoulder) >= MinVisibility;
            Quaternion hipQ = TryBasisQuat(
                xRaw: rightHip - leftHip,
                yRaw: midShoulder - midHip,
                out bool hipOk);
            if (!hipOk) hipV = false;

            // CHEST — same axes anchored at mid-shoulder.
            bool chestV = hipV; // shares the same input visibilities
            Quaternion chestQ = TryBasisQuat(
                xRaw: rightShoulder - leftShoulder,
                yRaw: midShoulder - midHip,
                out bool chestOk);
            if (!chestOk) chestV = false;

            // LEFT_UPPER_LEG — shortest-arc from rest direction (down) to hip→knee vector.
            bool lUlV = Vis(BlazePoseLandmarkIndex.LeftHip) >= MinVisibility &&
                        Vis(BlazePoseLandmarkIndex.LeftKnee) >= MinVisibility;
            Quaternion lUlQ = TryShortestArc(RestUpperLegDir, leftKnee - leftHip, out bool lUlOk);
            if (!lUlOk) lUlV = false;

            bool rUlV = Vis(BlazePoseLandmarkIndex.RightHip) >= MinVisibility &&
                        Vis(BlazePoseLandmarkIndex.RightKnee) >= MinVisibility;
            Quaternion rUlQ = TryShortestArc(RestUpperLegDir, rightKnee - rightHip, out bool rUlOk);
            if (!rUlOk) rUlV = false;

            // LEFT_FOOT — basis: y=heel→ankle (up), z=heel→toe (forward), x=cross(y,z).
            bool lFootV = Vis(BlazePoseLandmarkIndex.LeftHeel) >= MinVisibility &&
                          Vis(BlazePoseLandmarkIndex.LeftAnkle) >= MinVisibility &&
                          Vis(BlazePoseLandmarkIndex.LeftFootIndex) >= MinVisibility;
            Quaternion lFootQ = TryFootBasisQuat(leftHeel, leftAnkle, leftToe, out bool lFootOk);
            if (!lFootOk) lFootV = false;

            bool rFootV = Vis(BlazePoseLandmarkIndex.RightHeel) >= MinVisibility &&
                          Vis(BlazePoseLandmarkIndex.RightAnkle) >= MinVisibility &&
                          Vis(BlazePoseLandmarkIndex.RightFootIndex) >= MinVisibility;
            Quaternion rFootQ = TryFootBasisQuat(rightHeel, rightAnkle, rightToe, out bool rFootOk);
            if (!rFootOk) rFootV = false;

            // Hip position is already hip-relative (origin) — hip is at (0,0,0). Caller layers
            // floor + height calibration before sending to SlimeVR.
            return new SolvedBoneFrame(
                hipQ, hipV,
                chestQ, chestV,
                lUlQ, lUlV,
                rUlQ, rUlV,
                lFootQ, lFootV,
                rFootQ, rFootV,
                Vector3.Zero);
        }

        /// <summary>
        /// Method A — shortest-arc quaternion that rotates <paramref name="from"/> onto
        /// <paramref name="to"/>. Both vectors are normalised internally. Handles the 180°
        /// flip case by picking an arbitrary perpendicular axis.
        /// </summary>
        public static Quaternion TryShortestArc(Vector3 from, Vector3 to, out bool ok) {
            float lenFrom = from.Length();
            float lenTo = to.Length();
            if (lenFrom < MinBoneLength || lenTo < MinBoneLength) { ok = false; return Quaternion.Identity; }
            ok = true;

            Vector3 u = from / lenFrom;
            Vector3 v = to / lenTo;
            float dot = Vector3.Dot(u, v);

            if (dot < -0.999999f) {
                // 180° flip — pick any axis perpendicular to u.
                Vector3 axis = Math.Abs(u.X) < 0.9f
                    ? Vector3.Normalize(Vector3.Cross(u, Vector3.UnitX))
                    : Vector3.Normalize(Vector3.Cross(u, Vector3.UnitY));
                return new Quaternion(axis.X, axis.Y, axis.Z, 0);
            }

            Vector3 cross = Vector3.Cross(u, v);
            var q = new Quaternion(cross.X, cross.Y, cross.Z, 1 + dot);
            return Quaternion.Normalize(q);
        }

        /// <summary>
        /// Method B — orthonormal basis quaternion from a primary right vector and a primary
        /// up vector. The two vectors don't need to be perpendicular — the up vector is
        /// re-orthogonalised against the right vector. Returns identity + ok=false if either
        /// input is degenerate or both are colinear.
        /// </summary>
        public static Quaternion TryBasisQuat(Vector3 xRaw, Vector3 yRaw, out bool ok) {
            float lx = xRaw.Length();
            float ly = yRaw.Length();
            if (lx < MinBoneLength || ly < MinBoneLength) { ok = false; return Quaternion.Identity; }
            Vector3 x = xRaw / lx;
            Vector3 yProvisional = yRaw / ly;
            Vector3 z = Vector3.Cross(x, yProvisional);
            float lz = z.Length();
            if (lz < MinBoneLength) { ok = false; return Quaternion.Identity; } // colinear
            z /= lz;
            Vector3 y = Vector3.Cross(z, x); // re-orthogonalised, already unit since x ⟂ z
            ok = true;
            return MatrixToQuat(x, y, z);
        }

        /// <summary>
        /// Foot basis: y axis = heel→ankle (≈ up when standing), z axis = heel→toe
        /// (forward), x axis = cross(y, z) re-orthogonalised. Caller decides how to map this
        /// to SlimeVR's foot tracker convention via T-pose calibration.
        /// </summary>
        public static Quaternion TryFootBasisQuat(Vector3 heel, Vector3 ankle, Vector3 toe, out bool ok) {
            Vector3 yRaw = ankle - heel;
            Vector3 zRaw = toe - heel;
            float ly = yRaw.Length();
            float lz = zRaw.Length();
            if (ly < MinBoneLength || lz < MinBoneLength) { ok = false; return Quaternion.Identity; }

            Vector3 yProv = yRaw / ly;
            Vector3 zProv = zRaw / lz;
            Vector3 x = Vector3.Cross(yProv, zProv);
            float lx = x.Length();
            if (lx < MinBoneLength) { ok = false; return Quaternion.Identity; }
            x /= lx;
            Vector3 z = Vector3.Cross(x, yProv);
            float lzf = z.Length();
            if (lzf < MinBoneLength) { ok = false; return Quaternion.Identity; }
            z /= lzf;
            Vector3 y = Vector3.Cross(z, x);
            ok = true;
            return MatrixToQuat(x, y, z);
        }

        /// <summary>
        /// Convert an orthonormal basis (columns x|y|z, right-handed) to a unit quaternion.
        /// Standard Shoemake formulation; numerically robust across all four major sign
        /// branches of the trace.
        /// </summary>
        public static Quaternion MatrixToQuat(Vector3 x, Vector3 y, Vector3 z) {
            float trace = x.X + y.Y + z.Z;
            float qw, qx, qy, qz;
            if (trace > 0f) {
                float s = MathF.Sqrt(trace + 1f) * 2f;
                qw = 0.25f * s;
                qx = (y.Z - z.Y) / s;
                qy = (z.X - x.Z) / s;
                qz = (x.Y - y.X) / s;
            } else if (x.X > y.Y && x.X > z.Z) {
                float s = MathF.Sqrt(1f + x.X - y.Y - z.Z) * 2f;
                qw = (y.Z - z.Y) / s;
                qx = 0.25f * s;
                qy = (y.X + x.Y) / s;
                qz = (z.X + x.Z) / s;
            } else if (y.Y > z.Z) {
                float s = MathF.Sqrt(1f + y.Y - x.X - z.Z) * 2f;
                qw = (z.X - x.Z) / s;
                qx = (y.X + x.Y) / s;
                qy = 0.25f * s;
                qz = (z.Y + y.Z) / s;
            } else {
                float s = MathF.Sqrt(1f + z.Z - x.X - y.Y) * 2f;
                qw = (x.Y - y.X) / s;
                qx = (z.X + x.Z) / s;
                qy = (z.Y + y.Z) / s;
                qz = 0.25f * s;
            }
            return Quaternion.Normalize(new Quaternion(qx, qy, qz, qw));
        }

        /// <summary>
        /// Hemisphere-pinning: ensure consecutive quaternions stay on the same 4D hemisphere
        /// so slerp/EMA paths are short. Apply per-bone after solve, comparing to last
        /// emitted quat.
        /// </summary>
        public static Quaternion EnforceHemisphere(Quaternion previous, Quaternion current) {
            if (previous.X * current.X + previous.Y * current.Y +
                previous.Z * current.Z + previous.W * current.W < 0f) {
                return new Quaternion(-current.X, -current.Y, -current.Z, -current.W);
            }
            return current;
        }

        /// <summary>
        /// Map our internal <see cref="WebcamBone"/> → SlimeVR <see cref="FirmwareConstants.TrackerPosition"/>.
        /// Centralised so UI and tracker code agree on slot assignment.
        /// </summary>
        public static FirmwareConstants.TrackerPosition ToSlimePosition(WebcamBone bone) => bone switch {
            WebcamBone.Hip => FirmwareConstants.TrackerPosition.HIP,
            WebcamBone.Chest => FirmwareConstants.TrackerPosition.CHEST,
            WebcamBone.LeftUpperLeg => FirmwareConstants.TrackerPosition.LEFT_UPPER_LEG,
            WebcamBone.RightUpperLeg => FirmwareConstants.TrackerPosition.RIGHT_UPPER_LEG,
            WebcamBone.LeftFoot => FirmwareConstants.TrackerPosition.LEFT_FOOT,
            WebcamBone.RightFoot => FirmwareConstants.TrackerPosition.RIGHT_FOOT,
            _ => FirmwareConstants.TrackerPosition.NONE,
        };
    }
}
