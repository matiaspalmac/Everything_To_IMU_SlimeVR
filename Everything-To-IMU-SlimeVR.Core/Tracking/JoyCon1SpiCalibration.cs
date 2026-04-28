using HidSharp;
using System.Collections.Concurrent;
using System.Numerics;

namespace Everything_To_IMU_SlimeVR.Tracking;

/// <summary>
/// Reads factory + user IMU calibration from Joy-Con 1 / Switch Pro SPI flash. JoyShockLibrary
/// applies SPI cal internally on its own pipeline, but our parallel HID reader bypasses JSL's
/// IMU output entirely (so it can recover the 2 samples per packet JSL drops). Without this
/// class we'd be feeding VQF un-calibrated raw values, which causes per-unit yaw drift visible
/// as "ankle ticking down constantly" — each LSM6DS3-TR-C ships with a slightly different
/// zero-rate offset (typically ±20–80 LSB) and per-axis sensitivity coefficient (~1% spread).
///
/// Calibration sources (per dekuNukem reverse-engineering doc + Linux hid-nintendo):
///   - Factory: SPI 0x6020, 24 bytes, always present.
///   - User override: SPI 0x8026, 26 bytes. First two bytes are magic 0xB2 0xA1; if that
///     matches, the body at +2..+25 supersedes the factory block. User cal is what Switch
///     stick/IMU recalibration writes.
///
/// Layout (24 bytes, all int16 little-endian):
///   [ 0.. 5] accel_origin    XYZ
///   [ 6..11] accel_sens_coef XYZ
///   [12..17] gyro_origin     XYZ
///   [18..23] gyro_sens_coef  XYZ
///
/// dekuNukem formulas:
///   accel_g   = (raw - acc_origin)  * 4.0   / (acc_sens   - acc_origin)
///   gyro_dps  = (raw - gyro_origin) * 936.0 / (gyro_sens  - gyro_origin)
/// At nominal (origin=0, acc_sens≈16384, gyro_sens≈13371) the multipliers collapse to the
/// chip's spec scales of 1/4096 g/LSB and ~0.07 dps/LSB. Per-unit cal corrects both bias and
/// the small (~1%) sensitivity spread between chips.
///
/// Subcommand 0x10 (SPI Flash Read) is sent on output report 0x01 with a neutral rumble
/// payload; reply lands on input report 0x21 with the cal bytes at offset 20. The HID stream
/// is shared with JSL — we send our subcmd and filter the 0x21 stream for the matching
/// address echo. JSL may also be sending subcmds (rumble, LEDs) so the device can be busy;
/// we retry up to 3× with a 300 ms deadline each before giving up and falling back to nominal
/// scaling.
///
/// Cached per device path so re-attaches don't re-read SPI.
/// </summary>
public static class JoyCon1SpiCalibration
{
    private const int FactoryCalAddr = 0x6020;
    private const int UserCalAddr = 0x8026; // 2-byte magic at +0, 24-byte cal at +2
    private const int FactoryCalLen = 24;
    private const int UserCalLen = 26;
    private const byte SpiReadSubcmd = 0x10;
    private const byte UserMagic0 = 0xB2;
    private const byte UserMagic1 = 0xA1;

    public record Calibration(
        Vector3 AccelOrigin, Vector3 AccelSens,
        Vector3 GyroOrigin, Vector3 GyroSens,
        bool Valid)
    {
        // Pre-computed per-axis coefficients so the hot path is one subtract + one multiply.
        // Caller multiplies (raw - origin) by these to land in g (accel) or dps (gyro).
        public Vector3 AccelCoeff { get; } = Valid
            ? new Vector3(SafeDiv(4f, AccelSens.X - AccelOrigin.X),
                          SafeDiv(4f, AccelSens.Y - AccelOrigin.Y),
                          SafeDiv(4f, AccelSens.Z - AccelOrigin.Z))
            : new Vector3(1f / 4096f);
        public Vector3 GyroCoeff { get; } = Valid
            ? new Vector3(SafeDiv(936f, GyroSens.X - GyroOrigin.X),
                          SafeDiv(936f, GyroSens.Y - GyroOrigin.Y),
                          SafeDiv(936f, GyroSens.Z - GyroOrigin.Z))
            : new Vector3(2000f / 32767f);

        private static float SafeDiv(float num, float denom) => Math.Abs(denom) < 100f ? num / 16384f : num / denom;
    }

    public static readonly Calibration Unknown = new(Vector3.Zero, new Vector3(16384f), Vector3.Zero, new Vector3(13371f), false);
    private static readonly ConcurrentDictionary<string, Calibration> _cache = new();
    private static int _packetCounter;

    /// <summary>
    /// Synchronously read SPI cal on the supplied open HidStream. Tries user cal first
    /// (if magic is present); falls back to factory cal. Returns Unknown on any failure
    /// — caller should fall back to nominal scaling and rely on the runtime
    /// GyroBiasCalibrator to converge bias.
    /// </summary>
    public static Calibration Read(HidStream stream, string deviceKey, int outputReportLength)
    {
        if (_cache.TryGetValue(deviceKey, out var hit)) return hit;

        Calibration result = Unknown;
        for (int attempt = 0; attempt < 3 && !result.Valid; attempt++)
        {
            try
            {
                var userBuf = ReadSpi(stream, UserCalAddr, UserCalLen, outputReportLength);
                if (userBuf != null && userBuf[0] == UserMagic0 && userBuf[1] == UserMagic1)
                {
                    result = ParseCalBlock(userBuf, 2);
                    if (result.Valid) break;
                }
                var factoryBuf = ReadSpi(stream, FactoryCalAddr, FactoryCalLen, outputReportLength);
                if (factoryBuf != null)
                {
                    result = ParseCalBlock(factoryBuf, 0);
                }
            }
            catch
            {
                // Stream hiccup — next attempt reopens the read window.
            }
        }
        _cache[deviceKey] = result;
        return result;
    }

