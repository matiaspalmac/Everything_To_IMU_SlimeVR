using Everything_To_IMU_SlimeVR.Osc;

namespace Everything_To_IMU_SlimeVR.Tracking {
    internal static class LedColorPalette {
        public static int ForBodyBinding(HapticNodeBinding binding) => binding switch {
            HapticNodeBinding.Hips or HapticNodeBinding.HipsFront or HapticNodeBinding.HipsBack
                => unchecked((int)0xFFED6A5A), // red
            HapticNodeBinding.Chest or HapticNodeBinding.ChestFront or HapticNodeBinding.ChestBack
                => unchecked((int)0xFF6E8CF0), // blue
            HapticNodeBinding.ChestAndHips or HapticNodeBinding.ChestAndHipsFront or HapticNodeBinding.ChestAndHipsBack
                => unchecked((int)0xFFB067E6), // purple
            HapticNodeBinding.LeftFoot => unchecked((int)0xFF5CD65C),  // green
            HapticNodeBinding.RightFoot => unchecked((int)0xFF2FA32F), // dark green
            HapticNodeBinding.LeftCalf => unchecked((int)0xFF7ED957),
            HapticNodeBinding.RightCalf => unchecked((int)0xFF4AAF2F),
            HapticNodeBinding.LeftThigh => unchecked((int)0xFFA8D93F),
            HapticNodeBinding.RightThigh => unchecked((int)0xFF7FB332),
            HapticNodeBinding.LeftHand => unchecked((int)0xFFFFC857),
            HapticNodeBinding.RightHand => unchecked((int)0xFFFF8F1F),
            HapticNodeBinding.LeftForeArm => unchecked((int)0xFFFFD780),
            HapticNodeBinding.RightForeArm => unchecked((int)0xFFFFA640),
            HapticNodeBinding.LeftUpperArm => unchecked((int)0xFFE8B94A),
            HapticNodeBinding.RightUpperArm => unchecked((int)0xFFCC7A18),
            HapticNodeBinding.LeftShoulder => unchecked((int)0xFFFFE3B3),
            HapticNodeBinding.RightShoulder => unchecked((int)0xFFFFB366),
            HapticNodeBinding.Head => unchecked((int)0xFFF5F5F5), // white
            _ => unchecked((int)0xFF6E8CF0),
        };
    }
}
