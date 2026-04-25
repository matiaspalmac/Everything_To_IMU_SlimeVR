using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everything_To_IMU_SlimeVR.Tracking.Camera;
using Everything_To_IMU_SlimeVR.UI.Services;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace Everything_To_IMU_SlimeVR.UI.ViewModels;

public partial class CameraSettingsViewModel : ObservableObject
{
    public record CapturePreset(string Label, int Width, int Height, int Fps);
    public static readonly IReadOnlyList<CapturePreset> Presets = new[] {
        new CapturePreset("720p @ 30 FPS (recommended)", 1280, 720, 30),
        new CapturePreset("720p @ 60 FPS", 1280, 720, 60),
        new CapturePreset("1080p @ 30 FPS", 1920, 1080, 30),
        new CapturePreset("1080p @ 60 FPS", 1920, 1080, 60),
        new CapturePreset("480p @ 30 FPS (low CPU)", 640, 480, 30),
        new CapturePreset("Custom", 0, 0, 0),
    };
    public IReadOnlyList<CapturePreset> AvailablePresets => Presets;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private int _deviceIndex;
    [ObservableProperty] private int _captureWidth = 1280;
    [ObservableProperty] private int _captureHeight = 720;
    [ObservableProperty] private int _captureFps = 30;
    [ObservableProperty] private bool _mirrorPreview;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPreset))]
    [NotifyPropertyChangedFor(nameof(IsVirtualCameraSelected))]
    private CapturePreset _selectedPreset;
    public bool IsCustomPreset => SelectedPreset?.Label == "Custom";
    public bool IsVirtualCameraSelected {
        get {
            string n = SelectedDeviceName?.ToLowerInvariant() ?? string.Empty;
            return n.Contains("obs") || n.Contains("ndi") || n.Contains("manycam")
                || n.Contains("xsplit") || n.Contains("vcam") || n.Contains("droidcam")
                || n.Contains("iriun") || n.Contains("camo") || n.Contains("virtual");
        }
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRescan))]
    private bool _isRescanning;
    public bool CanRescan => !IsRescanning;

    [ObservableProperty] private string _statusMessage = "Idle.";
    [ObservableProperty] private double _liveFps;
    [ObservableProperty] private double _inferenceMs;
    [ObservableProperty] private long _framesProcessed;
    [ObservableProperty] private long _framesDropped;
    [ObservableProperty] private bool _calibrated;
    [ObservableProperty] private string _modelLayout = "—";
    [ObservableProperty] private bool _hasWorldLandmarks;
    [ObservableProperty] private string _captureFormat = "—";
    [ObservableProperty] private WriteableBitmap? _previewBitmap;

    public ObservableCollection<WebcamCaptureLoop.DeviceInfo> AvailableDevices { get; } = new();

    private readonly DispatcherTimer _statsTimer;
    private long _lastFrameRenderTicks;
    private const int PreviewMaxWidth = 640; // downscale preview to limit dispatcher payload
    private const int PreviewFps = 15;        // half of capture rate — eyes can't tell the difference

    public CameraSettingsViewModel()
    {
        var cfg = AppServices.Instance.Configuration;
        if (cfg != null) {
            _enabled = cfg.WebcamPoseEnabled;
            _deviceIndex = cfg.WebcamPoseDeviceIndex;
            _captureWidth = cfg.WebcamPoseWidth;
            _captureHeight = cfg.WebcamPoseHeight;
            _captureFps = cfg.WebcamPoseFps;
        }
        _selectedPreset = ResolvePresetFor(_captureWidth, _captureHeight, _captureFps);

        var mgr = WebcamPoseManager.Instance;
        mgr.StatusChanged += OnManagerStatus;
        mgr.RawFrameReady += OnRawFrame;
        mgr.TPoseAutoCaptured += OnAutoCaptured;

        // Refresh stats at 4 Hz — captures FPS / dropped without burning the UI thread.
        _statsTimer = new DispatcherTimer(DispatcherPriority.Background) {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _statsTimer.Tick += (_, _) => RefreshStats();
        _statsTimer.Start();

        _ = RefreshDevices();
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RefreshDevices()
    {
        if (IsRescanning) return;
        IsRescanning = true;
        StatusMessage = "Scanning cameras…";
        try {
            // Run on background thread — WMI query is fast (<100 ms typical) but we keep
            // the UI responsive even if the system is under load.
            var found = await System.Threading.Tasks.Task.Run(() => WebcamCaptureLoop.EnumerateDeviceInfos());

            AvailableDevices.Clear();
            foreach (var d in found) AvailableDevices.Add(d);

            // Preserve current selection if still present, otherwise pick the first.
            int desired = DeviceIndex;
            bool stillPresent = false;
            foreach (var d in AvailableDevices) if (d.Index == desired) { stillPresent = true; break; }
            if (!stillPresent && AvailableDevices.Count > 0) DeviceIndex = AvailableDevices[0].Index;

            StatusMessage = AvailableDevices.Count == 0
                ? "No cameras found."
                : $"Found {AvailableDevices.Count} camera(s).";
        } catch (Exception ex) {
            StatusMessage = $"Device enumeration failed: {ex.Message}";
        } finally {
            IsRescanning = false;
        }
    }

    /// <summary>
    /// Returns the friendly name for the currently-selected DeviceIndex (or empty string).
    /// </summary>
    public string SelectedDeviceName {
        get {
            foreach (var d in AvailableDevices) if (d.Index == DeviceIndex) return d.FriendlyName;
            return string.Empty;
        }
    }

    [RelayCommand]
    private void CaptureTPose()
    {
        // Force a manual capture by bouncing the AutoCapture flag — Apply() will fire on the
        // next valid frame. Cleaner than reaching into private state.
        var calib = WebcamPoseManager.Instance.Calibration;
        calib.Reset();
        calib.AutoCaptureEnabled = true;
        StatusMessage = "Calibration: stand still in T-pose for 1.5s…";
    }

    [RelayCommand]
    private void ResetCalibration()
    {
        WebcamPoseManager.Instance.ResetCalibration();
        Calibrated = false;
        StatusMessage = "Calibration cleared.";
    }

    partial void OnEnabledChanged(bool value)
    {
        Persist(c => c.WebcamPoseEnabled = value);
        if (value) {
            WebcamPoseManager.Instance.Start(DeviceIndex, captureWidth: CaptureWidth, captureHeight: CaptureHeight, fps: CaptureFps);
        } else {
            WebcamPoseManager.Instance.Stop();
        }
    }

    partial void OnDeviceIndexChanged(int value) {
        Persist(c => c.WebcamPoseDeviceIndex = value);
        OnPropertyChanged(nameof(SelectedDeviceName));
        OnPropertyChanged(nameof(IsVirtualCameraSelected));
        // Hot-swap the camera if the pipeline is running. Stop fully so the previous capture
        // releases its DSHOW handle before we try to open the new one.
        if (Enabled && WebcamPoseManager.Instance.Running) {
            WebcamPoseManager.Instance.Stop();
            PreviewBitmap = null; // visual feedback that switch is in progress
            // Tiny delay so DSHOW unbinds the old device cleanly before the new open. 150 ms
            // is enough on every Win11 machine I tested; shorter occasionally races.
            System.Threading.Tasks.Task.Delay(150).ContinueWith(_ => {
                Application.Current?.Dispatcher.BeginInvoke(() => {
                    WebcamPoseManager.Instance.Start(value, captureWidth: CaptureWidth, captureHeight: CaptureHeight, fps: CaptureFps);
                });
            });
            StatusMessage = "Switching camera…";
        }
    }
    partial void OnCaptureWidthChanged(int value) { Persist(c => c.WebcamPoseWidth = value); ScheduleRestart(); }
    partial void OnCaptureHeightChanged(int value) { Persist(c => c.WebcamPoseHeight = value); ScheduleRestart(); }
    partial void OnCaptureFpsChanged(int value) { Persist(c => c.WebcamPoseFps = value); ScheduleRestart(); }

    partial void OnSelectedPresetChanged(CapturePreset value) {
        if (value == null || value.Label == "Custom") return; // keep manual values
        // Setting these triggers their own change handlers which schedule a single
        // debounced restart — fine, the debounce coalesces all three property writes.
        CaptureWidth = value.Width;
        CaptureHeight = value.Height;
        CaptureFps = value.Fps;
    }

    private CapturePreset ResolvePresetFor(int w, int h, int fps) {
        foreach (var p in Presets) {
            if (p.Width == w && p.Height == h && p.Fps == fps) return p;
        }
        return Presets[Presets.Count - 1]; // Custom
    }

    // Debounced restart for resolution / FPS changes — fires once after the user stops
    // editing for ~600 ms. Without this, dragging a NumberBox up/down restarts the pipeline
    // on every tick (50+ times per drag) which is both pointless and ugly.
    private System.Threading.Timer? _restartDebounce;
    private const int RestartDebounceMs = 600;
    private void ScheduleRestart() {
        if (!Enabled || !WebcamPoseManager.Instance.Running) return;
        if (_restartDebounce == null) {
            _restartDebounce = new System.Threading.Timer(_ => {
                Application.Current?.Dispatcher.BeginInvoke(() => {
                    if (!Enabled) return;
                    var mgr = WebcamPoseManager.Instance;
                    mgr.Stop();
                    PreviewBitmap = null;
                    System.Threading.Tasks.Task.Delay(150).ContinueWith(_ => {
                        Application.Current?.Dispatcher.BeginInvoke(() => {
                            if (!Enabled) return;
                            mgr.Start(DeviceIndex, captureWidth: CaptureWidth, captureHeight: CaptureHeight, fps: CaptureFps);
                        });
                    });
                    StatusMessage = $"Applying {CaptureWidth}x{CaptureHeight} @ {CaptureFps} FPS…";
                });
            }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }
        _restartDebounce.Change(RestartDebounceMs, System.Threading.Timeout.Infinite);
    }

    private void OnManagerStatus(string msg) {
        Application.Current?.Dispatcher.BeginInvoke(() => StatusMessage = msg);
    }

    private void OnAutoCaptured() {
        Application.Current?.Dispatcher.BeginInvoke(() => {
            Calibrated = true;
            StatusMessage = "T-pose captured.";
        });
    }

    private void OnRawFrame(Mat mat, double timestampSeconds) {
        // Cap preview render rate. 15 FPS thumbnail is plenty.
        long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (PreviewBitmap != null && ticks - _lastFrameRenderTicks < System.Diagnostics.Stopwatch.Frequency / PreviewFps) return;
        _lastFrameRenderTicks = ticks;

        // Downscale on the capture thread BEFORE crossing dispatcher. 1280x720 BGR = 2.6 MB
        // per frame; at 30 FPS that's 78 MB/s of GC pressure if we cloned full-res. Resize to
        // at most 640px wide → 0.6 MB → 9 MB/s. Aspect preserved.
        Mat preview;
        if (mat.Width > PreviewMaxWidth) {
            int newW = PreviewMaxWidth;
            int newH = (int)Math.Round(mat.Height * (double)newW / mat.Width);
            preview = new Mat();
            Cv2.Resize(mat, preview, new OpenCvSharp.Size(newW, newH), 0, 0, InterpolationFlags.Area);
        } else {
            preview = mat.Clone();
        }
        if (MirrorPreview) {
            Cv2.Flip(preview, preview, FlipMode.Y);
        }

        Application.Current?.Dispatcher.BeginInvoke(() => {
            try {
                using (preview) {
                    if (PreviewBitmap == null ||
                        PreviewBitmap.PixelWidth != preview.Width ||
                        PreviewBitmap.PixelHeight != preview.Height) {
                        PreviewBitmap = new WriteableBitmap(preview.Width, preview.Height, 96, 96,
                            System.Windows.Media.PixelFormats.Bgr24, null);
                    }
                    WriteableBitmapConverter.ToWriteableBitmap(preview, PreviewBitmap);
                }
            } catch (Exception ex) {
                StatusMessage = $"Preview error: {ex.Message}";
            }
        }, DispatcherPriority.Background);
    }

    private void RefreshStats() {
        var mgr = WebcamPoseManager.Instance;
        FramesProcessed = mgr.FramesProcessed;
        FramesDropped = mgr.FramesDropped;
        InferenceMs = mgr.LastInferenceMs;
        Calibrated = mgr.Calibration.Calibrated;
        var inf = mgr.Inference;
        if (inf != null) {
            ModelLayout = inf.ModelLayout;
            HasWorldLandmarks = inf.HasWorldLandmarks;
        }
        var cap = mgr.Capture;
        if (cap != null) {
            LiveFps = cap.ActualFps;
            CaptureFormat = $"{cap.ActualWidth}x{cap.ActualHeight} @ {cap.ActualFps:0.#} FPS";
        } else {
            CaptureFormat = "—";
        }
    }

    private static void Persist(Action<Configuration> mutate) {
        var cfg = AppServices.Instance.Configuration;
        if (cfg == null) return;
        try { mutate(cfg); cfg.SaveDebounced(); } catch { }
    }
}
