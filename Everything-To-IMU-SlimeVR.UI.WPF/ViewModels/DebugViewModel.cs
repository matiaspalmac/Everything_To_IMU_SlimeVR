using System.Collections.ObjectModel;
using System.Numerics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Everything_To_IMU_SlimeVR.Tracking;
using Everything_To_IMU_SlimeVR.UI.Services;

namespace Everything_To_IMU_SlimeVR.UI.ViewModels;

public partial class DebugViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;

    public ObservableCollection<IBodyTracker> AvailableTrackers { get; } = new();

    private IBodyTracker? _selectedTrackerItem;
    public IBodyTracker? SelectedTrackerItem
    {
        get => _selectedTrackerItem;
        set
        {
            if (_selectedTrackerItem == value) return;
            _selectedTrackerItem = value;
            AppServices.Instance.SelectedTracker = value;
            AppServices.Instance.RaiseSelectedChanged();
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private string _debugText = "Select a tracker.";
    [ObservableProperty] private string _trackerName = "(none)";
    [ObservableProperty] private double _euX;
    [ObservableProperty] private double _euY;
    [ObservableProperty] private double _euZ;
    [ObservableProperty] private double _euDegX;
    [ObservableProperty] private double _euDegY;
    [ObservableProperty] private double _euDegZ;
    [ObservableProperty] private double _accX;
    [ObservableProperty] private double _accY;
    [ObservableProperty] private double _accZ;

    [ObservableProperty] private bool _isPaused;

    // Debug chips
    [ObservableProperty] private string _peakText = "—";
    [ObservableProperty] private string _jitterText = "—";
    [ObservableProperty] private string _rateText = "—";
    [ObservableProperty] private bool _rateWarning;
    [ObservableProperty] private bool _spikeAlert;
    [ObservableProperty] private bool _isDisconnected;
    [ObservableProperty] private string _disconnectedAtText = "";

    // Protocol telemetry
    [ObservableProperty] private string _packetsSentText = "—";
    [ObservableProperty] private string _packetDropsText = "—";
    [ObservableProperty] private string _serverReachableText = "—";
    [ObservableProperty] private bool _hasPacketDrops;

    private readonly Queue<(DateTime t, Vector3 e)> _history = new();
    private DateTime _lastSampleTime;
    private DateTime _lastSpikeTime;
    private DateTime _lastDisconnectTime;

    public event EventHandler<Vector3>? SampleReady;
    public event EventHandler? ClearRequested;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ClearChart() => ClearRequested?.Invoke(this, EventArgs.Empty);

    public DebugViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        RefreshAvailable();
        _selectedTrackerItem = AppServices.Instance.SelectedTracker;
    }

    private void RefreshAvailable()
    {
        var live = new List<IBodyTracker>();
        foreach (var t in GenericTrackerManager.TrackersBluetooth) live.Add(t);
        foreach (var t in GenericTrackerManager.TrackersWiimote) live.Add(t);
        foreach (var t in GenericTrackerManager.Trackers3ds) live.Add(t);
        foreach (var kv in GenericTrackerManager.TrackersUdpHapticDevice) live.Add(kv.Value);

        var liveSet = new HashSet<IBodyTracker>(live);
        for (int i = AvailableTrackers.Count - 1; i >= 0; i--)
            if (!liveSet.Contains(AvailableTrackers[i])) AvailableTrackers.RemoveAt(i);

        var existing = new HashSet<IBodyTracker>(AvailableTrackers);
        foreach (var t in live)
            if (!existing.Contains(t)) AvailableTrackers.Add(t);

        // If nothing selected but trackers exist, pick the first one automatically
        if (_selectedTrackerItem == null && AvailableTrackers.Count > 0)
            SelectedTrackerItem = AvailableTrackers[0];

        // Or sync from AppServices if user picked in Trackers page
        if (AppServices.Instance.SelectedTracker != null
            && AppServices.Instance.SelectedTracker != _selectedTrackerItem)
        {
            _selectedTrackerItem = AppServices.Instance.SelectedTracker;
            OnPropertyChanged(nameof(SelectedTrackerItem));
        }
    }

    private int _refreshCounter;
    private void Tick()
    {
        if (++_refreshCounter % 10 == 0) RefreshAvailable(); // every ~500ms

        var tracker = AppServices.Instance.SelectedTracker;
        if (tracker == null)
        {
            TrackerName = "(no tracker selected)";
            DebugText = "Select a tracker in the Trackers page to see live data.";
            return;
        }
        TrackerName = tracker.ToString() ?? "(unnamed)";
        DebugText = tracker.Debug ?? string.Empty;

        var e = tracker.Euler;
        EuX = e.X; EuY = e.Y; EuZ = e.Z;
        const double r2d = 180.0 / Math.PI;
        EuDegX = e.X * r2d; EuDegY = e.Y * r2d; EuDegZ = e.Z * r2d;

        try
        {
            var accelProp = tracker.GetType().GetProperty("Acceleration");
            if (accelProp?.GetValue(tracker) is Vector3 a)
            {
                AccX = a.X; AccY = a.Y; AccZ = a.Z;
            }
        }
        catch { }

        // Chips: rolling stats over last 10s
        var now = DateTime.UtcNow;
        _history.Enqueue((now, e));
        while (_history.Count > 0 && (now - _history.Peek().t).TotalSeconds > 10) _history.Dequeue();

        double peakX = 0, peakY = 0, peakZ = 0;
        double sumX = 0, sumY = 0, sumZ = 0;
        double sqX = 0, sqY = 0, sqZ = 0;
        int n = _history.Count;
        foreach (var (_, v) in _history)
        {
            peakX = Math.Max(peakX, Math.Abs(v.X));
            peakY = Math.Max(peakY, Math.Abs(v.Y));
            peakZ = Math.Max(peakZ, Math.Abs(v.Z));
            sumX += v.X; sumY += v.Y; sumZ += v.Z;
            sqX += v.X * v.X; sqY += v.Y * v.Y; sqZ += v.Z * v.Z;
        }
        if (n > 1)
        {
            double mx = sumX / n, my = sumY / n, mz = sumZ / n;
            double sx = Math.Sqrt(sqX / n - mx * mx);
            double sy = Math.Sqrt(sqY / n - my * my);
            double sz = Math.Sqrt(sqZ / n - mz * mz);
            JitterText = $"σ {sx:F3}/{sy:F3}/{sz:F3}";
        }
        PeakText = $"|max| {peakX:F2}/{peakY:F2}/{peakZ:F2}";

        // Rate: samples/sec over last second
        int last1s = 0;
        foreach (var (t, _) in _history)
            if ((now - t).TotalSeconds <= 1.0) last1s++;
        RateText = $"{last1s} Hz";
        // Expected ~20Hz (50ms tick). Warn if <16Hz (80%).
        RateWarning = last1s > 0 && last1s < 16;

        // Δt between samples
        if (_lastSampleTime != default)
        {
            var dt = (now - _lastSampleTime).TotalMilliseconds;
            // Disconnect detection: gap >500ms while previously streaming
            if (dt > 500 && !IsDisconnected)
            {
                IsDisconnected = true;
                _lastDisconnectTime = now;
                DisconnectedAtText = $"Signal lost @ {DateTime.Now:HH:mm:ss}";
            }
            else if (dt < 200 && IsDisconnected)
            {
                IsDisconnected = false;
            }
        }
        _lastSampleTime = now;

        // Spike: |accel| > 3g
        double accelMag = Math.Sqrt(AccX * AccX + AccY * AccY + AccZ * AccZ);
        if (accelMag > 3.0)
        {
            SpikeAlert = true;
            _lastSpikeTime = now;
        }
        else if (SpikeAlert && (now - _lastSpikeTime).TotalMilliseconds > 800)
        {
            SpikeAlert = false;
        }

        // Protocol telemetry snapshot
        PacketsSentText = $"{tracker.PacketsSent:N0} sent";
        var drops = tracker.SendFailures;
        HasPacketDrops = drops > 0;
        PacketDropsText = drops == 0 ? "0 drops" : $"{drops:N0} drops";
        ServerReachableText = tracker.ServerReachable ? "server ✓" : "server ✗";

        if (!IsPaused) SampleReady?.Invoke(this, e);
    }
}
