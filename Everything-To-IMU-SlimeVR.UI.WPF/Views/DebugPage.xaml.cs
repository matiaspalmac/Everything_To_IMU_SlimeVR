using System.Windows.Controls;
using System.Windows.Threading;
using Everything_To_IMU_SlimeVR.UI.ViewModels;

namespace Everything_To_IMU_SlimeVR.UI.Views;

public partial class DebugPage : Page
{
    private DebugViewModel? _vm;
    private DispatcherTimer? _renderTimer;

    public DebugPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not DebugViewModel vm) return;
        _vm = vm;
        vm.SampleReady += OnSample;
        vm.ClearRequested += OnClear;

        TogX.Checked += (_, _) => Chart.ShowX = true;
        TogX.Unchecked += (_, _) => Chart.ShowX = false;
        TogY.Checked += (_, _) => Chart.ShowY = true;
        TogY.Unchecked += (_, _) => Chart.ShowY = false;
        TogZ.Checked += (_, _) => Chart.ShowZ = true;
        TogZ.Unchecked += (_, _) => Chart.ShowZ = false;

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _renderTimer.Tick += (_, _) => Chart.Render();
        _renderTimer.Start();
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.SampleReady -= OnSample;
            _vm.ClearRequested -= OnClear;
        }
        _renderTimer?.Stop();
        _renderTimer = null;
    }

    private void OnClear(object? sender, EventArgs e) => Chart.Clear();

    private void OnSample(object? sender, System.Numerics.Vector3 euler)
    {
        // Cheap push; render timer handles repaint.
        Chart.AddSample(euler.X, euler.Y, euler.Z);
    }
}
