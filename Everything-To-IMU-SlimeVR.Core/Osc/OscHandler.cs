// Implementation borrowed from https://github.com/ButterscotchV/AXSlime

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Everything_To_IMU_SlimeVR.AudioHaptics;
using Everything_To_IMU_SlimeVR.Osc;
using Everything_To_IMU_SlimeVR.Tracking;
using LucHeart.CoreOSC;

namespace Everything_To_IMU_SlimeVR.Osc
{
    public class OscHandler : IDisposable
    {
        public static readonly string BundleAddress = "#bundle\0";
        public static readonly byte[] BundleAddressBytes = Encoding.ASCII.GetBytes(BundleAddress);
        public static readonly string AvatarParamPrefix = "/avatar/parameters/";
        public static List<string> parameterList = new List<string>();

        private UdpClient _oscClient;

        private CancellationTokenSource _cancelTokenSource = new();
        private Task _oscReceiveTask;

        private readonly AxHaptics _axHaptics;
        private readonly bHaptics _bHaptics;

        private readonly HapticsSource[] _hapticsSources;
        private readonly VRCHapticLogManager _vrcHapticLogManager;
        private ConcurrentDictionary<int, UdpClient> _portToUdpClientDictionary = new ConcurrentDictionary<int, UdpClient>();
        public OscHandler()
        {

            _axHaptics = new();
            _bHaptics = new();

            _hapticsSources = [_axHaptics, _bHaptics];

            _vrcHapticLogManager = new VRCHapticLogManager();
            var monitor = new DesktopAudioHapticMonitor(
                          new AudioHapticsAdapter(), fftSize: 1024, lowBandMaxHz: 200f, highBandMinHz: 5000f,
                                                   onThresholdDb: -24f, offThresholdDb: -38f, stereoDbBias: 3f);
            monitor.Start();
            RefreshOSCPort();
        }

        public void RefreshOSCPort()
        {
            if (_cancelTokenSource != null)
            {
                _cancelTokenSource.Cancel();
                try
                {
                    _oscReceiveTask?.Wait(2000);
                }
                catch { /* task cancellation is expected */ }
                _cancelTokenSource.Dispose();
            }
            _cancelTokenSource = new CancellationTokenSource();
            if (_oscClient != null)
            {
                _oscClient.Dispose();
                _oscClient = null;
            }
            foreach (var kvp in _portToUdpClientDictionary)
            {
                try { kvp.Value.Dispose(); } catch { }
            }
            _portToUdpClientDictionary.Clear();
            if (!int.TryParse(Configuration.Instance.PortInput, out var port) || port < 1 || port > 65535)
            {
                Debug.WriteLine($"Invalid OSC input port: {Configuration.Instance.PortInput}");
                return;
            }
            if (!Configuration.Instance.PortOutputs.Contains(port))
            {
                try
                {
                    // Loopback-only by default — VRChat/local OSC senders are local.
                    // Enable AcceptOscFromLan to receive from other hosts (e.g. phone OSC app).
                    bool allowLan = Configuration.Instance.AcceptOscFromLan;
                    var bindAddr = allowLan ? IPAddress.Any : IPAddress.Loopback;
                    _oscClient = new UdpClient(new IPEndPoint(bindAddr, port));
                }
                catch (SocketException ex)
                {
                    Debug.WriteLine($"OSC bind failed on port {port}: {ex.Message}");
                    return;
                }
                var token = _cancelTokenSource.Token;
                _oscReceiveTask = Task.Run(() => OscReceiveTask(token));
            }
        }

