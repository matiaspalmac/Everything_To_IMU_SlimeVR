using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Xml.Linq;
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

    private const string UpdateManifestUrl = "https://raw.githubusercontent.com/matiaspalmac/Everything_To_IMU_SlimeVR/main/update.xml";
    private static string? _expectedChecksumSha256;

    private static void TryStartAutoUpdater()
    {
        try
        {
            AutoUpdater.DownloadPath = AppDomain.CurrentDomain.BaseDirectory;
            AutoUpdater.Synchronous = false;
            AutoUpdater.Mandatory = false;
            AutoUpdater.UpdateMode = Mode.Normal;
            // Parse <checksum> field ourselves (AutoUpdater.NET does not handle non-standard
            // children). Stored for use by ApplicationExitEvent hook that runs after download
            // completes and before the package replaces the running exe. Run on the
            // threadpool — the previous synchronous fetch on the UI thread blocked the
            // splash for up to 10 s on slow / hijacked DNS.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                _expectedChecksumSha256 = TryFetchExpectedChecksum(UpdateManifestUrl);
            });
            AutoUpdater.ApplicationExitEvent += VerifyDownloadedZip;
            AutoUpdater.Start(UpdateManifestUrl);
        }
        catch (Exception ex)
        {
            LogUpdate($"AutoUpdater start failed: {ex}");
        }
    }

    private static string? TryFetchExpectedChecksum(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var xml = http.GetStringAsync(url).GetAwaiter().GetResult();
            var doc = XDocument.Parse(xml);
            var node = doc.Root?.Element("checksum");
            var alg = node?.Attribute("algorithm")?.Value;
            if (!string.Equals(alg, "SHA256", StringComparison.OrdinalIgnoreCase)) return null;
            var hex = node?.Value?.Trim();
            return string.IsNullOrEmpty(hex) ? null : hex;
        }
        catch (Exception ex)
        {
            LogUpdate($"Checksum prefetch failed: {ex.Message}");
            return null;
        }
    }

    private static void VerifyDownloadedZip()
    {
        // The downloaded file is the most recent .zip in DownloadPath matching the release
        // naming pattern. If AutoUpdater.NET changes its layout this fallback still works.
        try
        {
            if (string.IsNullOrWhiteSpace(_expectedChecksumSha256))
            {
                LogUpdate("Checksum missing from update.xml — skipping verification (insecure). " +
                          "This will be treated as a fatal error starting with v0.3.0.");
                return;
            }
            var candidates = Directory.EnumerateFiles(AppDomain.CurrentDomain.BaseDirectory, "Everything-To-IMU-SlimeVR-*.zip")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            if (candidates.Count == 0) { LogUpdate("Verify: no downloaded zip found"); return; }
            var path = candidates[0];
            string actual;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                actual = Convert.ToHexString(sha.ComputeHash(fs));

            if (!string.Equals(actual, _expectedChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                LogUpdate($"CHECKSUM MISMATCH on {Path.GetFileName(path)}: expected {_expectedChecksumSha256} got {actual}. Deleting.");
                try { File.Delete(path); } catch { }
                MessageBox.Show(
                    "Downloaded update failed checksum verification and was discarded. " +
                    "This could indicate a corrupted download or a tampered package.",
                    "Update verification failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                Environment.Exit(1); // refuse to continue — AutoUpdater would apply the zip next
            }
            LogUpdate($"Checksum OK on {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            LogUpdate($"Verify exception: {ex}");
        }
    }

    private static void LogUpdate(string msg)
    {
        try
        {
            var log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update.log");
            File.AppendAllText(log, $"[{DateTime.UtcNow:O}] {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
