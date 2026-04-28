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
        AppServices.Instance.TrackerConnected += OnTrackerConnected;
        AppServices.Instance.TrackerDisconnected += OnTrackerDisconnected;
        // AppServices is process-lifetime — without explicit unsubscribe, every recreated
        // window stays rooted forever via its invocation list. Closed fires on real shutdown
        // (TrayExit forces _forceExit then Close), at which point we no longer want toasts.
        Closed += (_, _) =>
        {
            try { AppServices.Instance.BatteryLowAlert -= OnBatteryLow; } catch { }
            try { AppServices.Instance.TrackerConnected -= OnTrackerConnected; } catch { }
            try { AppServices.Instance.TrackerDisconnected -= OnTrackerDisconnected; } catch { }
        };
    }

    private static bool NotificationsOn() =>
        AppServices.Instance.Configuration?.NotificationsEnabled ?? true;

    private void OnBatteryLow(object? sender, BatteryLowEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (!NotificationsOn()) return;
                TrayIcon.ShowNotification(
                    title: "Tracker battery low",
                    message: $"{e.TrackerName} at {e.Percent}% — plug in or swap batteries soon.");
            }
            catch { }
        });
    }

    private void OnTrackerConnected(object? sender, TrackerNotificationEventArgs e)
    {
        // Skip toast while the main window is in foreground — the tracker grid already shows
        // the new row, a balloon would be redundant noise. Only fire when we're in the tray.
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (!NotificationsOn()) return;
                if (IsActive && WindowState != WindowState.Minimized) return;
                TrayIcon.ShowNotification(title: "Tracker connected", message: e.TrackerName);
            }
            catch { }
        });
    }

    private void OnTrackerDisconnected(object? sender, TrackerNotificationEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (!NotificationsOn()) return;
                if (IsActive && WindowState != WindowState.Minimized) return;
                TrayIcon.ShowNotification(title: "Tracker disconnected", message: e.TrackerName);
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
