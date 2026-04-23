using System.Numerics;

namespace Everything_To_IMU_SlimeVR.Tracking {
    public interface IBodyTracker {
        int Id { get; set; }
        public bool SupportsHaptics { get;}
        public bool SupportsIMU {  get; }
        string MacSpoof { get; set; }
        Vector3 Euler { get; set; }
        float LastHmdPositon { get; set; }
        bool Ready {  get; set; }
        HapticNodeBinding HapticNodeBinding { get; set; }
        public Vector3 RotationCalibration { get; set; }
        TrackerConfig.RotationReferenceType YawReferenceTypeValue { get; set; }

        TrackerConfig.RotationReferenceType ExtensionYawReferenceTypeValue { get; set; }
        string Debug { get; }

        // Packet telemetry — optional overrides (default via UDPHandler counters).
        long PacketsSent => 0;
        long SendFailures => 0;
        bool ServerReachable => true;

        void Rediscover();
        Vector3 GetCalibration();

        void Identify();

        void HapticIntensityTest();

        public void EngageHaptics(int duration, float intensity);
        public void DisableHaptics();

        public string ToString();
    }
}