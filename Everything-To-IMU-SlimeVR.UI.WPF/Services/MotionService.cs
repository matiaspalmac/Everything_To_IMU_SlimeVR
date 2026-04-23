using System.Windows;
using System.Windows.Media.Animation;

namespace Everything_To_IMU_SlimeVR.UI.Services;

public static class MotionService
{
    public static bool AnimationsEnabled { get; } = SystemParameters.ClientAreaAnimation && !SystemParameters.IsRemoteSession;

    public static void BeginIfAllowed(this Storyboard sb, FrameworkElement target)
    {
        if (!AnimationsEnabled) return;
        try { sb.Begin(target); } catch { }
    }

    public static void BeginIfAllowed(this Storyboard sb)
    {
        if (!AnimationsEnabled) return;
        try { sb.Begin(); } catch { }
    }
}
