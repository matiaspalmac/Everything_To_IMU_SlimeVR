using System;
using System.Runtime.InteropServices;

public class VQFWrapper : IDisposable {
    private IntPtr handle;
    private readonly double[] _gyrBuf = new double[3];
    private readonly double[] _accBuf = new double[3];
    private readonly double[] _quatBuf = new double[4];

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
    public void SetTauAcc(double tauAcc) => VQF_SetTauAcc(handle, tauAcc);
    public void SetTauMag(double tauMag) => VQF_SetTauMag(handle, tauMag);

    public void Update(double[] gyro, double[] accel, double[]? mag = null) {
        if (gyro.Length != 3 || accel.Length != 3) {
            throw new ArgumentException("Gyro and accel must have 3 elements.");
        }
        VQF_Update(handle, gyro, accel, mag);
    }


    public double[] GetQuat9D() {
        double[] quat = new double[4];
        VQF_GetQuat9D(handle, quat);
        return quat;
    }

    public double[] GetQuat6D() {
        double[] quat = new double[4];
        VQF_GetQuat6D(handle, quat);
        return quat;
    }

    // Zero-alloc hot-path: reuses internal buffers. Input vector is mapped to VQF convention (X, -Z, Y).
    public void UpdateFast(System.Numerics.Vector3 gyro, System.Numerics.Vector3 accel) {
        _gyrBuf[0] = gyro.X; _gyrBuf[1] = -gyro.Z; _gyrBuf[2] = gyro.Y;
        _accBuf[0] = accel.X; _accBuf[1] = -accel.Z; _accBuf[2] = accel.Y;
        VQF_Update(handle, _gyrBuf, _accBuf, null);
    }

    public System.Numerics.Quaternion GetQuat6DFast() {
        VQF_GetQuat6D(handle, _quatBuf);
        return new System.Numerics.Quaternion((float)_quatBuf[1], (float)_quatBuf[2], (float)_quatBuf[3], (float)_quatBuf[0]);
    }

    public void Dispose() {
        if (handle != IntPtr.Zero) {
            VQF_Destroy(handle);
            handle = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~VQFWrapper() => Dispose();
}
