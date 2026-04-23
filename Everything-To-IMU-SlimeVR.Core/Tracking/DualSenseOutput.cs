using HidSharp;

namespace Everything_To_IMU_SlimeVR.Tracking;

/// <summary>
/// Sends HID output reports to DualSense / DualSense Edge controllers to drive adaptive trigger
/// effects. USB path only (report 0x02, 47-byte payload) — BT path (report 0x31) needs CRC32 and
/// is not implemented yet.
///
/// Output reports are shared (not exclusive) on Windows HID, so JSL holding the input handle
/// does not block our writes. If the handle open fails (older Windows, exclusive driver), the
/// call silently no-ops. Flag bytes follow pydualsense defaults (0xFF / 0xF7) which are known
/// to keep rumble, lightbar, mic LED and triggers all addressable in one report.
/// </summary>
public static class DualSenseOutput
{
    private const int SonyVid = 0x054C;
    private const int DualSensePid = 0x0CE6;
    private const int DualSenseEdgePid = 0x0DF2;

    public enum TriggerSide { Left, Right }

    public enum TriggerPreset
    {
        Off = 0,
        Resistance = 1,
        Weapon = 2,
        Vibration = 3,
        Bow = 4,
    }

    private static readonly object _lock = new();

    public static bool ApplyTrigger(int controllerIndex, TriggerSide side, TriggerPreset preset)
    {
        lock (_lock)
        {
            try
            {
                var device = FindDualSense(controllerIndex);
                if (device == null) return false;
                var payload = BuildOutputReport(side, preset, applyBoth: false);
                return WriteReport(device, payload);
            }
            catch { return false; }
        }
    }

    public static bool ApplyBothTriggers(int controllerIndex, TriggerPreset left, TriggerPreset right)
    {
        lock (_lock)
        {
            try
            {
                var device = FindDualSense(controllerIndex);
                if (device == null) return false;
                var payload = BuildOutputReport(left, right);
                return WriteReport(device, payload);
            }
            catch { return false; }
        }
    }

    private static HidDevice? FindDualSense(int controllerIndex)
    {
        var all = DeviceList.Local.GetHidDevices()
            .Where(d => d.VendorID == SonyVid &&
                        (d.ProductID == DualSensePid || d.ProductID == DualSenseEdgePid))
            .GroupBy(d => d.DevicePath.Split('#').Take(3).Aggregate((a, b) => a + "#" + b))
            .Select(g => g.First())
            .ToList();
        if (controllerIndex < 0 || controllerIndex >= all.Count) return null;
        return all[controllerIndex];
    }

    private static bool WriteReport(HidDevice device, byte[] report)
    {
        try
        {
            using var stream = device.Open();
            stream.WriteTimeout = 500;
            stream.Write(report);
            return true;
        }
        catch { return false; }
    }

    // USB output report 0x02 = 48 bytes total (1 id + 47 payload).
    private static byte[] BuildOutputReport(TriggerSide side, TriggerPreset preset, bool applyBoth)
    {
        var (mode, p) = PresetBytes(preset);
        return BuildOutputReport(
            left: side == TriggerSide.Left ? preset : TriggerPreset.Off,
            right: side == TriggerSide.Right ? preset : TriggerPreset.Off);
    }

    private static byte[] BuildOutputReport(TriggerPreset left, TriggerPreset right)
    {
        var report = new byte[48];
        report[0] = 0x02;                    // Report ID (USB)
        report[1] = 0xFF;                    // flag0: enable rumble + all output sections
        report[2] = 0xF7;                    // flag1: enable all except mute LED

        // Right trigger effect at offset 11 (10 mode + 11..20 params).
        var (rMode, rParams) = PresetBytes(right);
        report[11] = rMode;
        Array.Copy(rParams, 0, report, 12, rParams.Length);

        // Left trigger effect at offset 22 (22 mode + 23..32 params).
        var (lMode, lParams) = PresetBytes(left);
        report[22] = lMode;
        Array.Copy(lParams, 0, report, 23, lParams.Length);

        return report;
    }

    /// <summary>
    /// Returns (mode, params[10]) for the named preset. Parameters follow pydualsense conventions.
    /// </summary>
    private static (byte mode, byte[] parameters) PresetBytes(TriggerPreset preset)
    {
        var p = new byte[10];
        switch (preset)
        {
            case TriggerPreset.Off:
                return (0x00, p);
            case TriggerPreset.Resistance:
                // Constant resistance from start_pos through full pull.
                p[0] = 0;    // start position (0..9)
                p[1] = 8;    // strength (0..8)
                return (0x01, p);
            case TriggerPreset.Weapon:
                // Stiff section start..end, release at end.
                p[0] = 2;    // start position
                p[1] = 5;    // end position
                p[2] = 8;    // strength
                return (0x02, p);
            case TriggerPreset.Vibration:
                // Oscillating vibration past start_pos.
                p[0] = 10;   // frequency (Hz)
                p[1] = 8;    // strength (0..8)
                p[2] = 2;    // start position
                return (0x06, p);
            case TriggerPreset.Bow:
                // Bow draw: resistance builds from start to end, then snaps.
                p[0] = 1;    // start
                p[1] = 4;    // end
                p[2] = 4;    // resistance
                p[3] = 4;    // snap force
                return (0x21, p);
            default:
                return (0x00, p);
        }
    }
}