        private static bool IsBundle(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < BundleAddressBytes.Length) return false;
            for (int i = 0; i < BundleAddressBytes.Length; i++)
            {
                if (buffer[i] != BundleAddressBytes[i]) return false;
            }
            return true;
        }
        /// <summary>
        /// Takes in an OSC bundle package in byte form and parses it into a more usable OscBundle object
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>Bundle containing elements and a timetag</returns>
        public static OscBundle ParseBundle(Span<byte> msg)
        {
            ReadOnlySpan<byte> msgReadOnly = msg;
            var messages = new List<OscMessage>();

            var index = 0;

            var messageBytes = msg.Slice(index, msgReadOnly.Length - index);
            var message = OscMessage.ParseMessage(messageBytes);
            messages.Add(message);

            var output = new OscBundle((ulong)DateTime.Now.Ticks, messages.ToArray());
            return output;
        }
        private async Task OscReceiveTask(CancellationToken cancelToken = default)
        {
            while (!cancelToken.IsCancellationRequested)
            {
                try
                {
                    var packet = await _oscClient.ReceiveAsync();
                    if (IsBundle(packet.Buffer))
                    {
                        var bundle = ParseBundle(packet.Buffer);
                        var delay = bundle.Timestamp - DateTime.Now;
                        if (delay > TimeSpan.Zero && delay < TimeSpan.FromSeconds(1))
                        {
                            _ = Task.Run(
                                async () =>
                                {
                                    await Task.Delay(delay, cancelToken);
                                    OnOscBundle(bundle);
                                },
                                cancelToken
                            );
                        } else
                        {
                            OnOscBundle(bundle);
                        }
                    } else
                    {
                        OnOscMessage(OscMessage.ParseMessage(packet.Buffer));
                    }
                    foreach (var outputPort in Configuration.Instance.PortOutputs)
                    {
                        UdpClient udpClient = null;
                        if (!_portToUdpClientDictionary.ContainsKey(outputPort))
                        {
                            udpClient = new UdpClient();
                            udpClient.Connect(Configuration.Instance.OscIpAddress, outputPort);
                            _portToUdpClientDictionary[outputPort] = udpClient;
                        } else
                        {
                            udpClient = _portToUdpClientDictionary[outputPort];
                        }
                        _portToUdpClientDictionary[outputPort].SendAsync(packet.Buffer, packet.Buffer.Length);
                    }
                } catch (OperationCanceledException) { } catch (Exception e)
                {
                    Debug.WriteLine(e);
                }
            }
        }

        private void OnOscBundle(OscBundle bundle)
        {
            foreach (var message in bundle.Messages)
            {
                OnOscMessage(message);
            }
        }

        private void OnOscMessage(OscMessage message)
        {
            if (message.Arguments.Length <= 0)
            {
                return;
            }
            var events = ComputeEvents(message);
            foreach (var hapticEvent in events)
            {
                HapticsManager.SetNodeVibration(hapticEvent.Node, 300, (float)hapticEvent.Intensity);
            }
            //if (events.Length == 0 && HapticsManager.HapticsEngaged) {
            //    HapticsManager.StopNodeVibrations();
            //}
        }

        private HapticEvent[] ComputeEvents(OscMessage message)
        {
            if (message.Address.Length <= AvatarParamPrefix.Length)
            {
                return [];
            }

            var param = message.Address[AvatarParamPrefix.Length..];
            if (!parameterList.Contains(param))
            {
                parameterList.Add(param);
                Debug.WriteLine(param);
            }
            if (TryHandleDualSenseTrigger(param, message)) return [];
            foreach (var source in _hapticsSources)
            {
                if (source.IsSource(param, message))
                {
                    return source.ComputeHaptics(param, message);
                }
            }

            return [];
        }

        /// <summary>
        /// Maps VRChat avatar parameters DS5TriggerL / DS5TriggerR (int preset 0..4) to a trigger
        /// effect on the first connected DualSense. Returns true if the parameter was consumed.
        /// </summary>
        private static bool TryHandleDualSenseTrigger(string param, OscMessage message)
        {
            DualSenseOutput.TriggerSide? side = param switch
            {
                "DS5TriggerL" => DualSenseOutput.TriggerSide.Left,
                "DS5TriggerR" => DualSenseOutput.TriggerSide.Right,
                _ => null,
            };
            if (side == null) return false;
            if (message.Arguments.Length == 0) return true;
            int preset = message.Arguments[0] switch
            {
                int i => i,
                float f => (int)f,
                bool b => b ? 1 : 0,
                _ => 0,
            };
            preset = Math.Clamp(preset, 0, 4);
            try { DualSenseOutput.ApplyTrigger(0, side.Value, (DualSenseOutput.TriggerPreset)preset); } catch { }
            return true;
        }

        public void Dispose()
        {
            _cancelTokenSource.Cancel();
            _oscReceiveTask.Wait();
            _cancelTokenSource.Dispose();
            _oscClient.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
