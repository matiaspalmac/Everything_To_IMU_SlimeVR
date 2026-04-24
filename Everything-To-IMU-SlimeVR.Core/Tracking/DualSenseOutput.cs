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

    // Per-device state cache. Output reports are stateless at the wire level — every Apply*
    // call rebuilds a full report. Cache lets ApplyTrigger preserve mic/player LED state
    // (and vice versa) instead of clobbering them to zero. Keyed by HID DevicePath so USB
    // vs BT instances of the same physical controller don't collide.
    private sealed class DeviceState
    {
        public TriggerPreset Left = TriggerPreset.Off;
        public TriggerPreset Right = TriggerPreset.Off;
        public byte PlayerLedMask;   // bits: 0=R-out, 1=R-in, 2=center, 3=L-in, 4=L-out
        public bool MicLedOn;
    }
    private static readonly Dictionary<string, DeviceState> _stateCache = new();
    private static readonly object _lock = new();

    private static DeviceState GetState(HidDevice device)
    {
        if (!_stateCache.TryGetValue(device.DevicePath, out var s))
        {
            s = new DeviceState();
            _stateCache[device.DevicePath] = s;
        }
        return s;
    }

    /// <summary>
    /// Common player LED mask for tracker slot N (1..5). Patterns mirror PS5 system UI so
    /// players recognize the layout at a glance.
    /// </summary>
    public static byte PlayerLedMaskForSlot(int slot1Based)
    {
        switch (Math.Clamp(slot1Based, 1, 5))
        {
            case 1: return 0b00100;  // center only
            case 2: return 0b01010;  // two inners
            case 3: return 0b10101;  // two outers + center
            case 4: return 0b11011;  // four outer+inner
            case 5: return 0b11111;  // all five
            default: return 0b00100;
        }
    }

    public static bool ApplyTrigger(int controllerIndex, TriggerSide side, TriggerPreset preset)
    {
        lock (_lock)
        {
            try
            {
                var device = FindDualSense(controllerIndex);
                if (device == null) return false;
                var s = GetState(device);
                if (side == TriggerSide.Left) s.Left = preset; else s.Right = preset;
                return WriteState(device, s);
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
                var s = GetState(device);
                s.Left = left;
                s.Right = right;
                return WriteState(device, s);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Sets the 5-LED player bar under the touchpad and the mic-mute LED. Leaves trigger
    /// effects unchanged (cached per device). Use byte 0..0x1F for playerLedMask (see
    /// PlayerLedMaskForSlot helper for well-known patterns). micLed true = solid on.
    /// </summary>
    public static bool ApplyLedState(int controllerIndex, byte playerLedMask, bool micLedOn)
    {
        lock (_lock)
        {
            try
            {
                var device = FindDualSense(controllerIndex);
                if (device == null) return false;
                var s = GetState(device);
                s.PlayerLedMask = (byte)(playerLedMask & 0x1F);
                s.MicLedOn = micLedOn;
                return WriteState(device, s);
            }
            catch { return false; }
        }
    }

    private static bool WriteState(HidDevice device, DeviceState s)
    {
        int maxOut = 0;
        try { maxOut = device.GetMaxOutputReportLength(); } catch { }
        bool isBt = maxOut >= 78;
        var report = isBt ? BuildBtReport(s) : BuildUsbReport(s);
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

    // Common LED-section fill for both transports. Payload offsets match pydualsense /
    // dualsensectl conventions. Caller supplies a writable span starting at the payload
    // base (byte 1 for USB after the report ID, byte 2 for BT after report ID + tag).
    private static void WriteLedAndTriggerPayload(Span<byte> payload, DeviceState s)
    {
        payload[0] = 0xFF; // flag0: rumble + haptic select + all output sections enabled
        payload[1] = 0xF7; // flag1: LED + triggers + mic LED enabled (pydualsense default)

        // Mic LED (payload offset 8, 0=off, 1=solid, 2=pulse). Solid for our use case.
        payload[8] = (byte)(s.MicLedOn ? 1 : 0);

        // Right trigger effect at payload offset 10 (mode + 10 params).
        var (rMode, rParams) = PresetBytes(s.Right);
        payload[10] = rMode;
        rParams.CopyTo(payload.Slice(11, rParams.Length));

        // Left trigger effect at payload offset 21.
        var (lMode, lParams) = PresetBytes(s.Left);
        payload[21] = lMode;
        lParams.CopyTo(payload.Slice(22, lParams.Length));

        // Player LED strip: brightness at offset 39 (0=high, 1=med, 2=low), mask at 40.
        // Pulse flag at 38 = 0 (solid). Brightness 1 = medium so it doesn't dominate room.
        payload[38] = 0;
        payload[39] = 1;
        payload[40] = s.PlayerLedMask;

        // Lightbar RGB at offsets 41-43 stays zero — JSL.JslSetLightColour owns that path
        // (body-slot color from HapticNodeBinding). flag1 bit for lightbar still enabled
        // but zeroes match the last JSL-driven state closely enough in practice. If this
        // causes flicker, future work: pull the LED palette color here too.
    }

    // USB output report 0x02 = 48 bytes total (1 id + 47 payload).
    private static byte[] BuildUsbReport(DeviceState s)
    {
        var report = new byte[48];
        report[0] = 0x02;
        WriteLedAndTriggerPayload(report.AsSpan(1), s);
        return report;
    }

    // BT output report 0x31 = 78 bytes total. Layout: [0]=0x31, [1]=tag, [2..] same payload
    // as USB shifted +1, last 4 bytes = CRC32(0xA2 || report[..74]).
    private static byte[] BuildBtReport(DeviceState s)
    {
        var report = new byte[78];
        report[0] = 0x31;
        report[1] = 0x02; // Transaction tag (any valid value is accepted)
        WriteLedAndTriggerPayload(report.AsSpan(2), s);

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
