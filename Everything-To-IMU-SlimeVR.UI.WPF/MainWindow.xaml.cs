using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Everything_To_IMU_SlimeVR.UI.Services;
using Wpf.Ui.Controls;

namespace Everything_To_IMU_SlimeVR.UI;

public partial class MainWindow : FluentWindow
{
    private bool _forceExit;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new ViewModels.MainWindowViewModel();
        Loaded += (_, _) => RootNavigation.Navigate(typeof(Views.TrackersPage));
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        AppServices.Instance.BatteryLowAlert += OnBatteryLow;
    }

    private void OnBatteryLow(object? sender, BatteryLowEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                TrayIcon.ShowNotification(
                    title: "Tracker battery low",
                    message: $"{e.TrackerName} at {e.Percent}% — plug in or swap batteries soon.");
            }
            catch { }
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var cfg = AppServices.Instance.Configuration;
        var mode = ThemeManager.Parse(cfg?.Theme);
        ThemeManager.Apply(mode, this);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            ShowInTaskbar = false;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_forceExit) return;
        e.Cancel = true;
        WindowState = WindowState.Minimized;
    }

    private void TrayIcon_LeftClick(object sender, RoutedEventArgs e) => ShowFromTray();

    private void TrayShow_Click(object sender, RoutedEventArgs e) => ShowFromTray();

    private void TrayHide_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _forceExit = true;
        TrayIcon.Dispose();
        Application.Current.Shutdown();
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }
}
