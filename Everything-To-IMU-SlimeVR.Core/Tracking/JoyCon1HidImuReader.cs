using HidSharp;
using System.Collections.Concurrent;
using System.Numerics;

namespace Everything_To_IMU_SlimeVR.Tracking;

/// <summary>
/// Reads Joy-Con (Switch 1) and Switch Pro Controller IMU samples directly from the HID
/// stream so we can recover the 2 frames JoyShockLibrary throws away.
///
/// Switch firmware streams input report 0x30 at ~67 Hz, but each report carries THREE IMU
/// samples spaced 5 ms apart (the chip is sampling at 200 Hz internally). JSL's callback
/// only surfaces the most recent sample — the older two are silently dropped. By tapping
/// the HID stream in parallel and pumping all three into VQF we triple the effective IMU
/// rate without changing transport or polling.
///
/// Architecture:
///   - Singleton; one HID reader thread per Switch device, keyed by JSL index.
///   - Subscribers get raw accel (g) and gyro (dps) per sample, plus a frame index 0/1/2.
///     Frame 0 is the OLDEST sample in the packet (chronological order so VQF integrates
///     them with the correct 5 ms dt monotonic forward).
///   - JSL still owns buttons / sticks / battery / haptics. Only the IMU samples are
///     duplicated; SensorOrientation toggles a flag so it ignores JSL's redundant gyro
///     callback while this reader is active.
///   - Open is shared (FILE_SHARE_READ|WRITE on Windows) so JSL coexists.
///
/// Report 0x30 layout (dekuNukem reverse-engineering doc):
///   [0]    0x30 report ID
///   [1]    timer (increments per packet)
///   [2]    battery + connection nibbles
///   [3-5]  button state (right / shared / left)
///   [6-11] left stick + right stick (12-bit packed)
///   [12]   vibrator return state
///   [13-24] IMU sample 1 (older — 15 ms ago at packet receipt)
///   [25-36] IMU sample 2
///   [37-48] IMU sample 3 (newest — same as JSL's callback value)
///   Each IMU sample: accel X/Y/Z + gyro X/Y/Z, 6 × int16 LE = 12 bytes.
///
/// Scaling — Switch family uses STMicro LSM6DS3-TR-C: accel ±8G at 4096 LSB/g,
/// gyro ±2000 dps at ~16.4 LSB/dps. We apply factory SPI cal (0x6020 / user override
/// 0x8026) per device via JoyCon1SpiCalibration before emitting samples — JSL's pipeline
/// does the same internally on its own callback, but our HID path bypasses JSL entirely
/// to recover the 2 dropped samples. Without cal each LSM6DS3 ships with a slightly
/// different ZRL bias (the user-visible "ankle yaw ticks down constantly" symptom) plus
/// ~1% sensitivity spread, both of which VQF can't fully recover from raw stream alone.
/// If the SPI read fails (clone, busy bus) we fall back to nominal scaling and rely on
/// the runtime GyroBiasCalibrator in SensorOrientation to converge bias.
/// </summary>
public static class JoyCon1HidImuReader
{
    private const int NintendoVid = 0x057E;
    private const int JoyConLeftPid = 0x2006;
    private const int JoyConRightPid = 0x2007;
    private const int JoyConChargingGripPid = 0x200E;
    private const int SwitchProPid = 0x2009;

    // Output stays in JSL's unit conventions (g and dps) so subscribers can mix our samples
    // with JSL's without unit conversions. Per-axis scale lives in JoyCon1SpiCalibration.
    public record Sample(int JslIndex, int FrameIndex, Vector3 AccelG, Vector3 GyroDps);

    public static event EventHandler<Sample>? SampleReady;

    private record Worker(HidDevice Device, HidStream Stream, Thread Thread, CancellationTokenSource Cts);
    private static readonly ConcurrentDictionary<int, Worker> _workers = new();
    private static readonly object _startLock = new();

    /// <summary>
    /// Returns true once a reader thread is up for the given JSL index. SensorOrientation
    /// uses this flag to short-circuit JSL's redundant IMU callback.
    /// </summary>
    public static bool IsActiveFor(int jslIndex) => _workers.ContainsKey(jslIndex);

