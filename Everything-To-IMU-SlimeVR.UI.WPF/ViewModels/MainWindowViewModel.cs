using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Everything_To_IMU_SlimeVR.UI.Services;

namespace Everything_To_IMU_SlimeVR.UI.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly DispatcherTimer _statusTimer;
    private static Brush GreenBrush => (Brush?)Application.Current.Resources["SuccessBrush"] ?? Brushes.Green;
    private static Brush RedBrush => (Brush?)Application.Current.Resources["ErrorBrush"] ?? Brushes.Red;
    private static Brush GrayBrush => (Brush?)Application.Current.Resources["TextTertiaryBrush"] ?? Brushes.Gray;

    [ObservableProperty] private string _slimeVrStatusText = "";
    [ObservableProperty] private string _slimeVrStatusTooltip = "SlimeVR target: ws://localhost:21110 — click to retry (launches server if installed)";
    [ObservableProperty] private Brush _slimeVrStatusBrush = Brushes.Gray;
    [ObservableProperty] private string _oscStatusText = "";
    [ObservableProperty] private Brush _oscStatusBrush = Brushes.Gray;
    [ObservableProperty] private string _controllerCountText = "";
    [ObservableProperty] private string _vrChatLogStatusText = "";

    // Launch readiness checklist
    [ObservableProperty] private bool _checkSlimeVr;
    [ObservableProperty] private bool _checkTrackers;
    [ObservableProperty] private bool _checkOsc;
    [ObservableProperty] private bool _isReadyToLaunch;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void LaunchSteamVr()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "steam://run/250820",
                UseShellExecute = true,
            });
        }
        catch
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "vrmonitor://",
                    UseShellExecute = true,
                });
            }
            catch { }
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task RetrySlimeVr()
    {
        SlimeVrStatusText = L("Str.Status.SlimeChecking");
        SlimeVrStatusBrush = GrayBrush;
        bool up = await SlimeVrStatusProbe.IsUp();
        if (!up)
        {
            // Try to launch installed server; fall back to opening download page.
            if (SlimeVrLauncher.IsInstalled())
            {
                SlimeVrLauncher.LaunchServer();
                await Task.Delay(2000);
            }
            else
            {
                SlimeVrLauncher.OpenDownloadPage();
            }
        }
        await RefreshStatusAsync();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenSlimeVrDashboard() => SlimeVrLauncher.OpenDashboard();

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ShowShortcuts()
    {
        var dlg = new Views.ShortcutsDialog { Owner = Application.Current?.MainWindow };
        dlg.ShowDialog();
    }

    // Reentry guard for the 2 s status probe. SlimeVR is unreachable + retry → IsUp() can
    // exceed the 2 s tick interval, two RefreshStatusAsync chains race writing the same
    // observable fields. _refreshing makes the second tick skip until the first completes.
    private int _refreshing;

    public MainWindowViewModel()
    {
        SlimeVrStatusText = L("Str.Status.SlimeChecking");
        SlimeVrStatusBrush = GrayBrush;
        OscStatusBrush = GrayBrush;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += OnStatusTick;
        _statusTimer.Start();
        _ = RefreshStatusAsync();
    }

    private async void OnStatusTick(object? sender, EventArgs e)
    {
        // Skip if a previous probe is still in flight. Atomic CAS avoids reentry.
        if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try { await RefreshStatusAsync(); }
        catch { }
        finally { System.Threading.Interlocked.Exchange(ref _refreshing, 0); }
    }

    /// <summary>
    /// Stop the background probe timer. Call from MainWindow.Closed so ticks don't keep
    /// firing during AppServices.Shutdown's config flush.
    /// </summary>
    public void StopBackgroundWork()
    {
        try { _statusTimer.Stop(); } catch { }
        try { _statusTimer.Tick -= OnStatusTick; } catch { }
    }

    public void OnLanguageChanged() => _ = RefreshStatusAsync();

    private static string L(string key) => Application.Current.Resources[key] as string ?? key;

    private async Task RefreshStatusAsync()
    {
        try
        {
            bool slimeUp = await SlimeVrStatusProbe.IsUp();
            SlimeVrStatusText = slimeUp ? L("Str.Status.SlimeConnected") : L("Str.Status.SlimeOffline");
            SlimeVrStatusBrush = slimeUp ? GreenBrush : RedBrush;

            var cfg = AppServices.Instance.Configuration;
            if (cfg != null)
            {
                OscStatusText = L("Str.Status.OscInPrefix").TrimEnd() + " " + cfg.PortInput;
                OscStatusBrush = GreenBrush;
            }

            ControllerCountText = L("Str.Status.ControllersPrefix").TrimEnd() + " " + AppServices.Instance.TotalTrackerCount;

            string vrcLogPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).Replace("Local", "LocalLow"),
                "VRChat", "VRChat");
            VrChatLogStatusText = Directory.Exists(vrcLogPath) ? L("Str.Status.VrcWatching") : L("Str.Status.VrcOff");

            // Launch readiness
            CheckSlimeVr = slimeUp;
            CheckTrackers = AppServices.Instance.TotalTrackerCount > 0;
            CheckOsc = cfg != null && int.TryParse(cfg.PortInput, out var p) && p > 0 && p < 65536;
            IsReadyToLaunch = CheckSlimeVr && CheckTrackers && CheckOsc;
        }
        catch { }
    }
}
