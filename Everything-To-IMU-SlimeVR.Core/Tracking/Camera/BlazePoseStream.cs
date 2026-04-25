using System;
using System.Threading;
using OpenCvSharp;

namespace Everything_To_IMU_SlimeVR.Tracking.Camera {
    /// <summary>
    /// Wires <see cref="WebcamCaptureLoop"/> + <see cref="BlazePoseInference"/>. Inference is
    /// gated to one-in-flight: if inference is still running when the next frame arrives, that
    /// frame is dropped. Keeps memory bounded and prevents queue buildup on slow GPUs.
    /// </summary>
    public sealed class BlazePoseStream : IDisposable {
        public event Action<BlazePoseFrame> PoseReady;
        public event Action<string> StatusChanged;

        private readonly WebcamCaptureLoop _capture;
        private readonly BlazePoseInference _inference;
        private int _inflight; // 0 = idle, 1 = running

        public WebcamCaptureLoop Capture => _capture;
        public BlazePoseInference Inference => _inference;
        public long FramesProcessed { get; private set; }
        public long FramesDropped { get; private set; }
        public double LastInferenceMs { get; private set; }

        public BlazePoseStream(string modelPath, int deviceIndex = 0, int width = 1280, int height = 720, int fps = 30, bool preferDirectML = true) {
            _capture = new WebcamCaptureLoop(deviceIndex, width, height, fps);
            _inference = new BlazePoseInference(modelPath, preferDirectML);
            _capture.FrameReady += OnFrame;
            _capture.StatusChanged += s => StatusChanged?.Invoke(s);
        }

        public void Start() => _capture.Start();
        public void Stop() => _capture.Stop();

        private void OnFrame(Mat frame, double timestampSeconds) {
            // Drop frame if inference is busy. Don't queue.
            if (Interlocked.CompareExchange(ref _inflight, 1, 0) != 0) {
                FramesDropped++;
                return;
            }
            try {
                var result = _inference.Infer(frame, timestampSeconds);
                if (result != null) {
                    FramesProcessed++;
                    LastInferenceMs = result.InferenceMs;
                    PoseReady?.Invoke(result);
                }
            } catch (Exception ex) {
                StatusChanged?.Invoke($"Inference error: {ex.Message}");
            } finally {
                Interlocked.Exchange(ref _inflight, 0);
            }
        }

        public void Dispose() {
            _capture?.Dispose();
            _inference?.Dispose();
        }
    }
}
