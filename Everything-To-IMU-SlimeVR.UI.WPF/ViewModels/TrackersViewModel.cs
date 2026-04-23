using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everything_To_IMU_SlimeVR.Tracking;
using Everything_To_IMU_SlimeVR.UI.Services;
using static Everything_To_IMU_SlimeVR.TrackerConfig;

namespace Everything_To_IMU_SlimeVR.UI.ViewModels;

public partial class TrackersViewModel : ObservableObject
{
    private readonly DispatcherTimer _refreshTimer;

    public ObservableCollection<TrackerRow> Trackers { get; } = new();

    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isHintOpen = true;

    [RelayCommand]
    private void DismissHint() => IsHintOpen = false;

    [RelayCommand]
    private void RescanDevices()
    {
        // Force re-enumeration by toggling lockInDetectedDevices briefly.
        GenericTrackerManager.lockInDetectedDevices = false;
    }

    [RelayCommand]
    private void OpenWindowsBluetooth()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:bluetooth",
                UseShellExecute = true,
            });
        }
        catch { }
    }

    public IReadOnlyList<RotationReferenceType> YawSources { get; } =
        Enum.GetValues<RotationReferenceType>();

    public IReadOnlyList<HapticNodeBinding> HapticNodes { get; } =
        Enum.GetValues<HapticNodeBinding>();

    [ObservableProperty]
    private TrackerRow? _selectedTracker;

    partial void OnSelectedTrackerChanged(TrackerRow? value)
    {
        OnPropertyChanged(nameof(SelectedYawSource));
        OnPropertyChanged(nameof(SelectedExtensionYawSource));
        OnPropertyChanged(nameof(SelectedHapticNode));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedMountYawText));
        OnPropertyChanged(nameof(SelectedGyroTrim));
        OnPropertyChanged(nameof(SelectedGyroTrimText));
        AppServices.Instance.SelectedTracker = value?.Tracker;
        AppServices.Instance.RaiseSelectedChanged();
    }

    public bool HasSelection => SelectedTracker != null;

    public string SelectedMountYawText =>
        SelectedTracker == null ? "0°" :
        (Everything_To_IMU_SlimeVR.Configuration.Instance?.GetMountYawDegrees(SelectedTracker.Tracker.MacSpoof) ?? 0) + "°";

    public double SelectedGyroTrim
    {
        get => SelectedTracker == null ? 1.0 :
            (Everything_To_IMU_SlimeVR.Configuration.Instance?.GetGyroScaleTrim(SelectedTracker.Tracker.MacSpoof) ?? 1.0f);
        set
        {
            if (SelectedTracker == null) return;
            try
            {
                Everything_To_IMU_SlimeVR.Configuration.Instance?.SetGyroScaleTrim(SelectedTracker.Tracker.MacSpoof, (float)value);
                OnPropertyChanged(nameof(SelectedGyroTrimText));
            }
            catch { }
        }
    }

    public string SelectedGyroTrimText =>
        SelectedTracker == null ? "1.000" :
        (Everything_To_IMU_SlimeVR.Configuration.Instance?.GetGyroScaleTrim(SelectedTracker.Tracker.MacSpoof) ?? 1.0f).ToString("F3");

    [RelayCommand]
    private void RotateMount()
    {
        if (SelectedTracker == null) return;
        try
        {
            Everything_To_IMU_SlimeVR.Configuration.Instance?.BumpMountYaw(SelectedTracker.Tracker.MacSpoof, 90);
            SelectedTracker.Refresh();
            OnPropertyChanged(nameof(SelectedMountYawText));
        }
        catch { }
    }

    public RotationReferenceType SelectedYawSource
    {
        get => SelectedTracker?.Tracker.YawReferenceTypeValue ?? RotationReferenceType.HmdRotation;
        set
        {
            if (SelectedTracker == null) return;
            SelectedTracker.Tracker.YawReferenceTypeValue = value;
            PersistCurrentTrackerConfig();
            OnPropertyChanged();
        }
    }

    public RotationReferenceType SelectedExtensionYawSource
    {
        get => SelectedTracker?.Tracker.ExtensionYawReferenceTypeValue ?? RotationReferenceType.HmdRotation;
        set
        {
            if (SelectedTracker == null) return;
            SelectedTracker.Tracker.ExtensionYawReferenceTypeValue = value;
            PersistCurrentTrackerConfig();
            OnPropertyChanged();
        }
    }

    public HapticNodeBinding SelectedHapticNode
    {
        get => SelectedTracker?.Tracker.HapticNodeBinding ?? default;
        set
        {
            if (SelectedTracker == null) return;
            SelectedTracker.Tracker.HapticNodeBinding = value;
            PersistCurrentTrackerConfig();
            OnPropertyChanged();
        }
    }

    public TrackersViewModel()
    {
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _refreshTimer.Tick += (_, _) => RefreshList();
        _refreshTimer.Start();
        RefreshList();
    }

    private void RefreshList()
    {
        var live = new List<(IBodyTracker tracker, TrackerKind kind)>();
        foreach (var t in GenericTrackerManager.TrackersBluetooth) live.Add((t, TrackerKind.DualSense));
        foreach (var t in GenericTrackerManager.TrackersJoyCon2) live.Add((t, TrackerKind.JoyCon2));
        foreach (var t in GenericTrackerManager.TrackersWiimote) live.Add((t, TrackerKind.Wiimote));
        foreach (var t in GenericTrackerManager.Trackers3ds) live.Add((t, TrackerKind.ThreeDs));
        foreach (var kv in GenericTrackerManager.TrackersUdpHapticDevice) live.Add((kv.Value, TrackerKind.UdpHaptic));

        var existing = Trackers.ToDictionary(r => r.Tracker);
        var liveSet = live.Select(x => x.tracker).ToHashSet();

        // remove gone
        for (int i = Trackers.Count - 1; i >= 0; i--)
        {
            if (!liveSet.Contains(Trackers[i].Tracker))
                Trackers.RemoveAt(i);
        }

        // add new
        foreach (var (tracker, kind) in live)
        {
            if (!existing.ContainsKey(tracker))
                Trackers.Add(new TrackerRow(tracker, kind));
        }

        foreach (var row in Trackers) row.Refresh();
        IsEmpty = Trackers.Count == 0;
        AppServices.Instance.CheckBatteryLevels();
    }

    private void PersistCurrentTrackerConfig()
    {
        try { AppServices.Instance.Configuration?.SaveConfig(); } catch { }
    }

    [RelayCommand]
    private void Identify()
    {
        try { SelectedTracker?.Tracker.Identify(); } catch { }
    }

    [RelayCommand]
    private void Rediscover()
    {
        try { SelectedTracker?.Tracker.Rediscover(); } catch { }
    }

    [RelayCommand]
    private void Recalibrate()
    {
        // Original WinForms app had a single Recalibrate button that fires the tracker's
        // Recalibrate() directly. No UI countdown / overlay / timestamp tracking here — those
        // were my additions that duplicated SlimeVR dashboard responsibilities and got removed.
        if (SelectedTracker?.Tracker is not { } tracker) return;
        try
        {
            if (tracker is GenericControllerTracker g) g.Recalibrate();
            else if (tracker is WiiTracker w) w.Recalibrate();
            else if (tracker is ThreeDsControllerTracker d) d.Recalibrate();
        }
        catch { }
    }

    [RelayCommand]
    private void CalibrateHaptics()
    {
        if (SelectedTracker?.Tracker is not IBodyTracker tracker) return;
        if (!tracker.SupportsHaptics) return;
        var dlg = new Views.HapticCalibratorDialog(tracker)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        dlg.ShowDialog();
    }
}
