using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Everything_To_IMU_SlimeVR.Tracking.Camera {
    /// <summary>
    /// Owns the webcam pose pipeline: capture → ONNX inference → landmark filter →
    /// bone solver → T-pose calibration → 6 virtual SlimeVR trackers.
    ///
    /// Tracker IDs in [100..105] reserved to avoid collisions with the IDs the existing
    /// JC2/DS5/PSMove sources hand out (0..N). The IDs are UI-side only — the wire protocol
    /// uses one UDPHandler instance per tracker, sending under wire-tracker-id 0.
    /// </summary>
    public sealed class WebcamPoseManager : IDisposable {
        private const int FirstTrackerId = 100;

        public event Action<string> StatusChanged;
        public event Action TPoseAutoCaptured;
        public event Action<BlazePoseFrame> ProcessedFrameReady;
        public event EventHandler<WebcamPoseTracker> TrackerAdded;
        public event EventHandler<WebcamPoseTracker> TrackerRemoved;
        /// <summary>
        /// Fired on the capture thread for every successfully-grabbed frame, regardless of
        /// whether inference picks it up. Used by the UI live-preview overlay. The Mat is
        /// owned by the capture loop — DO NOT keep a reference; clone it if you need the
        /// pixels after the handler returns.
        /// </summary>
        public event Action<OpenCvSharp.Mat, double> RawFrameReady;

        private static WebcamPoseManager _instance;
        /// <summary>
        /// Process-wide instance, lazily created. Mirrors the pattern used by JoyCon2Manager —
        /// GenericTrackerManager subscribes once at startup so new trackers automatically flow
        /// into the global registry / UI. Pipeline only runs after <see cref="Start"/>.
        /// </summary>
        public static WebcamPoseManager Instance => _instance ??= new WebcamPoseManager();

        private BlazePoseStream _stream;
        private LandmarkProcessor _processor;
        private TPoseCalibration _calibration;
        private readonly List<WebcamPoseTracker> _trackers = new();
        private readonly object _gate = new();
        private int _deviceIndex;
        private string _modelPath;
        private bool _running;
        private bool _disposed;

        public IReadOnlyList<WebcamPoseTracker> Trackers {
            get { lock (_gate) return _trackers.ToArray(); }
        }
        public bool Running => _running;
        public WebcamCaptureLoop Capture => _stream?.Capture;
        public BlazePoseInference Inference => _stream?.Inference;
        public TPoseCalibration Calibration => _calibration;
        public LandmarkProcessor Processor => _processor;
        public long FramesProcessed => _stream?.FramesProcessed ?? 0;
        public long FramesDropped => _stream?.FramesDropped ?? 0;
        public double LastInferenceMs => _stream?.LastInferenceMs ?? 0;

        public WebcamPoseManager() {
            _processor = new LandmarkProcessor();
            _calibration = new TPoseCalibration();
            _calibration.AutoCaptured += () => TPoseAutoCaptured?.Invoke();
        }

        /// <summary>
        /// Start the pipeline. Spawns 6 trackers, opens camera, loads model. If trackers were
        /// previously created they are disposed first (clean restart).
        /// </summary>
        public void Start(int deviceIndex = 0, string modelDir = null, int captureWidth = 1280, int captureHeight = 720, int fps = 30) {
            if (_running || _disposed) return;
            _deviceIndex = deviceIndex;
            _modelPath = ResolveModelPath(modelDir);
            if (!File.Exists(_modelPath)) {
                StatusChanged?.Invoke($"Model not found: {_modelPath}");
                return;
            }

            // Spawn trackers first so they are visible in the UI even before the first inference.
            WebcamPoseTracker[] spawned;
            lock (_gate) {
                DisposeTrackersUnlocked();
                int idx = 0;
                foreach (WebcamBone b in Enum.GetValues(typeof(WebcamBone))) {
                    var tracker = new WebcamPoseTracker(b, deviceIndex, FirstTrackerId + idx);
                    _trackers.Add(tracker);
                    idx++;
                }
                spawned = _trackers.ToArray();
            }
            foreach (var t in spawned) TrackerAdded?.Invoke(this, t);

            try {
                _stream = new BlazePoseStream(_modelPath, deviceIndex, captureWidth, captureHeight, fps, preferDirectML: true);
                _stream.PoseReady += OnPoseReady;
                _stream.StatusChanged += s => StatusChanged?.Invoke(s);
                _stream.Capture.FrameReady += (mat, ts) => {
                    try { RawFrameReady?.Invoke(mat, ts); } catch { }
                };
                _stream.Start();
                _running = true;
                StatusChanged?.Invoke($"Webcam pose pipeline started: device={deviceIndex} layout={_stream.Inference.ModelLayout} world3D={_stream.Inference.HasWorldLandmarks}");
            } catch (Exception ex) {
                StatusChanged?.Invoke($"Failed to start pipeline: {ex.Message}");
                StopInternal();
            }
        }

        public void Stop() => StopInternal();

        public bool CaptureTPoseNow() {
            // Caller can force a manual capture independent of auto-detect. Returns true if
            // the latest frame had all 6 bones valid.
            return _calibration.Calibrated; // placeholder — actual capture happens on next OnPoseReady
        }

        /// <summary>Reset the calibration so auto-capture can fire again.</summary>
        public void ResetCalibration() => _calibration.Reset();

        private void StopInternal() {
            _running = false;
            try {
                if (_stream != null) {
                    _stream.PoseReady -= OnPoseReady;
                    _stream.Stop();
                    _stream.Dispose();
                    _stream = null;
                }
            } catch { }
            lock (_gate) DisposeTrackersUnlocked();
        }

        private void DisposeTrackersUnlocked() {
            foreach (var t in _trackers) {
                try {
                    TrackerRemoved?.Invoke(this, t);
                    t.MarkDisconnected();
                    t.Dispose();
                } catch { }
            }
            _trackers.Clear();
        }

        private async void OnPoseReady(BlazePoseFrame raw) {
            if (!_running || raw == null) return;

            var processed = _processor.Process(raw);
            ProcessedFrameReady?.Invoke(processed);

            var solved = BoneSolver.Solve(processed);
            var calibrated = _calibration.Apply(solved, raw.CaptureTimestampSeconds);

            // Push to trackers concurrently — each owns its own UDPHandler so sends are independent.
            WebcamPoseTracker[] snapshot;
            lock (_gate) snapshot = _trackers.ToArray();

            var sendTasks = new List<Task>(snapshot.Length);
            foreach (var t in snapshot) {
                try {
                    sendTasks.Add(t.PushSolvedRotation(calibrated[t.Bone], calibrated.IsValid(t.Bone)));
                } catch (Exception ex) {
                    StatusChanged?.Invoke($"Push failed for {t.Bone}: {ex.Message}");
                }
            }
            try { await Task.WhenAll(sendTasks); } catch { }
        }

        private static string ResolveModelPath(string explicitDir) {
            if (!string.IsNullOrEmpty(explicitDir)) {
                return Path.Combine(explicitDir, "pose_landmark_full.onnx");
            }
            // Search common locations: app dir / parent / Models/blazepose. Mirrors how
            // JoyShockLibrary.dll + vqf.dll get located.
            string baseDir = AppContext.BaseDirectory;
            string[] candidates = {
                Path.Combine(baseDir, "Models", "blazepose", "pose_landmark_full.onnx"),
                Path.Combine(baseDir, "Models", "blazepose", "pose_landmark_heavy.onnx"),
                Path.Combine(baseDir, "..", "Models", "blazepose", "pose_landmark_full.onnx"),
                Path.Combine(baseDir, "..", "..", "..", "..", "Models", "blazepose", "pose_landmark_full.onnx"),
            };
            foreach (var c in candidates) {
                if (File.Exists(c)) return Path.GetFullPath(c);
            }
            return candidates[0]; // return the canonical path so error message points at it
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            StopInternal();
        }
    }
}
