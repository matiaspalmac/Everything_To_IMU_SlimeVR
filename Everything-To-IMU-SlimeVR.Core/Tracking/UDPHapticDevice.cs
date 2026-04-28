using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using static Everything_To_IMU_SlimeVR.TrackerConfig;
using SlimeImuProtocol.SlimeVR;

namespace Everything_To_IMU_SlimeVR.Tracking
{
    public class UDPHapticDevice : IBodyTracker, IDisposable
    {
        private HapticNodeBinding _hapticNodeBinding;
        private PacketBuilder _packetBuilder;
        private string _ipAddress;
        private string _alias;
        private UdpClient _udpServer;
        private IPEndPoint _clientEndPoint;
        private bool isAlreadyVibrating;
        private RotationReferenceType _extensionYawReferenceTypeValue;
        DateTime _hapticEndTime;
        private float _lastIntensity;
        private bool _disposed;

        public UDPHapticDevice(string ipAddress, string alias)
        {
            _packetBuilder = new PacketBuilder("");
            // Set up UDP server
            _ipAddress = ipAddress;
            _alias = alias;
            _clientEndPoint = new IPEndPoint(IPAddress.Parse(ipAddress), 6969);
            _udpServer = new UdpClient();
            _udpServer.Connect(ipAddress, 6969);
            Ready = true;
        }

        public int Id { get; set; }
        public string MacSpoof { get; set; }
        public Vector3 Euler { get; set; }
        public float LastHmdPositon { get; set; }
        public bool SimulateThighs { get; set; }
        public HapticNodeBinding HapticNodeBinding { get => _hapticNodeBinding; set => _hapticNodeBinding = value; }
        public TrackerConfig.RotationReferenceType YawReferenceTypeValue { get; set; }

        public string Debug => "Wifi haptic devices do not provide tracking data to this software.";

        public bool Ready { get; set; }
        public RotationReferenceType ExtensionYawReferenceTypeValue { get => _extensionYawReferenceTypeValue; set => _extensionYawReferenceTypeValue = value; }
        public Vector3 RotationCalibration { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public bool SupportsHaptics => true;

        public bool SupportsIMU => false;

        public string Alias { get => _alias; set => _alias = value; }
        public string IpAddress { get => _ipAddress; set => _ipAddress = value; }

        public void DisableHaptics()
        {
            if (_disposed) return;
            var data = _packetBuilder.BuildHapticPacket(0, 0);
            _ = SendSafelyAsync(data);
            _hapticEndTime = new DateTime();
        }

        public void EngageHaptics(int duration, float intensity)
        {
            if (_disposed) return;
            _hapticEndTime = DateTime.Now.AddMilliseconds(duration);
            if (!isAlreadyVibrating || intensity != _lastIntensity)
            {
                var data = _packetBuilder.BuildHapticPacket(intensity, duration);
                _ = SendSafelyAsync(data);
                _lastIntensity = intensity;
            }
            if (!isAlreadyVibrating)
            {
                isAlreadyVibrating = true;
                // Async wait — was Task.Run + Thread.Sleep(10), which parked a threadpool
                // thread for the full duration. With multiple haptic devices the threadpool
                // was permanently saturated.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var remaining = _hapticEndTime - DateTime.Now;
                        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
                        isAlreadyVibrating = false;
                        if (_disposed) return;
                        var stopData = _packetBuilder.BuildHapticPacket(0, 0);
                        await SendSafelyAsync(stopData);
                    }
                    catch { isAlreadyVibrating = false; }
                });
            }
        }

        // Wraps SendAsync so the dropped Task can't unobserved-exception leak into the
        // TaskScheduler.UnobservedTaskException handler with a network-down failure.
        // PacketBuilder returns ReadOnlyMemory<byte>; UdpClient supports it natively.
        private async Task SendSafelyAsync(ReadOnlyMemory<byte> data)
        {
            try { await _udpServer.SendAsync(data); } catch { }
        }

        public Vector3 GetCalibration()
        {
            return new Vector3();
        }

        public void HapticIntensityTest()
        {
            // Was a synchronous loop with Thread.Sleep(45) blocking for ~11 s on the caller.
            // Move to a fire-and-forget async chain so the UI doesn't freeze when the user
            // clicks Test.
            _ = Task.Run(async () =>
            {
                for (byte i = 0; i < 255 && !_disposed; i++)
                {
                    EngageHaptics(50, i / 255f);
                    await Task.Delay(45);
                }
            });
        }

        public void Identify()
        {
            EngageHaptics(300, 1);
        }

        public static event EventHandler<string>? UserMessageRequested;

        public void Rediscover()
        {
            UserMessageRequested?.Invoke(this, Debug);
        }

        public override string ToString()
        {
            return !string.IsNullOrEmpty(_alias) ? _alias : _ipAddress;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _udpServer?.Close(); } catch { }
            try { _udpServer?.Dispose(); } catch { }
        }
    }
}
