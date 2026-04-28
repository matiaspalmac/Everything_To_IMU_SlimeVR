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
    /// <summary>Fires when a tracker first appears in the live list.</summary>
    public event EventHandler<TrackerNotificationEventArgs>? TrackerConnected;
    /// <summary>Fires when a previously-seen tracker drops out.</summary>
    public event EventHandler<TrackerNotificationEventArgs>? TrackerDisconnected;
    private readonly HashSet<string> _knownTrackerIds = new();

    public void NoteLiveTrackers(IEnumerable<IBodyTracker> live)
    {
        // Diff against last snapshot — any newly-present id fires Connected; any missing id
        // fires Disconnected. Uses MacSpoof as the stable identity so a reconnect does not
        // produce a phantom disconnect+reconnect pair on the same physical controller.
        var liveIds = new HashSet<string>();
        foreach (var t in live)
        {
            if (string.IsNullOrEmpty(t.MacSpoof)) continue;
            liveIds.Add(t.MacSpoof);
            if (_knownTrackerIds.Add(t.MacSpoof))
            {
                TrackerConnected?.Invoke(this, new TrackerNotificationEventArgs(
                    t.ToString() ?? t.MacSpoof, t.MacSpoof));
            }
        }
        foreach (var id in _knownTrackerIds.Except(liveIds).ToList())
        {
            _knownTrackerIds.Remove(id);
            TrackerDisconnected?.Invoke(this, new TrackerNotificationEventArgs(id, id));
        }
    }

    private readonly Dictionary<IBodyTracker, DateTime> _lastLowBatteryAlert = new();
    private static readonly TimeSpan LowBatteryAlertCooldown = TimeSpan.FromMinutes(10);
    private float LowBatteryThreshold => Configuration?.BatteryLowThreshold ?? 0.15f;

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
        GenericTrackerManager.SnapshotBluetooth().Count +
        GenericTrackerManager.Snapshot3ds().Count +
        GenericTrackerManager.SnapshotWiimote().Count;

    public void Shutdown()
    {
        try { Configuration?.FlushPendingSave(); } catch { }
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
        foreach (var t in GenericTrackerManager.SnapshotBluetooth()) yield return t;
        foreach (var t in GenericTrackerManager.SnapshotWiimote()) yield return t;
        foreach (var t in GenericTrackerManager.Snapshot3ds()) yield return t;
        foreach (var t in GenericTrackerManager.SnapshotJoyCon2()) yield return t;
    }
}

public record BatteryLowEventArgs(string TrackerName, float Level)
{
    public int Percent => (int)Math.Round(Level * 100);
}

public record TrackerNotificationEventArgs(string TrackerName, string TrackerId);
