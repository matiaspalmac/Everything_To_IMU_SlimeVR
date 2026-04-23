using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Everything_To_IMU_SlimeVR.Tracking;
using Everything_To_IMU_SlimeVR.UI.Services;

namespace Everything_To_IMU_SlimeVR.UI.ViewModels;

public enum TrackerKind { DualSense, Wiimote, ThreeDs, UdpHaptic }

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
            _ => ("Unknown", "QuestionCircle24"),
        };
        SupportsHaptics = tracker.SupportsHaptics;
        SupportsImu = tracker.SupportsIMU;
        Refresh();
    }

    public void Refresh()
    {
        Ready = Tracker.Ready;
        (StatusText, StatusBrush) = ComputeStatus();
        RefreshBattery();
        RefreshHz();
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

    private (string, Brush) ComputeStatus()
    {
        var green = (Brush)(System.Windows.Application.Current.Resources["SuccessBrush"] ?? Brushes.Green);
        var gray = (Brush)(System.Windows.Application.Current.Resources["TextTertiaryBrush"] ?? Brushes.Gray);

        if (!Tracker.Ready) return ("Connecting", gray);
        if (!SupportsImu) return ("Haptic only", green);
        return ("Streaming", green);
    }

    private string ComputeLastCalibrated()
    {
        return "—";
    }
}
