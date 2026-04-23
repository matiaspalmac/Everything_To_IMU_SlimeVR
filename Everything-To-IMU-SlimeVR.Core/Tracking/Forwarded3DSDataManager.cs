using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace Everything_To_IMU_SlimeVR.Tracking {
    public class Forwarded3DSDataManager {
        const int listenPort = 9305;
        private static ConcurrentDictionary<string, ThreeDSState> _deviceMap = new ConcurrentDictionary<string, ThreeDSState>();
        private static ConcurrentDictionary<string, ThreeDsStateTracker> _stateTracker = new ConcurrentDictionary<string, ThreeDsStateTracker>();
        public static EventHandler<string> NewPacketReceived;
        public static ConcurrentDictionary<string, ThreeDSState> DeviceMap { get => _deviceMap; set => _deviceMap = value; }

        public Forwarded3DSDataManager() {
            Task.Run(() => Initialize());
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ImuPacket {
            public short ax, ay, az;
            public short gx, gy, gz;
        }
        public static Quaternion GetOrientationFromGravity(Vector3 gravity) {
            gravity = Vector3.Normalize(gravity);
            float pitch = MathF.Asin(-gravity.X);
            float roll = MathF.Atan2(gravity.Y, gravity.Z);
            Quaternion qPitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);
            Quaternion qRoll = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, roll);
            return Quaternion.Normalize(Quaternion.Concatenate(qRoll, qPitch));
        }
        async Task Initialize() {
            UdpClient udpClient = new UdpClient(listenPort); // Match port from 3DS
            _deviceMap = new ConcurrentDictionary<string, ThreeDSState>();
            int expectedSize = Marshal.SizeOf(typeof(ImuPacket));
            Console.WriteLine("Listening for IMU data...");
            while (true) {
                IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try {
                    data = udpClient.Receive(ref remoteEP);
                } catch (Exception ex) {
                    Console.WriteLine($"3DS recv error: {ex.Message}");
                    await Task.Delay(500);
                    continue;
                }

                if (!IsAllowedSource(remoteEP.Address)) {
                    continue;
                }
                if (data.Length != expectedSize) {
                    continue;
                }

                string ip = remoteEP.Address.ToString();
                GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
                ImuPacket value;
                try {
                    value = (ImuPacket)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(ImuPacket));
                } finally {
                    handle.Free();
                }
                if (!_stateTracker.ContainsKey(ip)) {
                    _stateTracker[ip] = new ThreeDsStateTracker();
                }
                _deviceMap[ip] = _stateTracker[ip].ProcessPacket(value);
                NewPacketReceived?.Invoke(this, ip);
            }
        }

        static bool IsAllowedSource(IPAddress addr) {
            if (IPAddress.IsLoopback(addr)) return true;
            if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            var bytes = addr.GetAddressBytes();
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            return false;
        }
    }
}