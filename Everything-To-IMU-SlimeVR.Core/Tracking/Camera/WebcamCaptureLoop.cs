using System;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace Everything_To_IMU_SlimeVR.Tracking.Camera {
    /// <summary>
    /// Single-camera capture loop. Uses DirectShow backend (DSHOW) explicitly — Media Foundation
    /// has documented enumeration / black-frame issues on Windows 11. MJPG fourcc avoids USB
    /// bandwidth throttling at 720p+.
    /// </summary>
    public sealed class WebcamCaptureLoop : IDisposable {
        public event Action<Mat, double> FrameReady;
        public event Action<string> StatusChanged;

        private readonly int _deviceIndex;
        private readonly int _requestedWidth;
        private readonly int _requestedHeight;
        private readonly int _requestedFps;

        private VideoCapture _capture;
        private CancellationTokenSource _cts;
        private Task _loop;
        private long _capturedFrames;

        public bool Running => _loop != null && !_loop.IsCompleted;
        public long CapturedFrames => Interlocked.Read(ref _capturedFrames);
        public int ActualWidth { get; private set; }
        public int ActualHeight { get; private set; }
        public double ActualFps { get; private set; }

        public WebcamCaptureLoop(int deviceIndex = 0, int width = 1280, int height = 720, int fps = 30) {
            _deviceIndex = deviceIndex;
            _requestedWidth = width;
            _requestedHeight = height;
            _requestedFps = fps;
        }

        public void Start() {
            if (Running) return;

            // Try DSHOW first — best track record on physical USB cameras and the only
            // backend OpenCvSharp4 reliably supports MJPG fourcc on. Fall back to MSMF if
            // DSHOW fails to open: virtual cameras (OBS Virtual Cam, NDI Tools, ManyCam)
            // sometimes register as MediaFoundation sources only.
            string backendName = "DSHOW";
            _capture = new VideoCapture(_deviceIndex, VideoCaptureAPIs.DSHOW);
            if (!_capture.IsOpened()) {
                _capture.Dispose();
                _capture = new VideoCapture(_deviceIndex, VideoCaptureAPIs.MSMF);
                backendName = "MSMF";
            }
            if (!_capture.IsOpened()) {
                StatusChanged?.Invoke($"Failed to open camera index {_deviceIndex} via DSHOW or MSMF.");
                _capture.Dispose();
                _capture = null;
                return;
            }

            // MJPG avoids YUY2 bandwidth cap (5 FPS at 1080p over USB 2 hubs). MSMF ignores
            // the fourcc set on most virtual cams — that's fine, virtuals don't have USB
            // bandwidth constraints anyway.
            _capture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
            _capture.Set(VideoCaptureProperties.FrameWidth, _requestedWidth);
            _capture.Set(VideoCaptureProperties.FrameHeight, _requestedHeight);
            _capture.Set(VideoCaptureProperties.Fps, _requestedFps);
            // BufferSize=1 keeps the driver from queueing stale frames. Critical for OBS
            // Virtual Camera which adds 200-300 ms of buffering by default; with BufferSize=1
            // + a Grab() drain in the read loop we get the freshest possible frame.
            try { _capture.Set(VideoCaptureProperties.BufferSize, 1); } catch { /* not all backends honour this */ }

            ActualWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
            ActualHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
            ActualFps = _capture.Get(VideoCaptureProperties.Fps);

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunLoop(_cts.Token));
            StatusChanged?.Invoke($"Capture started ({backendName}): {ActualWidth}x{ActualHeight} @ {ActualFps:0.#} FPS");
        }

        public void Stop() {
            try { _cts?.Cancel(); } catch { }
            try { _loop?.Wait(2000); } catch { }
            _capture?.Dispose();
            _capture = null;
            _cts?.Dispose();
            _cts = null;
            _loop = null;
            StatusChanged?.Invoke("Capture stopped.");
        }

        private void RunLoop(CancellationToken ct) {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!ct.IsCancellationRequested) {
                using var frame = new Mat();
                if (!_capture.Read(frame) || frame.Empty()) {
                    Thread.Sleep(5);
                    continue;
                }
                Interlocked.Increment(ref _capturedFrames);
                double ts = sw.Elapsed.TotalSeconds;
                try { FrameReady?.Invoke(frame, ts); } catch (Exception ex) {
                    StatusChanged?.Invoke($"FrameReady handler threw: {ex.Message}");
                }
            }
        }

        public void Dispose() => Stop();

        /// <summary>
        /// One enumerated capture device. Index aligns with what
        /// <see cref="VideoCapture(int, VideoCaptureAPIs)"/> expects.
        /// Note: WPF data binding only reads properties (not fields), so Index / FriendlyName
        /// must be properties to work with ComboBox DisplayMemberPath / SelectedValuePath.
        /// </summary>
        public readonly struct DeviceInfo {
            public int Index { get; }
            public string FriendlyName { get; }
            public DeviceInfo(int index, string name) {
                Index = index;
                FriendlyName = string.IsNullOrWhiteSpace(name) ? $"Camera {index}" : name;
            }
            public override string ToString() => $"{Index}: {FriendlyName}";
        }

        /// <summary>
        /// Enumerates capture devices via Windows.Devices.Enumeration (WinRT). Crucially
        /// includes virtual cameras (OBS Virtual Cam, ManyCam, NDI Tools, etc.) — WMI's
        /// PnPEntity query does not, because virtual cameras are DirectShow / Media-Foundation
        /// software filters, not PnP entities.
        ///
        /// Index assignment is sequential (0..N-1) matching the WinRT enumeration order. WinRT
        /// sits on top of Media Foundation, which generally matches DirectShow's filter graph
        /// order — but on multi-virtual-cam setups they can drift. If the user reports the
        /// wrong device opens, the workaround is to scroll through the dropdown until the
        /// preview shows the right feed.
        /// </summary>
        public static DeviceInfo[] EnumerateDeviceInfos() {
            var list = new System.Collections.Generic.List<DeviceInfo>();

            // 1. DirectShow filter graph (primary). Sees DSHOW-only virtual cameras
            //    (OBS Virtual Cam, NDI Tools, ManyCam, XSplit Vcam) AND physical USB cams.
            //    Index order matches what VideoCapture(idx, DSHOW) opens, so what the user
            //    picks in the dropdown is exactly what gets opened.
            try {
                var dsNames = DirectShowDeviceEnum.GetVideoInputNames();
                for (int i = 0; i < dsNames.Count; i++) {
                    list.Add(new DeviceInfo(i, dsNames[i]));
                }
            } catch { /* fall through */ }

            // 2. WinRT MediaFoundation fallback (only used when DSHOW enum returned nothing).
            //    Won't see DSHOW-only filters but works on locked-down systems where COM
            //    activation is restricted.
            if (list.Count == 0) {
                try {
                    var task = Windows.Devices.Enumeration.DeviceInformation
                        .FindAllAsync(Windows.Devices.Enumeration.DeviceClass.VideoCapture)
                        .AsTask();
                    if (task.Wait(3000)) {
                        int idx = 0;
                        foreach (var d in task.Result) {
                            string name = d?.Name ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(name)) name = $"Camera {idx}";
                            list.Add(new DeviceInfo(idx, name));
                            idx++;
                        }
                    }
                } catch { }
            }

            // 3. WMI PnP fallback (last resort — physical cameras only).
            if (list.Count == 0) {
                try {
                    using var searcher = new System.Management.ManagementObjectSearcher(
                        "SELECT Name, PNPClass FROM Win32_PnPEntity WHERE PNPClass='Camera' OR PNPClass='Image'");
                    int idx = 0;
                    foreach (var item in searcher.Get()) {
                        string name = item["Name"] as string ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        list.Add(new DeviceInfo(idx, name));
                        idx++;
                    }
                } catch { }
            }

            if (list.Count == 0) {
                for (int i = 0; i < 2; i++) list.Add(new DeviceInfo(i, $"Camera {i}"));
            }
            return list.ToArray();
        }

        /// <summary>
        /// Backward-compat wrapper. Returns just the indices. Now WMI-backed so it is fast
        /// and non-blocking (no DSHOW probe).
        /// </summary>
        public static int[] EnumerateDevices(int maxProbe = 8) {
            var infos = EnumerateDeviceInfos();
            var arr = new int[infos.Length];
            for (int i = 0; i < infos.Length; i++) arr[i] = infos[i].Index;
            return arr;
        }
    }
}
