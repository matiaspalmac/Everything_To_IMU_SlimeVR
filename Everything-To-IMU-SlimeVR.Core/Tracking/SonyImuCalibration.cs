using HidSharp;
using System.Numerics;

namespace Everything_To_IMU_SlimeVR.Tracking;

/// <summary>
/// Reads factory IMU calibration from DualSense / DualShock 4 HID feature reports because
/// JoyShockLibrary does NOT apply Sony factory calibration — its SPI read path is gated to
/// the Switch family. Sony firmware exposes per-unit gyro bias + per-axis gyro sensitivity
/// + per-axis accel bias + per-axis accel sensitivity via feature report 0x05 (DualSense,
/// DualShock 4 BT) or 0x02 (DualShock 4 USB).
///
/// We parse the full cal block now (extended from the earlier bias-only implementation):
///   - Gyro bias (bytes 1-6): eliminates ±1–2 dps ZRL drift in the first seconds before VQF
///     would have learned the bias itself.
///   - Per-axis gyro sensitivity (bytes 7-22): ~2% magnitude correction. Linux computes
///     `calibrated_dps = raw * speed_2x / sens_denom_i`; we apply the relative correction
///     in JSL's dps domain as a unitless scale factor close to 1.0.
///   - Accel bias + scale (bytes 23-34): removes the per-axis zero-g offset JSL leaves in
///     place (VQF normalises gravity magnitude but can't recover the per-axis bias if the
///     chip shipped with a non-zero ZRL on an axis).
///
/// DS4 over Bluetooth returns the SAME 41-byte payload as DS5 but uses an interleaved
/// layout (all three "plus" values first, then all three "minus"), while DS5 / DS4-USB
/// use the sequential "plus/minus per axis" layout. We pick the parser by device PID.
///
/// Cached per DevicePath, thread-safe, never throws (returns Unknown on any failure).
/// Enumeration order tracks HidBatteryReader's same-order-as-JSL assumption.
/// </summary>
public static class SonyImuCalibration
{
    private const int SonyVid = 0x054C;
    private const int DualSensePid = 0x0CE6;
    private const int DualSenseEdgePid = 0x0DF2;
    private const int DualShock4V1Pid = 0x05C4;
    private const int DualShock4V2Pid = 0x09CC;
    private const int DualShockDonglePid = 0x0BA0;

    // JSL reports gyro in dps with scale raw * 2000/32767. Multiply the raw int16 bias by
    // the same scale so the subtraction happens in the dps domain JSL already gives us.
    private const float GyroScaleDpsPerUnit = 2000f / 32767f;
    // JSL reports accel in g units with scale raw / 8192.
    private const float AccelScaleGPerUnit = 1f / 8192f;

    /// <summary>
    /// Factory cal values in JSL's output domain so application is one multiply + subtract:
    /// corrected_gyro_dps = (jsl_gyro_dps - GyroBiasDps) * GyroScale
    /// corrected_accel_g  = (jsl_accel_g  - AccelBiasG ) * AccelScale
    /// </summary>
    public record Calibration(Vector3 GyroBiasDps, Vector3 GyroScale,
                              Vector3 AccelBiasG, Vector3 AccelScale, bool Valid);

    private static readonly Calibration Unknown = new Calibration(
        Vector3.Zero, Vector3.One, Vector3.Zero, Vector3.One, false);
    private static readonly Dictionary<string, Calibration> _cache = new();
    private static readonly object _lock = new();

    public static Calibration GetCalibration(int controllerIndex)
    {
        lock (_lock)
        {
            try
            {
                var all = DeviceList.Local.GetHidDevices()
                    .Where(d => d.VendorID == SonyVid)
                    .Where(d => d.ProductID == DualSensePid || d.ProductID == DualSenseEdgePid
                             || d.ProductID == DualShock4V1Pid || d.ProductID == DualShock4V2Pid
                             || d.ProductID == DualShockDonglePid)
                    .GroupBy(d => d.DevicePath.Split('#').Take(3).Aggregate((a, b) => a + "#" + b))
                    .Select(g => g.First())
                    .ToList();

                if (controllerIndex < 0 || controllerIndex >= all.Count) return Unknown;
                var device = all[controllerIndex];
                var key = device.DevicePath;
                if (_cache.TryGetValue(key, out var cached)) return cached;

                var cal = ReadFromDevice(device);
                _cache[key] = cal;
                return cal;
            }
            catch
            {
                return Unknown;
            }
        }
    }

