using System.Numerics;

namespace Everything_To_IMU_SlimeVR.Tracking;

/// <summary>
/// Online gyro bias estimator. Detects stillness via accel magnitude variance + gyro magnitude
/// threshold. When stillness persists for RestMinSeconds, samples gyro average as bias offset.
/// Subtract from every sample before feeding VQF.
///
/// Portable across all controllers (DualSense, DS4, Joy-Con, Switch Pro, Wiimote) — no HID
/// calibration report parsing required. Works while controller sits on desk at boot or any
/// time user leaves it still.
///
/// Thresholds tuned for handheld IMUs. Adaptive: bias recomputed if subsequent stillness
/// window shows drift vs current estimate (e.g., thermal drift after warmup).
///
/// NOTE on factory calibration: DualSense (feature report 0x05) and DS4 (0x02) store Sony
/// factory bias+sensitivity. We intentionally do NOT apply them here because JoyShockLibrary
/// may already subtract factory bias internally — double-subtraction would zero true bias
/// and produce drifting rotation. Revisit if JSL internals can be verified. Online estimator
/// below converges in ~2s of stillness regardless.
/// </summary>
public sealed class GyroBiasCalibrator
{
    // Stillness gate thresholds. Defaults from pedestrian nav literature (arxiv 2008.09208)
    // adapted for handheld controllers sitting on flat surface or lightly held.
    public float AccelMagVarianceThreshold { get; set; } = 0.08f;   // (m/s²)² — low for desk rest
    public float GyroMagThresholdRadPerSec { get; set; } = 0.05f;   // ~2.9 °/s max
    public float RestMinSeconds { get; set; } = 1.5f;

    // Rolling window stats
    private const int WindowSize = 64;
    private readonly Vector3[] _accelBuf = new Vector3[WindowSize];
    private readonly Vector3[] _gyroBuf = new Vector3[WindowSize];
    private int _bufIndex;
    private int _bufCount;

    private DateTime _restStartedAt = DateTime.MinValue;
    private Vector3 _bias = Vector3.Zero;
    private bool _hasBias;

    public Vector3 Bias => _bias;
    public bool HasBias => _hasBias;

    /// <summary>
    /// Feed one sample. Returns true when bias has been updated this call.
    /// </summary>
    public bool AddSample(Vector3 accelMs2, Vector3 gyroRadPerSec)
    {
        _accelBuf[_bufIndex] = accelMs2;
        _gyroBuf[_bufIndex] = gyroRadPerSec;
        _bufIndex = (_bufIndex + 1) % WindowSize;
        if (_bufCount < WindowSize) _bufCount++;

        if (_bufCount < WindowSize) return false;

        // Stillness detection: accel magnitude variance + gyro magnitude upper bound.
        float accelMagMean = 0;
        for (int i = 0; i < WindowSize; i++) accelMagMean += _accelBuf[i].Length();
        accelMagMean /= WindowSize;

        float accelMagVar = 0;
        for (int i = 0; i < WindowSize; i++)
        {
            float d = _accelBuf[i].Length() - accelMagMean;
            accelMagVar += d * d;
        }
        accelMagVar /= WindowSize;

        float maxGyroMag = 0;
        for (int i = 0; i < WindowSize; i++)
        {
            float g = _gyroBuf[i].Length();
            if (g > maxGyroMag) maxGyroMag = g;
        }

        bool isStill = accelMagVar < AccelMagVarianceThreshold
                    && maxGyroMag < GyroMagThresholdRadPerSec;

        if (!isStill)
        {
            _restStartedAt = DateTime.MinValue;
            return false;
        }

        if (_restStartedAt == DateTime.MinValue)
        {
            _restStartedAt = DateTime.UtcNow;
            return false;
        }

        if ((DateTime.UtcNow - _restStartedAt).TotalSeconds < RestMinSeconds) return false;

        // Capture bias = gyro mean over the stillness window.
        Vector3 mean = Vector3.Zero;
        for (int i = 0; i < WindowSize; i++) mean += _gyroBuf[i];
        mean /= WindowSize;
        _bias = mean;
        _hasBias = true;
        // Reset rest timer so we don't recompute every sample — wait for next rest window.
        _restStartedAt = DateTime.MinValue;
        return true;
    }

    /// <summary>Corrected gyro = raw - bias. Call before feeding VQF.</summary>
    public Vector3 Correct(Vector3 rawGyroRadPerSec) => _hasBias ? rawGyroRadPerSec - _bias : rawGyroRadPerSec;

    public void Reset()
    {
        _bufCount = 0;
        _bufIndex = 0;
        _restStartedAt = DateTime.MinValue;
        _bias = Vector3.Zero;
        _hasBias = false;
    }
}