    private static byte[]? ReadSpi(HidStream stream, int addr, int len, int outputReportLength)
    {
        // Output report 0x01: report id + packet counter + 8-byte rumble + subcommand.
        // Pad to the device's output report length so the HID write descriptor matches.
        int outLen = Math.Max(outputReportLength, 16);
        var output = new byte[outLen];
        output[0] = 0x01;
        output[1] = (byte)(System.Threading.Interlocked.Increment(ref _packetCounter) & 0x0F);
        // Neutral rumble (0x00 0x01 0x40 0x40 twice). Anything else triggers HD Rumble.
        output[2] = 0x00; output[3] = 0x01; output[4] = 0x40; output[5] = 0x40;
        output[6] = 0x00; output[7] = 0x01; output[8] = 0x40; output[9] = 0x40;
        output[10] = SpiReadSubcmd;
        output[11] = (byte)(addr & 0xFF);
        output[12] = (byte)((addr >> 8) & 0xFF);
        output[13] = (byte)((addr >> 16) & 0xFF);
        output[14] = (byte)((addr >> 24) & 0xFF);
        output[15] = (byte)len;

        try { stream.Write(output, 0, outLen); }
        catch { return null; }

        // Read until we see the matching subcmd reply or the 300 ms deadline expires. The
        // stream may be mid-burst of 0x30 IMU packets (if JSL already enabled full mode) or
        // 0x3F simple packets — either way subcmd replies always come on 0x21.
        var deadline = DateTime.UtcNow.AddMilliseconds(300);
        var buf = new byte[Math.Max(outputReportLength, 64)];
        int prevTimeout = stream.ReadTimeout;
        try { stream.ReadTimeout = 50; } catch { }
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                int read;
                try { read = stream.Read(buf); }
                catch (TimeoutException) { continue; }
                catch { return null; }
                if (read < 21 || buf[0] != 0x21) continue;
                // Layout from dekuNukem: [13]=ACK, [14]=subcmd echo, [15..18]=addr LE,
                // [19]=length echo, [20..]=payload. ACK MSB set means success.
                if ((buf[13] & 0x80) == 0) continue;
                if (buf[14] != SpiReadSubcmd) continue;
                int addrEcho = buf[15] | (buf[16] << 8) | (buf[17] << 16) | (buf[18] << 24);
                if (addrEcho != addr) continue;
                int lenEcho = buf[19];
                if (lenEcho != len || read < 20 + len) continue;
                var data = new byte[len];
                Array.Copy(buf, 20, data, 0, len);
                return data;
            }
        }
        finally
        {
            try { stream.ReadTimeout = prevTimeout; } catch { }
        }
        return null;
    }

    private static Calibration ParseCalBlock(byte[] buf, int offset)
    {
        if (buf.Length < offset + 24) return Unknown;
        var ao = new Vector3(BitConverter.ToInt16(buf, offset + 0),
                             BitConverter.ToInt16(buf, offset + 2),
                             BitConverter.ToInt16(buf, offset + 4));
        var ac = new Vector3(BitConverter.ToInt16(buf, offset + 6),
                             BitConverter.ToInt16(buf, offset + 8),
                             BitConverter.ToInt16(buf, offset + 10));
        var go = new Vector3(BitConverter.ToInt16(buf, offset + 12),
                             BitConverter.ToInt16(buf, offset + 14),
                             BitConverter.ToInt16(buf, offset + 16));
        var gc = new Vector3(BitConverter.ToInt16(buf, offset + 18),
                             BitConverter.ToInt16(buf, offset + 20),
                             BitConverter.ToInt16(buf, offset + 22));

        // Sanity: factory cal denominators sit around 16384 (accel) and 13371 (gyro). Anything
        // smaller than 100 means we either grabbed an erased flash sector (0xFFFF → -1 → tiny
        // denom) or a clone that ignored the SPI request. Reject and fall back to nominal.
        if (Math.Abs(ac.X - ao.X) < 100 || Math.Abs(ac.Y - ao.Y) < 100 || Math.Abs(ac.Z - ao.Z) < 100) return Unknown;
        if (Math.Abs(gc.X - go.X) < 100 || Math.Abs(gc.Y - go.Y) < 100 || Math.Abs(gc.Z - go.Z) < 100) return Unknown;
        // Origin guard: factory ZRL stays within ±2000 LSB. Beyond that we likely mis-parsed.
        if (Math.Abs(ao.X) > 8000 || Math.Abs(ao.Y) > 8000 || Math.Abs(ao.Z) > 8000) return Unknown;
        if (Math.Abs(go.X) > 8000 || Math.Abs(go.Y) > 8000 || Math.Abs(go.Z) > 8000) return Unknown;

        return new Calibration(ao, ac, go, gc, true);
    }
}
