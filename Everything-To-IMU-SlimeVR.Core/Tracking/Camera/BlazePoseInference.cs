using System;
using System.IO;
using System.Numerics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace Everything_To_IMU_SlimeVR.Tracking.Camera {
    /// <summary>
    /// BlazePose landmark inference via ONNX Runtime DirectML.
    /// Single-stage path: assumes the input frame is already cropped/centered on the person.
    /// Two-stage detector path (pose_detection.onnx) is a follow-up; for the PoC we run landmarker
    /// on the whole frame and rely on the user being centered.
    ///
    /// Models from PINTO_model_zoo/053_BlazePose. Two known variants observed in the wild:
    ///   FULL (pose_landmark_full.onnx, ~13 MB) — most-portable export from MediaPipe:
    ///     Input:  [1, 256, 256, 3] float32 RGB **NHWC**, [0..1]
    ///     Outputs:
    ///       - "ld_3d:0"             [1, 195]   = 39 * (x,y,z, vis, pres) image-space (x,y in pixels 0..256; z relative)
    ///       - "output_poseflag:0"   [1, 1]     pose presence
    ///       - "output_segmentation:0" [1, 128, 128, 1]  ignore
    ///   HEAVY (pose_landmark_heavy.onnx, ~26 MB) — adds metric world output:
    ///     Adds "world_3d:0" [1, 117] = 39 * (x,y,z) metres, hip-origin
    ///
    /// World metric output is preferred for SlimeVR but full-only is acceptable: bone
    /// quaternions only need relative landmark vectors, units cancel out. LandmarkProcessor
    /// normalises by body-height calibration in either case.
    /// </summary>
    public sealed class BlazePoseInference : IDisposable {
        private const int InputSize = 256;
        private const int LandmarkCount = 33;
        private const int WorldLandmarkStride = 3; // x, y, z
        private const int ImageLandmarkStride = 5; // x, y, z, visibility, presence

        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly string[] _outputNames;
        private readonly bool _isNhwc;
        private long _frameCounter;

        public bool HasWorldLandmarks { get; private set; }
        public string ModelLayout => _isNhwc ? "NHWC" : "NCHW";

        public BlazePoseInference(string modelPath, bool preferDirectML = true) {
            if (!File.Exists(modelPath)) {
                throw new FileNotFoundException(
                    $"BlazePose model not found at '{modelPath}'. Expected pose_landmark_heavy.onnx from PINTO_model_zoo/053_BlazePose.",
                    modelPath);
            }

            var options = new SessionOptions {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            };

            if (preferDirectML) {
                try { options.AppendExecutionProvider_DML(0); } catch { /* fall through to CPU */ }
            }

            _session = new InferenceSession(modelPath, options);
            _inputName = _session.InputMetadata.Keys.First();
            _outputNames = _session.OutputMetadata.Keys.ToArray();

            // Detect NHWC vs NCHW from input dim layout. NHWC has channels last; NCHW has channels at index 1.
            var dims = _session.InputMetadata[_inputName].Dimensions;
            _isNhwc = dims.Length == 4 && dims[3] == 3;

            // World landmarks present only in heavy variant — detect by output count/shape.
            foreach (var kv in _session.OutputMetadata) {
                int len = 1;
                foreach (var d in kv.Value.Dimensions) len *= System.Math.Max(1, d);
                if (len == 39 * WorldLandmarkStride) { HasWorldLandmarks = true; break; }
            }
        }

        /// <summary>
        /// Runs landmark inference on a BGR frame. Caller is responsible for ensuring the
        /// person is reasonably centered and visible. Returns null on inference failure.
        /// </summary>
        public BlazePoseFrame Infer(Mat bgrFrame, double captureTimestampSeconds) {
            if (bgrFrame == null || bgrFrame.Empty()) return null;

            using var resized = new Mat();
            Cv2.Resize(bgrFrame, resized, new Size(InputSize, InputSize), 0, 0, InterpolationFlags.Linear);
            using var rgb = new Mat();
            Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

            var tensor = MatToTensor(rgb, _isNhwc);
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var results = _session.Run(inputs, _outputNames);
            sw.Stop();

            // Find tensors by total length: 117 = world (39*3), 195 = image landmarks (39*5).
            DenseTensor<float> worldTensor = null;
            DenseTensor<float> imageTensor = null;
            foreach (var r in results) {
                if (r.Value is DenseTensor<float> dt) {
                    if (dt.Length == 39 * WorldLandmarkStride && worldTensor == null) {
                        worldTensor = dt;
                    } else if (dt.Length == 39 * ImageLandmarkStride && imageTensor == null) {
                        imageTensor = dt;
                    }
                }
            }

            if (imageTensor == null) return null; // image-space landmarks are mandatory

            var landmarks = new BlazePoseLandmark[LandmarkCount];
            for (int i = 0; i < LandmarkCount; i++) {
                int iOff = i * ImageLandmarkStride;
                Vector3 pos;
                if (worldTensor != null) {
                    int wOff = i * WorldLandmarkStride;
                    pos = new Vector3(
                        worldTensor.GetValue(wOff + 0),
                        worldTensor.GetValue(wOff + 1),
                        worldTensor.GetValue(wOff + 2));
                } else {
                    // Full variant: convert image-space (pixels) to roughly normalised [-1..+1]
                    // centred on input crop. Z is already model-relative depth.
                    float halfSize = InputSize * 0.5f;
                    pos = new Vector3(
                        (imageTensor.GetValue(iOff + 0) - halfSize) / halfSize,
                        (imageTensor.GetValue(iOff + 1) - halfSize) / halfSize,
                        imageTensor.GetValue(iOff + 2) / halfSize);
                }
                float visibility = SigmoidClamp(imageTensor.GetValue(iOff + 3));
                float presence = SigmoidClamp(imageTensor.GetValue(iOff + 4));
                landmarks[i] = new BlazePoseLandmark(pos, visibility, presence);
            }

            return new BlazePoseFrame {
                Landmarks = landmarks,
                FrameNumber = ++_frameCounter,
                InferenceMs = sw.Elapsed.TotalMilliseconds,
                CaptureTimestampSeconds = captureTimestampSeconds
            };
        }

        private static DenseTensor<float> MatToTensor(Mat rgb, bool nhwc) {
            // BlazePose expects float32 normalised to [0..1]. Layout depends on export:
            //   NHWC = [1, H, W, 3]   (PINTO 053 full variant uses this)
            //   NCHW = [1, 3, H, W]   (some custom exports)
            //
            // Bulk-copy the whole Mat to a managed byte[] (single Marshal.Copy under the hood
            // = ~memcpy speed) then iterate over the array. Per-pixel indexer access burns
            // ~5-10x more CPU than this on a 256x256 frame.
            int h = rgb.Rows;
            int w = rgb.Cols;
            int size = h * w * 3;
            var tensor = nhwc
                ? new DenseTensor<float>(new[] { 1, h, w, 3 })
                : new DenseTensor<float>(new[] { 1, 3, h, w });
            byte[] bytes = new byte[size];
            System.Runtime.InteropServices.Marshal.Copy(rgb.Data, bytes, 0, size);
            const float inv255 = 1f / 255f;
            var span = tensor.Buffer.Span;

            if (nhwc) {
                // Mat is HWC RGB after Cvt; tensor is also HWC. 1-to-1 element copy with scale.
                for (int i = 0; i < size; i++) span[i] = bytes[i] * inv255;
            } else {
                // Channel-major (CHW): split RGB interleaved into 3 contiguous planes.
                int plane = h * w;
                int j = 0;
                for (int p = 0; p < plane; p++) {
                    span[p] = bytes[j] * inv255;
                    span[plane + p] = bytes[j + 1] * inv255;
                    span[2 * plane + p] = bytes[j + 2] * inv255;
                    j += 3;
                }
            }
            return tensor;
        }

        private static float SigmoidClamp(float raw) {
            // Some BlazePose exports emit pre-sigmoid logits for visibility/presence.
            float s = 1f / (1f + MathF.Exp(-raw));
            return MathF.Max(0f, MathF.Min(1f, s));
        }

        public void Dispose() => _session?.Dispose();
    }
}
