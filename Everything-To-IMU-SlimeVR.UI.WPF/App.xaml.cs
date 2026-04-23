using System.Diagnostics;
using System.IO;
using System.Windows;
using AutoUpdaterDotNET;
using Everything_To_IMU_SlimeVR.UI.Services;

namespace Everything_To_IMU_SlimeVR.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global safety net: unhandled background exceptions (async void, ThreadPool Tasks)
        // are the usual cause of silent crashes when an external dependency (SlimeVR server,
        // Bluetooth stack) vanishes mid-run. Log + swallow so the app stays alive.
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            try
            {
                var log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                File.AppendAllText(log, $"[{DateTime.UtcNow:O}] UnhandledException: {ev.ExceptionObject}{Environment.NewLine}");
            }
            catch { }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            try
            {
                var log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                File.AppendAllText(log, $"[{DateTime.UtcNow:O}] UnobservedTaskException: {ev.Exception}{Environment.NewLine}");
                ev.SetObserved();
            }
            catch { }
        };
        DispatcherUnhandledException += (_, ev) =>
        {
            try
            {
                var log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                File.AppendAllText(log, $"[{DateTime.UtcNow:O}] DispatcherException: {ev.Exception}{Environment.NewLine}");
                ev.Handled = true;
            }
            catch { }
        };

        try
        {
            Behaviors.ScrollViewerHelper.RegisterGlobal();
            AppServices.Instance.Initialize();

            var cfg = AppServices.Instance.Configuration;
            LanguageService.Apply(cfg?.Language ?? "en");

            // Theme is applied in MainWindow.OnSourceInitialized so SystemThemeWatcher has a valid HWND
            TryStartAutoUpdater();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to initialize:\n{ex}", "Startup error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppServices.Instance.Shutdown(); } catch { }
        base.OnExit(e);
    }

    private static void TryStartAutoUpdater()
    {
        try
        {
            AutoUpdater.DownloadPath = AppDomain.CurrentDomain.BaseDirectory;
            AutoUpdater.Synchronous = false;
            AutoUpdater.Mandatory = false;
            AutoUpdater.UpdateMode = Mode.Normal;
            AutoUpdater.Start("https://raw.githubusercontent.com/Sebane1/Everything_To_IMU_SlimeVR/main/update.xml");
        }
        catch (Exception ex)
        {
            var log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update.log");
            File.AppendAllText(log, $"[{DateTime.UtcNow:O}] AutoUpdater failed: {ex}{Environment.NewLine}");
        }
    }
}
