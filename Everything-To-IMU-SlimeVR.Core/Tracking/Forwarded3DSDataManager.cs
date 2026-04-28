using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace Everything_To_IMU_SlimeVR.Tracking {
    public class Forwarded3DSDataManager : IDisposable {
        const int listenPort = 9305;
        private static ConcurrentDictionary<string, ThreeDSState> _deviceMap = new ConcurrentDictionary<string, ThreeDSState>();
        private static ConcurrentDictionary<string, ThreeDsStateTracker> _stateTracker = new ConcurrentDictionary<string, ThreeDsStateTracker>();
        public static EventHandler<string> NewPacketReceived;
        public static ConcurrentDictionary<string, ThreeDSState> DeviceMap { get => _deviceMap; set => _deviceMap = value; }

        // Per-instance shutdown signal for the receive loop. UdpClient is captured here so
        // Dispose can close it — the previous design did `while(true)` with no cancellation,
        // leaking the socket on app exit / re-init and preventing a fresh instance from
        // binding port 9305 on the next run.
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private UdpClient _udpClient;

        public Forwarded3DSDataManager() {
            Task.Run(() => Initialize(_cts.Token));
        }

        public void Dispose() {
            try { _cts.Cancel(); } catch { }
            try { _udpClient?.Close(); } catch { }
            try { _udpClient?.Dispose(); } catch { }
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
        async Task Initialize(CancellationToken ct) {
            // Bind loopback by default; opt-in via Configuration for LAN companions (3DS on Wi-Fi).
            bool allowLan = Configuration.Instance?.AcceptCompanionsFromLan ?? false;
            var bindEp = new IPEndPoint(allowLan ? IPAddress.Any : IPAddress.Loopback, listenPort);
            try { _udpClient = new UdpClient(bindEp); }
            catch (Exception ex) { Console.WriteLine($"3DS bind failed: {ex.Message}"); return; }
            _deviceMap = new ConcurrentDictionary<string, ThreeDSState>();
            int expectedSize = Marshal.SizeOf(typeof(ImuPacket));
            Console.WriteLine("Listening for IMU data...");
            while (!ct.IsCancellationRequested) {
                IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try {
                    data = _udpClient.Receive(ref remoteEP);
                } catch (ObjectDisposedException) {
                    // Dispose called from outside — exit cleanly.
                    return;
                } catch (Exception ex) {
                    if (ct.IsCancellationRequested) return;
                    Console.WriteLine($"3DS recv error: {ex.Message}");
                    await Task.Delay(500, ct).ContinueWith(_ => { });
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
            // LAN sources only accepted when user opts in; default is loopback-only bind, so
            // this is belt-and-suspenders but protects if the socket was bound broader.
            if (!(Configuration.Instance?.AcceptCompanionsFromLan ?? false)) return false;
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