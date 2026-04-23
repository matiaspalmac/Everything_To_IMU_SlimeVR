using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Everything_To_IMU_SlimeVR.Tracking;
using Everything_To_IMU_SlimeVR.UI.Services;

namespace Everything_To_IMU_SlimeVR.UI.ViewModels;

public enum TrackerKind { DualSense, Wiimote, ThreeDs, UdpHaptic, JoyCon2 }

public partial class TrackerRow : ObservableObject
{
    public IBodyTracker Tracker { get; }
    public TrackerKind Kind { get; }

    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _typeLabel = string.Empty;
    [ObservableProperty] private string _typeIcon = string.Empty;
    [ObservableProperty] private bool _ready;
    [ObservableProperty] private bool _supportsHaptics;
    [ObservableProperty] private bool _supportsImu;
    [ObservableProperty] private string _statusText = "Connecting";
    [ObservableProperty] private Brush _statusBrush = Brushes.Gray;
    [ObservableProperty] private string _lastCalibratedText = "—";
    [ObservableProperty] private string _batteryText = "—";
    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private Brush _batteryBrush = Brushes.Gray;
    [ObservableProperty] private string _hzText = "—";
    [ObservableProperty] private string _mountYawText = "0°";
    [ObservableProperty] private bool _supportsGyroTrim;

    public TrackerRow(IBodyTracker tracker, TrackerKind kind)
    {
        Tracker = tracker;
        Kind = kind;
        Id = tracker.ToString() ?? "(unnamed)";
        (TypeLabel, TypeIcon) = kind switch
        {
            TrackerKind.DualSense => ("DualSense / Pro", "Games24"),
            TrackerKind.Wiimote => ("Wiimote", "GameChat24"),
            TrackerKind.ThreeDs => ("3DS", "Device24"),
            TrackerKind.UdpHaptic => ("UDP Haptic", "Pulse24"),
            TrackerKind.JoyCon2 => ("Joy-Con 2 / Switch 2", "Games24"),
            _ => ("Unknown", "QuestionCircle24"),
        };
        SupportsHaptics = tracker.SupportsHaptics;
        SupportsImu = tracker.SupportsIMU;
        // Gyro trim only meaningful for trackers with no factory cal we can read. JSL applies
        // factory cal automatically for the PS / Switch family so the user trim would fight it;
        // BLE Joy-Con 2 has no exposed cal so we let users dial it.
        SupportsGyroTrim = kind == TrackerKind.JoyCon2;
        Refresh();
    }

    public void Refresh()
    {
        Ready = Tracker.Ready;
        (StatusText, StatusBrush) = ComputeStatus();
        RefreshBattery();
        RefreshHz();
        try
        {
            int deg = Everything_To_IMU_SlimeVR.Configuration.Instance?.GetMountYawDegrees(Tracker.MacSpoof) ?? 0;
            MountYawText = deg + "°";
        }
        catch { MountYawText = "0°"; }
    }

    private void RefreshHz()
    {
        try
        {
            var prop = Tracker.GetType().GetProperty("Hz");
            if (prop?.PropertyType == typeof(double) && prop.GetValue(Tracker) is double hz && hz > 0)
            {
                HzText = $"{hz:F0} Hz";
            }
            else
            {
                HzText = "—";
            }
        }
        catch
        {
            HzText = "—";
        }
    }

    private void RefreshBattery()
    {
        try
        {
            var prop = Tracker.GetType().GetProperty("BatteryLevel");
            if (prop?.PropertyType == typeof(float) && prop.GetValue(Tracker) is float f && f > 0f)
            {
                HasBattery = true;
                int pct = (int)Math.Round(Math.Clamp(f, 0f, 1f) * 100);
                BatteryText = $"{pct}%";
                var res = System.Windows.Application.Current.Resources;
                BatteryBrush = pct <= 15
                    ? (Brush)(res["ErrorBrush"] ?? Brushes.Red)
                    : pct <= 35
                        ? (Brush)(res["WarningBrush"] ?? Brushes.Orange)
                        : (Brush)(res["SuccessBrush"] ?? Brushes.Green);
            }
            else
            {
                HasBattery = false;
                BatteryText = "—";
            }
        }
        catch
        {
            HasBattery = false;
            BatteryText = "—";
        }
    }

    // Sample rate floor below which a tracker is considered "Laggy". 60 Hz is the practical
    // minimum for VR head/limb tracking before motion lag becomes obvious; SlimeVR's own ESP
    // firmware streams at ~120 Hz so anything sustained above 60 is fine.
    private const double HealthyHzThreshold = 60.0;

    private (string, Brush) ComputeStatus()
    {
        var res = System.Windows.Application.Current.Resources;
        var green = (Brush)(res["SuccessBrush"] ?? Brushes.Green);
        var gray = (Brush)(res["TextTertiaryBrush"] ?? Brushes.Gray);
        var amber = (Brush)(res["WarningBrush"] ?? Brushes.Orange);
        var red = (Brush)(res["ErrorBrush"] ?? Brushes.Red);

        // Disconnected check via reflection — only some trackers expose the property.
        try
        {
            var disc = Tracker.GetType().GetProperty("Disconnected");
            if (disc?.GetValue(Tracker) is bool d && d) return ("Disconnected", red);
        }
        catch { }

        if (!Tracker.Ready) return ("Connecting", gray);
        if (!SupportsImu) return ("Haptic only", green);

        double hz = 0;
        try
        {
            var prop = Tracker.GetType().GetProperty("Hz");
            if (prop?.PropertyType == typeof(double) && prop.GetValue(Tracker) is double v) hz = v;
        }
        catch { }

        if (hz <= 0) return ("No IMU", amber);
        if (hz < HealthyHzThreshold) return ($"Laggy ({hz:F0} Hz)", amber);
        return ("Healthy", green);
    }

    private string ComputeLastCalibrated()
    {
        return "—";
    }
}
