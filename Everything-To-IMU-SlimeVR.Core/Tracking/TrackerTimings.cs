namespace Everything_To_IMU_SlimeVR.Tracking;

/// <summary>
/// Centralized timing constants for tracker initialization, recalibration, and background
/// polling loops. Replaces magic-number `Task.Delay(5000)` / `Thread.Sleep(10000)` sprinkled
/// across tracker files. Values chosen to match observed fusion convergence / reconnect
/// behaviour; change here rather than hunting individual sites.
/// </summary>
internal static class TrackerTimings
{
    /// <summary>Wait before reading post-reset orientation on JSL-backed controllers.</summary>
    public const int RecalibrateSettleMsJsl = 5000;

    /// <summary>Wait before reading post-reset orientation on BLE Joy-Con 2 + 3DS/Wii.</summary>
    public const int RecalibrateSettleMsBle = 3000;

    /// <summary>Idle poll interval when a feature (e.g. thigh sim) is disabled.</summary>
    public const int IdleDisabledPollMs = 10000;

    /// <summary>Re-scan interval for companion managers that poll upstream state.</summary>
    public const int CompanionRescanMs = 1000;
}
