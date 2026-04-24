using HidSharp;

namespace Everything_To_IMU_SlimeVR.Tracking;

/// <summary>
/// Sends HID output reports to DualSense / DualSense Edge controllers to drive adaptive trigger
/// effects. Supports both USB (report 0x02, 48 bytes) and Bluetooth (report 0x31, 78 bytes
/// with trailing CRC32) transports. Transport is auto-detected from the device's max output
/// report length so callers don't need to know which wire the controller is on.
///
/// Output reports are shared (not exclusive) on Windows HID, so JSL holding the input handle
/// does not block our writes. If the handle open fails (older Windows, exclusive driver), the
/// call silently no-ops. Flag bytes follow pydualsense defaults (0xFF / 0xF7) which keep
/// rumble, lightbar, mic LED and triggers all addressable in one report.
/// </summary>
public static class DualSenseOutput
{
    private const int SonyVid = 0x054C;
    private const int DualSensePid = 0x0CE6;
    private const int DualSenseEdgePid = 0x0DF2;

    // BT output CRC32 is computed over [0xA2 prefix || report bytes[..^4]]. 0xA2 is the HID
    // OUTPUT report tag that the DS5 firmware implicitly prepends when validating the CRC.
    private const byte BtCrcPrefix = 0xA2;

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
                return ApplyBothInternal(device,
                    left: side == TriggerSide.Left ? preset : TriggerPreset.Off,
                    right: side == TriggerSide.Right ? preset : TriggerPreset.Off);
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
                return ApplyBothInternal(device, left, right);
            }
            catch { return false; }
        }
    }

    private static bool ApplyBothInternal(HidDevice device, TriggerPreset left, TriggerPreset right)
    {
        // Transport detection: USB output is 48 bytes, BT is 78.
        int maxOut = 0;
        try { maxOut = device.GetMaxOutputReportLength(); } catch { }
        bool isBt = maxOut >= 78;
        var report = isBt ? BuildBtReport(left, right) : BuildUsbReport(left, right);
        return WriteReport(device, report);
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
    private static byte[] BuildUsbReport(TriggerPreset left, TriggerPreset right)
    {
        var report = new byte[48];
        report[0] = 0x02;                    // Report ID (USB)
        report[1] = 0xFF;                    // flag0: enable rumble + all output sections
        report[2] = 0xF7;                    // flag1: enable all except mute LED

        var (rMode, rParams) = PresetBytes(right);
        report[11] = rMode;
        Array.Copy(rParams, 0, report, 12, rParams.Length);

        var (lMode, lParams) = PresetBytes(left);
        report[22] = lMode;
        Array.Copy(lParams, 0, report, 23, lParams.Length);

        return report;
    }

    // BT output report 0x31 = 78 bytes total. Layout: [0]=0x31, [1]=tag, [2..] same payload
    // as USB shifted +1, last 4 bytes = CRC32(0xA2 || report[..74]).
    private static byte[] BuildBtReport(TriggerPreset left, TriggerPreset right)
    {
        var report = new byte[78];
        report[0] = 0x31;                    // Report ID (BT)
        report[1] = 0x02;                    // Transaction tag (any valid value works for feature-complete DS5 firmware)
        report[2] = 0xFF;                    // flag0 (was USB [1])
        report[3] = 0xF7;                    // flag1 (was USB [2])

        // Trigger blocks shift +1 vs USB (because of the tag byte at [1]).
        var (rMode, rParams) = PresetBytes(right);
        report[12] = rMode;                  // was USB [11]
        Array.Copy(rParams, 0, report, 13, rParams.Length);

        var (lMode, lParams) = PresetBytes(left);
        report[23] = lMode;                  // was USB [22]
        Array.Copy(lParams, 0, report, 24, lParams.Length);

        // Trailing CRC32 over [0xA2 || report[0..74]] — firmware rejects without it.
        uint crc = Crc32(BtCrcPrefix, report.AsSpan(0, 74));
        report[74] = (byte)(crc & 0xFF);
        report[75] = (byte)((crc >> 8) & 0xFF);
        report[76] = (byte)((crc >> 16) & 0xFF);
        report[77] = (byte)((crc >> 24) & 0xFF);
        return report;
    }

    /// <summary>
    /// Standard CRC32 (poly 0xEDB88320, reflected, init 0xFFFFFFFF, final XOR 0xFFFFFFFF).
    /// Seed byte is prepended to the payload before hashing (DS5 BT firmware quirk).
    /// </summary>
    private static uint Crc32(byte seed, ReadOnlySpan<byte> payload)
    {
        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, seed);
        foreach (var b in payload) crc = UpdateCrc(crc, b);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte b)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++)
            crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        return crc;
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
                p[0] = 0;    // start position (0..9)
                p[1] = 8;    // strength (0..8)
                return (0x01, p);
            case TriggerPreset.Weapon:
                p[0] = 2;    // start position
                p[1] = 5;    // end position
                p[2] = 8;    // strength
                return (0x02, p);
            case TriggerPreset.Vibration:
                p[0] = 10;   // frequency (Hz)
                p[1] = 8;    // strength (0..8)
                p[2] = 2;    // start position
                return (0x06, p);
            case TriggerPreset.Bow:
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