    private static Calibration ReadFromDevice(HidDevice device)
    {
        bool isDs5 = device.ProductID == DualSensePid || device.ProductID == DualSenseEdgePid;
        bool isDs4 = device.ProductID == DualShock4V1Pid || device.ProductID == DualShock4V2Pid
                  || device.ProductID == DualShockDonglePid;

        // Try report 0x05 (DS5 always; DS4 over Bluetooth).
        try
        {
            var buf = new byte[41];
            buf[0] = 0x05;
            using var stream = device.Open();
            stream.ReadTimeout = 500;
            stream.GetFeature(buf);
            if (buf[0] == 0x05)
            {
                // DS4 over 0x05 is the Bluetooth path → interleaved layout. DS5 always
                // uses the sequential layout even over Bluetooth.
                return ParseCal(buf, interleaved: isDs4);
            }
        }
        catch { /* fall through */ }

        // Fallback: report 0x02 (DS4 USB only, 37 bytes, sequential layout).
        if (isDs4)
        {
            try
            {
                var buf = new byte[37];
                buf[0] = 0x02;
                using var stream = device.Open();
                stream.ReadTimeout = 500;
                stream.GetFeature(buf);
                if (buf[0] == 0x02) return ParseCal(buf, interleaved: false);
            }
            catch { /* skip — device may be exclusive-locked by JSL */ }
        }

        return Unknown;
    }

