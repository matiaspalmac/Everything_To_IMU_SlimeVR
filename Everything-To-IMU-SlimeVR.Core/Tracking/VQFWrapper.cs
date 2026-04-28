using System;
using System.Runtime.InteropServices;

public class VQFWrapper : IDisposable {
    private IntPtr handle;
    private readonly double[] _gyrBuf = new double[3];
    private readonly double[] _accBuf = new double[3];
    private readonly double[] _magBuf = new double[3];
    private readonly double[] _quatBuf = new double[4];

    // Guards every native call against the finalizer and against external dispose-vs-update
    // races. Previous design relied on the parent class to lock around _vqf, but the finalizer
    // runs on the GC thread and never sees that parent lock — under abnormal teardown (e.g.
    // unhandled exception before the parent disposed us) the GC could call VQF_Destroy on a
    // handle that a notify thread was still using. _destroyed gates every native dispatch.
    private readonly object _handleLock = new();
    private bool _destroyed;

    private const string DllName = "vqf"; // or "libvqf" on macOS/Linux if not renamed

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr VQF_Create(double gyrTs, double accTs, double magTs);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void VQF_Destroy(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void VQF_Update(IntPtr handle, double[] gyr, double[] acc, double[] mag);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void VQF_GetQuat9D(IntPtr handle, double[] quatOut);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void VQF_GetQuat6D(IntPtr handle, double[] quatOut);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void VQF_SetTauAcc(IntPtr handle, double tauAcc);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void VQF_SetTauMag(IntPtr handle, double tauMag);
    public VQFWrapper(double gyrTs, double accTs = -1.0, double magTs = -1.0) {
        handle = VQF_Create(gyrTs, accTs, magTs);
    }
    // SetTauAcc / SetTauMag are defined further down with the locked dispatch path.

    public void Update(double[] gyro, double[] accel, double[]? mag = null) {
        if (gyro.Length != 3 || accel.Length != 3) {
            throw new ArgumentException("Gyro and accel must have 3 elements.");
        }
        lock (_handleLock) {
            if (_destroyed) return;
            VQF_Update(handle, gyro, accel, mag);
        }
    }


    public double[] GetQuat9D() {
        double[] quat = new double[4];
        lock (_handleLock) {
            if (_destroyed) return quat;
            VQF_GetQuat9D(handle, quat);
        }
        return quat;
    }

    public double[] GetQuat6D() {
        double[] quat = new double[4];
        lock (_handleLock) {
            if (_destroyed) return quat;
            VQF_GetQuat6D(handle, quat);
        }
        return quat;
    }

    // Zero-alloc hot-path: reuses internal buffers. Input vector is mapped to VQF convention (X, -Z, Y).
    public void UpdateFast(System.Numerics.Vector3 gyro, System.Numerics.Vector3 accel) {
        lock (_handleLock) {
            if (_destroyed) return;
            _gyrBuf[0] = gyro.X; _gyrBuf[1] = -gyro.Z; _gyrBuf[2] = gyro.Y;
            _accBuf[0] = accel.X; _accBuf[1] = -accel.Z; _accBuf[2] = accel.Y;
            VQF_Update(handle, _gyrBuf, _accBuf, null);
        }
    }

    public System.Numerics.Quaternion GetQuat6DFast() {
        lock (_handleLock) {
            if (_destroyed) return System.Numerics.Quaternion.Identity;
            VQF_GetQuat6D(handle, _quatBuf);
            return new System.Numerics.Quaternion((float)_quatBuf[1], (float)_quatBuf[2], (float)_quatBuf[3], (float)_quatBuf[0]);
        }
    }

    // Zero-alloc identity hot-path. Use when the caller has already transformed inputs into
    // the body frame VQF expects (Z up, gravity pointing +Z when stationary face-up). Avoids
    // the (X, -Z, Y) remap that UpdateFast applies for the JSL pipeline.
    public void UpdateIdentity(System.Numerics.Vector3 gyro, System.Numerics.Vector3 accel) {
        lock (_handleLock) {
            if (_destroyed) return;
            _gyrBuf[0] = gyro.X; _gyrBuf[1] = gyro.Y; _gyrBuf[2] = gyro.Z;
            _accBuf[0] = accel.X; _accBuf[1] = accel.Y; _accBuf[2] = accel.Z;
            VQF_Update(handle, _gyrBuf, _accBuf, null);
        }
    }

    public void UpdateIdentity9D(System.Numerics.Vector3 gyro, System.Numerics.Vector3 accel, System.Numerics.Vector3 mag) {
        lock (_handleLock) {
            if (_destroyed) return;
            _gyrBuf[0] = gyro.X; _gyrBuf[1] = gyro.Y; _gyrBuf[2] = gyro.Z;
            _accBuf[0] = accel.X; _accBuf[1] = accel.Y; _accBuf[2] = accel.Z;
            _magBuf[0] = mag.X; _magBuf[1] = mag.Y; _magBuf[2] = mag.Z;
            VQF_Update(handle, _gyrBuf, _accBuf, _magBuf);
        }
    }

    // 9DoF zero-alloc hot-path. Mag is mapped through the same (X, -Z, Y) convention as gyro/accel
    // so it lands in the same body frame VQF was tuned for. Mag input expected in µT (or any
    // unit consistent across calls — VQF normalises the vector internally).
    public void UpdateFast9D(System.Numerics.Vector3 gyro, System.Numerics.Vector3 accel, System.Numerics.Vector3 mag) {
        lock (_handleLock) {
            if (_destroyed) return;
            _gyrBuf[0] = gyro.X; _gyrBuf[1] = -gyro.Z; _gyrBuf[2] = gyro.Y;
            _accBuf[0] = accel.X; _accBuf[1] = -accel.Z; _accBuf[2] = accel.Y;
            _magBuf[0] = mag.X; _magBuf[1] = -mag.Z; _magBuf[2] = mag.Y;
            VQF_Update(handle, _gyrBuf, _accBuf, _magBuf);
        }
    }

    public System.Numerics.Quaternion GetQuat9DFast() {
        lock (_handleLock) {
            if (_destroyed) return System.Numerics.Quaternion.Identity;
            VQF_GetQuat9D(handle, _quatBuf);
            return new System.Numerics.Quaternion((float)_quatBuf[1], (float)_quatBuf[2], (float)_quatBuf[3], (float)_quatBuf[0]);
        }
    }

    // SetTau* taken under the lock too — they call into the same native object that
    // Update*/GetQuat* dispatch on, so concurrent set+update could race the C++ side.
    public void SetTauAcc(double tauAcc) {
        lock (_handleLock) {
            if (_destroyed) return;
            VQF_SetTauAcc(handle, tauAcc);
        }
    }
    public void SetTauMag(double tauMag) {
        lock (_handleLock) {
            if (_destroyed) return;
            VQF_SetTauMag(handle, tauMag);
        }
    }

    public void Dispose() {
        lock (_handleLock) {
            if (_destroyed) return;
            _destroyed = true;
            if (handle != IntPtr.Zero) {
                try { VQF_Destroy(handle); } catch { /* native side unavailable — let the OS reclaim */ }
                handle = IntPtr.Zero;
            }
        }
        GC.SuppressFinalize(this);
    }

    ~VQFWrapper() => Dispose();
}
