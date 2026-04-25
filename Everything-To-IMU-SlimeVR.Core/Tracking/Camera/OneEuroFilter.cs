using System;

namespace Everything_To_IMU_SlimeVR.Tracking.Camera {
    /// <summary>
    /// 1€ Filter (Casiez, Roussel, Vogel — CHI 2012). Adaptive low-pass for noisy real-time
    /// streams: cutoff scales with absolute derivative, so slow motion gets aggressive
    /// smoothing and fast motion stays responsive.
    ///
    /// Defaults match the published paper for landmark-style signals:
    ///   minCutoff = 1.0 Hz   (cutoff at rest — higher = less lag, more jitter)
    ///   beta      = 0.007    (cutoff slope vs speed — higher = faster response)
    ///   dCutoff   = 1.0 Hz   (derivative low-pass cutoff — rarely needs tuning)
    ///
    /// Apply per scalar axis. For Vector3 use 3 instances (XYZ). Do NOT apply to quaternions
    /// component-wise — breaks unit norm. Filter landmark POSITIONS, derive bone quats from
    /// filtered positions, optionally slerp-EMA the resulting quats if extra smoothing wanted.
    /// </summary>
    public sealed class OneEuroFilter {
        private readonly double _minCutoff;
        private readonly double _beta;
        private readonly double _dCutoff;

        private bool _initialised;
        private double _lastValue;
        private double _lastDx;
        private double _lastTimestamp;

        public OneEuroFilter(double minCutoff = 1.0, double beta = 0.007, double dCutoff = 1.0) {
            _minCutoff = minCutoff;
            _beta = beta;
            _dCutoff = dCutoff;
        }

        public void Reset() {
            _initialised = false;
            _lastValue = 0;
            _lastDx = 0;
            _lastTimestamp = 0;
        }

        /// <summary>Filter a single sample. <paramref name="timestamp"/> in seconds.</summary>
        public double Filter(double value, double timestamp) {
            if (!_initialised) {
                _initialised = true;
                _lastValue = value;
                _lastDx = 0;
                _lastTimestamp = timestamp;
                return value;
            }

            double dt = timestamp - _lastTimestamp;
            if (dt <= 0) dt = 1.0 / 30.0; // sane fallback if timestamps are equal/inverted
            double rate = 1.0 / dt;

            double dx = (value - _lastValue) * rate;
            double edx = LowPass(dx, _lastDx, Alpha(rate, _dCutoff));
            double cutoff = _minCutoff + _beta * Math.Abs(edx);
            double y = LowPass(value, _lastValue, Alpha(rate, cutoff));

            _lastDx = edx;
            _lastValue = y;
            _lastTimestamp = timestamp;
            return y;
        }

        private static double LowPass(double x, double prev, double alpha) =>
            alpha * x + (1 - alpha) * prev;

        private static double Alpha(double rate, double cutoff) {
            double tau = 1.0 / (2.0 * Math.PI * cutoff);
            double te = 1.0 / rate;
            return 1.0 / (1.0 + tau / te);
        }
    }
}
