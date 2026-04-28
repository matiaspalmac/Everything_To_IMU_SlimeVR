using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Everything_To_IMU_SlimeVR.Tracking;
using Wpf.Ui.Controls;

namespace Everything_To_IMU_SlimeVR.UI.Views;

public partial class HapticCalibratorDialog : FluentWindow
{
    private readonly IBodyTracker _tracker;
    private readonly DispatcherTimer _pulseTimer;
    private float _intensity;

    public HapticCalibratorDialog(IBodyTracker tracker)
    {
        InitializeComponent();
        _tracker = tracker;
        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        // Skip the pulse when the slider hasn't been touched yet — without this guard the
        // dialog spammed EngageHaptics(90, 0) the moment it opened, which on Joy-Con / DS5
        // BT wakes the rumble subsystem (and adds load to a BT stack we already audit for
        // pressure under 4× JC1 + phone).
        _pulseTimer.Tick += (_, _) =>
        {
            if (_intensity <= 0f) return;
            try { _tracker.EngageHaptics(90, _intensity); } catch { }
        };
        _pulseTimer.Start();
        Closed += (_, _) => _pulseTimer.Stop();
    }

    private void IntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _intensity = (float)e.NewValue;
        try { _tracker.EngageHaptics(50, _intensity); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