    /// <summary>
    /// Parse the cal report into JSL-domain offsets and scale factors.
    ///
    /// Sequential layout (DS5 + DS4 USB):
    ///   [ 1..2] gyro_pitch_bias        [ 3..4] gyro_yaw_bias        [ 5..6] gyro_roll_bias
    ///   [ 7..8] gyro_pitch_plus        [ 9..10] gyro_pitch_minus
    ///   [11..12] gyro_yaw_plus         [13..14] gyro_yaw_minus
    ///   [15..16] gyro_roll_plus        [17..18] gyro_roll_minus
    ///
    /// Interleaved layout (DS4 BT / Dongle):
    ///   [ 1..2] gyro_pitch_bias        [ 3..4] gyro_yaw_bias        [ 5..6] gyro_roll_bias
    ///   [ 7..8] gyro_pitch_plus        [ 9..10] gyro_yaw_plus       [11..12] gyro_roll_plus
    ///   [13..14] gyro_pitch_minus      [15..16] gyro_yaw_minus      [17..18] gyro_roll_minus
    ///
    /// Common tail:
    ///   [19..20] gyro_speed_plus       [21..22] gyro_speed_minus
    ///   [23..24] acc_x_plus            [25..26] acc_x_minus
    ///   [27..28] acc_y_plus            [29..30] acc_y_minus
    ///   [31..32] acc_z_plus            [33..34] acc_z_minus
    /// </summary>
    private static Calibration ParseCal(byte[] buf, bool interleaved)
    {
        if (buf.Length < 35) return Unknown;

        short biasX = BitConverter.ToInt16(buf, 1);
        short biasY = BitConverter.ToInt16(buf, 3);
        short biasZ = BitConverter.ToInt16(buf, 5);

        short pitchPlus, pitchMinus, yawPlus, yawMinus, rollPlus, rollMinus;
        if (interleaved)
        {
            pitchPlus  = BitConverter.ToInt16(buf, 7);
            yawPlus    = BitConverter.ToInt16(buf, 9);
            rollPlus   = BitConverter.ToInt16(buf, 11);
            pitchMinus = BitConverter.ToInt16(buf, 13);
            yawMinus   = BitConverter.ToInt16(buf, 15);
            rollMinus  = BitConverter.ToInt16(buf, 17);
        }
        else
        {
            pitchPlus  = BitConverter.ToInt16(buf, 7);
            pitchMinus = BitConverter.ToInt16(buf, 9);
            yawPlus    = BitConverter.ToInt16(buf, 11);
            yawMinus   = BitConverter.ToInt16(buf, 13);
            rollPlus   = BitConverter.ToInt16(buf, 15);
            rollMinus  = BitConverter.ToInt16(buf, 17);
        }

        short speedPlus  = BitConverter.ToInt16(buf, 19);
        short speedMinus = BitConverter.ToInt16(buf, 21);
        short accXPlus   = BitConverter.ToInt16(buf, 23);
        short accXMinus  = BitConverter.ToInt16(buf, 25);
        short accYPlus   = BitConverter.ToInt16(buf, 27);
        short accYMinus  = BitConverter.ToInt16(buf, 29);
        short accZPlus   = BitConverter.ToInt16(buf, 31);
        short accZMinus  = BitConverter.ToInt16(buf, 33);

        // Bias guard. Factory ZRL sits within a few hundred LSB; anything beyond ±4096 means
        // we grabbed uninitialised bytes (e.g. third-party clone that ignores the request).
        if (Math.Abs((int)biasX) > 4096 || Math.Abs((int)biasY) > 4096 || Math.Abs((int)biasZ) > 4096)
            return Unknown;

        var gyroBiasDps = new Vector3(
            biasX * GyroScaleDpsPerUnit,
            biasY * GyroScaleDpsPerUnit,
            biasZ * GyroScaleDpsPerUnit);

        // Per-axis gyro sensitivity. Linux formula:
        //   calibrated_dps = raw * speed_2x / sens_denom_i
        // JSL already multiplies by 2000/32767. So the scale factor relative to JSL is:
        //   scale_i = (speed_2x / sens_denom_i) / (2000 / 32767)
        // For an ideal cal (speed_2x ≈ 2000, sens_denom ≈ 32767) scale collapses to 1.0; real
        // cal gives per-axis values typically within ±2% of 1.0.
        int speed2x = speedPlus + speedMinus;
        var gyroScale = Vector3.One;
        if (speed2x > 0)
        {
            float jslRef = 2000f / 32767f;
            int denomX = Math.Abs(pitchPlus - biasX) + Math.Abs(pitchMinus - biasX);
            int denomY = Math.Abs(yawPlus   - biasY) + Math.Abs(yawMinus   - biasY);
            int denomZ = Math.Abs(rollPlus  - biasZ) + Math.Abs(rollMinus  - biasZ);
            if (denomX > 0 && denomY > 0 && denomZ > 0)
            {
                gyroScale = new Vector3(
                    (speed2x / (float)denomX) / jslRef,
                    (speed2x / (float)denomY) / jslRef,
                    (speed2x / (float)denomZ) / jslRef);
                // Clamp — anything outside ±10% of unity is more likely noise in a third-party
                // clone than a real sensitivity difference.
                gyroScale = new Vector3(
                    Math.Clamp(gyroScale.X, 0.9f, 1.1f),
                    Math.Clamp(gyroScale.Y, 0.9f, 1.1f),
                    Math.Clamp(gyroScale.Z, 0.9f, 1.1f));
            }
        }

        // Accel bias = midpoint of plus/minus; 2g range = plus - minus. Linux formula:
        //   calibrated_g = (raw - bias) * (2 * ACC_RES_PER_G) / range_2g
        // Converting to JSL-domain (raw = jsl_g * 8192, JSL scale = 1/8192):
        //   corrected_g = (jsl_g - bias_g) * (2 * 8192 / range_2g)
        // where bias_g = midpoint / 8192 and scale = 2 * 8192 / range_2g (unitless, ≈1.0).
        var accelBiasG = Vector3.Zero;
        var accelScale = Vector3.One;
        int rangeX = accXPlus - accXMinus;
        int rangeY = accYPlus - accYMinus;
        int rangeZ = accZPlus - accZMinus;
        if (rangeX > 0 && rangeY > 0 && rangeZ > 0)
        {
            int midX = accXPlus - rangeX / 2;
            int midY = accYPlus - rangeY / 2;
            int midZ = accZPlus - rangeZ / 2;
            accelBiasG = new Vector3(
                midX * AccelScaleGPerUnit,
                midY * AccelScaleGPerUnit,
                midZ * AccelScaleGPerUnit);
            accelScale = new Vector3(
                2f * 8192f / rangeX,
                2f * 8192f / rangeY,
                2f * 8192f / rangeZ);
            // Same ±10% sanity clamp as gyro; prevents a clone controller with garbage in
            // those bytes from driving the fusion filter off a cliff.
            accelScale = new Vector3(
                Math.Clamp(accelScale.X, 0.9f, 1.1f),
                Math.Clamp(accelScale.Y, 0.9f, 1.1f),
                Math.Clamp(accelScale.Z, 0.9f, 1.1f));
        }

        return new Calibration(gyroBiasDps, gyroScale, accelBiasG, accelScale, true);
    }
}
