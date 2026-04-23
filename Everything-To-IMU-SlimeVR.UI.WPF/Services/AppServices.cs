using Everything_To_IMU_SlimeVR;
using Everything_To_IMU_SlimeVR.Tracking;

namespace Everything_To_IMU_SlimeVR.UI.Services;

public sealed class AppServices
{
    public static AppServices Instance { get; } = new();

    public Configuration Configuration { get; private set; } = null!;
    public GenericTrackerManager TrackerManager { get; private set; } = null!;
    public ForwardedWiimoteManager WiimoteManager { get; private set; } = null!;
    public Forwarded3DSDataManager ThreeDsManager { get; private set; } = null!;

    public IBodyTracker? SelectedTracker { get; set; }
    public event EventHandler? SelectedTrackerChanged;
    public void RaiseSelectedChanged() => SelectedTrackerChanged?.Invoke(this, EventArgs.Empty);

    public Dictionary<IBodyTracker, DateTime> CalibrationTimestamps { get; } = new();

    public event EventHandler<BatteryLowEventArgs>? BatteryLowAlert;

    private readonly Dictionary<IBodyTracker, DateTime> _lastLowBatteryAlert = new();
    private static readonly TimeSpan LowBatteryAlertCooldown = TimeSpan.FromMinutes(10);
    private const float LowBatteryThreshold = 0.15f;

    private bool _initialized;

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        Configuration = Configuration.LoadConfig();
        Configuration.WiiPollingRate = 64;
        if (Configuration.SwitchingSessions)
        {
            Configuration.LastCalibration = DateTime.UtcNow;
        }

        TrackerManager = new GenericTrackerManager(Configuration);
        TrackerManager.PollingRate = Configuration.PollingRate;

        WiimoteManager = new ForwardedWiimoteManager();
        ThreeDsManager = new Forwarded3DSDataManager();
        Configuration.SwitchingSessions = false;
    }

    public int TotalTrackerCount =>
        GenericTrackerManager.TrackersBluetooth.Count +
        GenericTrackerManager.Trackers3ds.Count +
        GenericTrackerManager.TrackersWiimote.Count;

    public void Shutdown()
    {
        try { Configuration?.SaveConfig(); } catch { }
        // NOTE: GenericTrackerManager has no Dispose — legacy design.
        // Daemon loops (Task.Run + while(!disposed)) terminate at process exit.
    }

    public void CheckBatteryLevels()
    {
        foreach (var t in EnumerateAllTrackers())
        {
            try
            {
                var prop = t.GetType().GetProperty("BatteryLevel");
                if (prop?.GetValue(t) is not float level) continue;
                if (level <= 0f || level > LowBatteryThreshold) continue;
                if (_lastLowBatteryAlert.TryGetValue(t, out var prev)
                    && (DateTime.UtcNow - prev) < LowBatteryAlertCooldown) continue;
                _lastLowBatteryAlert[t] = DateTime.UtcNow;
                BatteryLowAlert?.Invoke(this, new BatteryLowEventArgs(t.ToString() ?? "Tracker", level));
            }
            catch { }
        }
    }

    private static IEnumerable<IBodyTracker> EnumerateAllTrackers()
    {
        foreach (var t in GenericTrackerManager.TrackersBluetooth) yield return t;
        foreach (var t in GenericTrackerManager.TrackersWiimote) yield return t;
        foreach (var t in GenericTrackerManager.Trackers3ds) yield return t;
    }
}

public record BatteryLowEventArgs(string TrackerName, float Level)
{
    public int Percent => (int)Math.Round(Level * 100);
}
