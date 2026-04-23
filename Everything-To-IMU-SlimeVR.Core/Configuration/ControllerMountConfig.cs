namespace Everything_To_IMU_SlimeVR {
    /// <summary>
    /// Per-controller mounting preferences persisted across sessions. Keyed off the tracker's
    /// stable MacSpoof so the preference follows the physical device rather than its enumeration
    /// slot. Applied as a yaw offset multiplied onto the tracker's fused rotation before it goes
    /// to SlimeVR, so the user can strap the controller on in any 90° orientation and correct
    /// the mounting once without re-calibrating after every reconnect.
    /// </summary>
    public class ControllerMountConfig {
        // Degrees around the tracker's local Z (yaw) axis. Constrained to 0/90/180/270 by
        // the Bump helper on Configuration; stored as int so it survives JSON round-trips
        // without float precision drift.
        public int YawDegrees { get; set; }

        // Multiplier applied to gyro samples before they hit the fusion filter. Lets the user
        // trim out per-chip drift on controllers where we don't have access to a factory bias
        // (Joy-Con 2 BLE path). 1.0 = no change. Reasonable range 0.95–1.05; values outside
        // that band usually mean something else is wrong (mounting, axis convention).
        public float GyroScaleTrim { get; set; } = 1.0f;
    }
}