    /// <summary>
    /// Try to attach a HID reader thread to the Nth Switch-family device. Returns false if
    /// already attached, no matching device, or HID open failed (e.g. exclusive lock).
    /// </summary>
    public static bool TryStart(int jslIndex)
    {
        lock (_startLock)
        {
            if (_workers.ContainsKey(jslIndex)) return false;
            try
            {
                var all = DeviceList.Local.GetHidDevices()
                    .Where(d => d.VendorID == NintendoVid)
                    .Where(d => d.ProductID == JoyConLeftPid || d.ProductID == JoyConRightPid
                             || d.ProductID == JoyConChargingGripPid || d.ProductID == SwitchProPid)
                    .GroupBy(d => d.DevicePath.Split('#').Take(3).Aggregate((a, b) => a + "#" + b))
                    .Select(g => g.First())
                    .ToList();

                if (jslIndex < 0 || jslIndex >= all.Count) return false;
                var device = all[jslIndex];
                HidStream stream;
                try
                {
                    stream = device.Open();
                    stream.ReadTimeout = 250;
                }
                catch
                {
                    // Exclusive lock or driver refusal — JSL keeps full ownership in that case.
                    return false;
                }

                var cts = new CancellationTokenSource();
                var thread = new Thread(() => RunReader(jslIndex, device, stream, cts.Token))
                {
                    IsBackground = true,
                    Name = $"JoyCon1Hid-{jslIndex}"
                };
                _workers[jslIndex] = new Worker(device, stream, thread, cts);
                thread.Start();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Stop(int jslIndex)
    {
        if (!_workers.TryRemove(jslIndex, out var w)) return;
        try { w.Cts.Cancel(); } catch { }
        try { w.Stream.Dispose(); } catch { }
    }

    public static void StopAll()
    {
        foreach (var key in _workers.Keys.ToList()) Stop(key);
    }

    private static void RunReader(int jslIndex, HidDevice device, HidStream stream, CancellationToken ct)
    {
        // Pre-allocate buffer at the input report length the device advertises. Switch
        // Joy-Con full mode (report 0x30) is 49 bytes; charging grip pair mode and Pro
        // can be longer, so size at the device's max report length and slice on read.
        int maxLen;
        try { maxLen = device.GetMaxInputReportLength(); }
        catch { maxLen = 64; }
        var report = new byte[Math.Max(maxLen, 64)];

        // Read SPI factory + user cal once on attach. Done here rather than in TryStart so a
        // 300 ms× retry deadline never blocks the JSL callback thread that calls TryStart.
        // Falls back to nominal scaling on any failure — runtime GyroBiasCalibrator in
        // SensorOrientation converges bias regardless.
        int outLen;
        try { outLen = device.GetMaxOutputReportLength(); }
        catch { outLen = 49; }
        var cal = JoyCon1SpiCalibration.Read(stream, device.DevicePath, outLen);
        var accelOrigin = cal.AccelOrigin;
        var gyroOrigin = cal.GyroOrigin;
        var accelCoeff = cal.AccelCoeff;
        var gyroCoeff = cal.GyroCoeff;

        while (!ct.IsCancellationRequested)
        {
            int len;
            try
            {
                len = stream.Read(report);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch
            {
                // Stream died (controller unplugged or driver hiccup) — exit; the next
                // start attempt by SensorOrientation reopens.
                break;
            }
            if (len < 49 || report[0] != 0x30) continue;

            // Three IMU samples at offsets 13/25/37, each 12 bytes.
            for (int i = 0; i < 3; i++)
            {
                int off = 13 + i * 12;
                short ax = BitConverter.ToInt16(report, off + 0);
                short ay = BitConverter.ToInt16(report, off + 2);
                short az = BitConverter.ToInt16(report, off + 4);
                short gx = BitConverter.ToInt16(report, off + 6);
                short gy = BitConverter.ToInt16(report, off + 8);
                short gz = BitConverter.ToInt16(report, off + 10);

                // (raw - origin) * coeff per axis. coeff was pre-computed from SPI cal as
                // 4.0 / (acc_sens - acc_origin) and 936.0 / (gyro_sens - gyro_origin) so the
                // hot path is one subtract + one multiply per axis. Falls back to chip-spec
                // nominal (1/4096 g, 2000/32767 dps) when cal read failed (Unknown.Valid=false).
                var accel = new Vector3((ax - accelOrigin.X) * accelCoeff.X,
                                        (ay - accelOrigin.Y) * accelCoeff.Y,
                                        (az - accelOrigin.Z) * accelCoeff.Z);
                var gyro  = new Vector3((gx - gyroOrigin.X)  * gyroCoeff.X,
                                        (gy - gyroOrigin.Y)  * gyroCoeff.Y,
                                        (gz - gyroOrigin.Z)  * gyroCoeff.Z);
                try { SampleReady?.Invoke(null, new Sample(jslIndex, i, accel, gyro)); } catch { }
            }
        }

        // Self-cleanup if we exited because the stream died (not because Stop was called).
        try { _workers.TryRemove(jslIndex, out _); } catch { }
        try { stream.Dispose(); } catch { }
    }
}
