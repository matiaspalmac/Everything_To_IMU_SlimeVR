using HidSharp;
using System.Text;

namespace Everything_To_IMU_SlimeVR.Tracking;

/// <summary>
/// Reads firmware + hardware identification from DualSense / DualShock 4 feature reports.
/// This replaces the earlier "battery cycle count" idea — cycle count is not exposed by any
/// public Sony HID report. Firmware build string + version bytes *are* exposed (feature 0x20
/// on DualSense, feature 0xA3 on DualShock 4) and are genuinely useful for diagnostics.
///
/// Read is one-shot per device path (cached forever). Best-effort: returns null if the device
/// is exclusive-locked or the report shape is unexpected.
/// </summary>
public static class SonyFirmwareInfo
{
    private const int SonyVid = 0x054C;
    private const int DualSensePid = 0x0CE6;
    private const int DualSenseEdgePid = 0x0DF2;
    private const int DualShock4V1Pid = 0x05C4;
    private const int DualShock4V2Pid = 0x09CC;
    private const int DualShockDonglePid = 0x0BA0;

    public record Info(string BuildInfo, ushort FwVersion, ushort HwVersion);

    private static readonly Dictionary<string, Info?> _cache = new();
    private static readonly object _lock = new();

    public static Info? GetInfo(int controllerIndex)
    {
        lock (_lock)
        {
            try
            {
                var device = FindDevice(controllerIndex);
                if (device == null) return null;
                if (_cache.TryGetValue(device.DevicePath, out var cached)) return cached;

                Info? info = null;
                try { info = ReadFromDevice(device); } catch { }
                _cache[device.DevicePath] = info;
                return info;
            }
            catch { return null; }
        }
    }

    private static HidDevice? FindDevice(int controllerIndex)
    {
        var all = DeviceList.Local.GetHidDevices()
            .Where(d => d.VendorID == SonyVid)
            .Where(d => d.ProductID == DualSensePid || d.ProductID == DualSenseEdgePid
                     || d.ProductID == DualShock4V1Pid || d.ProductID == DualShock4V2Pid
                     || d.ProductID == DualShockDonglePid)
            .GroupBy(d => d.DevicePath.Split('#').Take(3).Aggregate((a, b) => a + "#" + b))
            .Select(g => g.First())
            .ToList();
        if (controllerIndex < 0 || controllerIndex >= all.Count) return null;
        return all[controllerIndex];
    }

    private static Info? ReadFromDevice(HidDevice device)
    {
        bool isDs5 = device.ProductID == DualSensePid || device.ProductID == DualSenseEdgePid;
        byte reportId = isDs5 ? (byte)0x20 : (byte)0xA3;
        int bufLen = isDs5 ? 64 : 49;

        var buf = new byte[bufLen];
        buf[0] = reportId;
        using var stream = device.Open();
        stream.ReadTimeout = 500;
        stream.GetFeature(buf);
        if (buf[0] != reportId) return null;

        // Bytes 1..20 contain ASCII build date / build tag on both DS5 and DS4. Extract the
        // printable prefix as a human-readable build string.
        var sb = new StringBuilder();
        for (int i = 1; i < Math.Min(20, buf.Length); i++)
        {
            byte b = buf[i];
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            else if (sb.Length > 0) break;
        }
        string build = sb.ToString().Trim();

        // Version bytes live near the end of the header block on DS5; DS4 has a slightly
        // different layout but the same idea. We publish them as raw 16-bit values so the UI
        // can display them verbatim without pretending to know semantics we haven't verified.
        ushort fw = isDs5
            ? (ushort)(buf[24] | (buf[25] << 8))
            : (ushort)(buf[41] | (buf[42] << 8));
        ushort hw = isDs5
            ? (ushort)(buf[26] | (buf[27] << 8))
            : (ushort)(buf[43] | (buf[44] << 8));

        return new Info(build, fw, hw);
    }
}
