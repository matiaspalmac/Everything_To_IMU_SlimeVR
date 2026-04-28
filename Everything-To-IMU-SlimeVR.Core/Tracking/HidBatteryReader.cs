using HidSharp;

namespace Everything_To_IMU_SlimeVR.Tracking;

/// <summary>
/// Reads battery level directly from gamepad HID reports because JoyShockLibrary does not
/// expose a battery API. Supported: PS5 DualSense, PS4 DualShock 4, Switch Pro, Joycon L/R.
/// Battery is mapped to the tracker index by HID enumeration order (same order JSL sees them).
/// Cached 30s per device to keep HID round-trip cost out of the sample hot path.
/// </summary>
public static class HidBatteryReader
{
    private const int SonyVid = 0x054C;
    private const int NintendoVid = 0x057E;

    // PS5 DualSense
    private const int DualSensePid = 0x0CE6;
    // PS4 DualShock 4 (v1 + v2)
    private const int DualShock4Pid = 0x05C4;
    private const int DualShock4Pid2 = 0x09CC;
    // Nintendo
    private const int JoyconLPid = 0x2006;
    private const int JoyconRPid = 0x2007;
    private const int ProControllerPid = 0x2009;

    private record CachedReading(float Fraction, DateTime At);
    private static readonly Dictionary<string, CachedReading> _cache = new();
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(30);
    private static readonly object _lock = new();

    /// <summary>
    /// Best-effort battery read for the Nth controller of a given vendor family.
    /// Returns null if no device matches or the read fails.
    /// </summary>
    public static float? GetBatteryFraction(int controllerIndex)
    {
        // Fast path for Nintendo controllers: when JoyCon1HidImuReader is attached it
        // already parses battery byte 2 of every IMU packet, so we can skip opening a
        // second HID handle. With 4× JC1 paired the per-30s open cycle was contributing
        // real BT stack pressure on top of JSL's existing handle.
        if (JoyCon1HidImuReader.IsActiveFor(controllerIndex))
        {
            var cached = JoyCon1HidImuReader.CachedBatteryFor(controllerIndex);
            if (cached.HasValue) return cached;
        }

        lock (_lock)
        {
            try
            {
                // Enumerate HID devices in Sony + Nintendo VID range, any supported PID.
                var all = DeviceList.Local.GetHidDevices()
                    .Where(d => d.VendorID == SonyVid || d.VendorID == NintendoVid)
                    .Where(d =>
                        d.ProductID == DualSensePid ||
                        d.ProductID == DualShock4Pid || d.ProductID == DualShock4Pid2 ||
                        d.ProductID == JoyconLPid || d.ProductID == JoyconRPid ||
                        d.ProductID == ProControllerPid)
                    // Deduplicate: some devices expose multiple HID interfaces; pick main input.
                    .GroupBy(d => d.DevicePath.Split('#').Take(3).Aggregate((a, b) => a + "#" + b))
                    .Select(g => g.First())
                    .ToList();

                if (controllerIndex < 0 || controllerIndex >= all.Count) return null;

                var device = all[controllerIndex];
                var key = device.DevicePath;

                if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < _cacheTtl)
                    return cached.Fraction;

                float? fraction = null;
                try
                {
                    using var stream = device.Open();
                    stream.ReadTimeout = 500;
                    var report = new byte[device.GetMaxInputReportLength()];
                    int len = stream.Read(report);
                    fraction = Parse(device, report, len);
                }
                catch { /* device may be locked exclusive, skip */ }

                if (fraction is float f)
                {
                    _cache[key] = new CachedReading(f, DateTime.UtcNow);
                }
                return fraction;
            }
            catch
            {
                return null;
            }
        }
    }

    private static float? Parse(HidDevice device, byte[] report, int len)
    {
        if (len < 3) return null;

        // DualSense: battery at byte 53 (USB input report 0x01) or 54 (BT input report 0x31).
        // Format: low nibble = charge 0..10 (sometimes 0..8), bit 0x10 = "charging" flag which
        // stays set whenever the controller is plugged in via USB — do NOT treat it as 100%.
        if (device.VendorID == SonyVid && device.ProductID == DualSensePid)
        {
            int batteryByte = len >= 78 ? 54 : 53;
            if (len <= batteryByte) return null;
            var b = report[batteryByte];
            int level = b & 0x0F;
            // Some firmware caps at 8 instead of 10; normalize to 0..1 clamped.
            return Math.Clamp(level / 10f, 0f, 1f);
        }

        // DualShock 4: byte 12 (USB) or byte 30 (BT). Low nibble = level 0..10.
        // Charging bit (0x10) is set while plugged in, ignore it so we still display real level.
        if (device.VendorID == SonyVid && (device.ProductID == DualShock4Pid || device.ProductID == DualShock4Pid2))
        {
            int batteryByte = len >= 78 ? 30 : 12;
            if (len <= batteryByte) return null;
            var b = report[batteryByte];
            int level = b & 0x0F;
            return Math.Clamp(level / 10f, 0f, 1f);
        }

        // Nintendo Switch Pro + Joycons: input report 0x30, byte 2 high nibble = battery (0..8 step of 2).
        if (device.VendorID == NintendoVid)
        {
            if (report[0] != 0x30 && report[0] != 0x21) return null;
            if (len < 3) return null;
            var b = report[2];
            int raw = (b >> 4) & 0x0F;
            // raw: 8=full, 6=medium, 4=low, 2=critical, 0=empty, +1 = charging flag
            bool charging = (raw & 0x01) != 0;
            int level = raw & 0xE;
            if (charging) return 1f;
            return level switch
            {
                >= 8 => 1f,
                6 => 0.75f,
                4 => 0.5f,
                2 => 0.25f,
                _ => 0.05f,
            };
        }

        return null;
    }
}
