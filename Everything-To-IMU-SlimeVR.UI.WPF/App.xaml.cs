using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;
using AutoUpdaterDotNET;
using Everything_To_IMU_SlimeVR.UI.Services;

namespace Everything_To_IMU_SlimeVR.UI;

public partial class App : Application
{
    // Crash + update logs were originally written next to the .exe. That path is read-only
    // when the app is installed under Program Files, and stack traces routinely embed
    // BLE addresses (Joy-Con 2 reconnect failures, etc.) which leak controller MACs into
    // any file the user shares for support. Move logs to %LOCALAPPDATA% and run every
    // payload through a MAC redactor on the way in.
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EverythingToImuSlimeVR", "logs");

    private static readonly Regex BluetoothAddressRegex = new(
        @"\b(?:bluetoothAddress|BluetoothAddress|MAC|address)\s*[:=]\s*[0-9A-Fa-f]{12}\b",
        RegexOptions.Compiled);
    // Bare 12-hex tokens (`X12` formatted MACs) and colon/dash-separated MACs.
    private static readonly Regex BareMacRegex = new(
        @"\b(?:[0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}\b|\b[0-9A-Fa-f]{12}\b",
        RegexOptions.Compiled);

    private static string GetLogPath(string fileName)
    {
        try { Directory.CreateDirectory(LogDirectory); } catch { }
        return Path.Combine(LogDirectory, fileName);
    }

    private static string Redact(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return payload;
        try
        {
            // BluetoothAddressRegex first so the labelled form ("bluetoothAddress:0123ABCDEF12")
            // collapses to a single token rather than leaving the label dangling next to
            // <redacted-mac>. Both replacements use the same sentinel.
            payload = BluetoothAddressRegex.Replace(payload, "<redacted-mac>");
            payload = BareMacRegex.Replace(payload, "<redacted-mac>");
            return payload;
        }
        catch { return payload; }
    }

    private static void AppendCrashLog(string label, string body)
    {
        try
        {
            var log = GetLogPath("crash.log");
            File.AppendAllText(log, $"[{DateTime.UtcNow:O}] {label}: {Redact(body)}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global safety net: unhandled background exceptions (async void, ThreadPool Tasks)
        // are the usual cause of silent crashes when an external dependency (SlimeVR server,
        // Bluetooth stack) vanishes mid-run. Log + swallow so the app stays alive.
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            AppendCrashLog("UnhandledException", ev.ExceptionObject?.ToString() ?? "<null>");
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            AppendCrashLog("UnobservedTaskException", ev.Exception?.ToString() ?? "<null>");
            ev.SetObserved();
        };
        DispatcherUnhandledException += (_, ev) =>
        {
            try
            {
                AppendCrashLog("DispatcherException", ev.Exception?.ToString() ?? "<null>");
                ev.Handled = true;
                // First time per session, surface a non-blocking toast so the user knows
                // their UI hit something unexpected and can grab crash.log. Previously we
                // logged silently and the user saw "everything looks fine" until the next
                // weirdness compounded.
                if (!_unhandledNotified)
                {
                    _unhandledNotified = true;
                    try
                    {
                        MessageBox.Show(
                            "An unexpected error was logged to crash.log. The app will keep running, " +
                            "but please share that file if you hit any further issues.",
                            "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    catch { }
                }
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
    // One-shot flag so the dispatcher exception toast doesn't fire repeatedly if the same
    // bug spams a hot path.
    private static bool _unhandledNotified;

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
            var candidates = Directory.EnumerateFiles(AppDomain.CurrentDomain.BaseDirectory, "Everything-To-IMU-SlimeVR-*.zip")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            string? path = candidates.Count == 0 ? null : candidates[0];

            if (string.IsNullOrWhiteSpace(_expectedChecksumSha256))
            {
                // Missing checksum is now fatal. The previous behaviour ("skip verification
                // (insecure)") is a TOFU footgun: an attacker who can serve update.xml without
                // the <checksum> element can ship arbitrary code via the auto-updater. Refuse
                // to apply the zip, scrub it from disk, and surface the failure so the user
                // knows the update was rejected.
                LogUpdate("Checksum missing from update.xml — refusing to apply update.");
                if (path != null)
                {
                    try { File.Delete(path); } catch (Exception delEx) { LogUpdate($"Failed to delete unverified zip: {delEx.Message}"); }
                }
                MessageBox.Show(
                    "The update package could not be verified because update.xml does not include a checksum. " +
                    "The download was discarded and the running version will continue to be used.",
                    "Update verification failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                Environment.Exit(1);
                return;
            }
            if (path == null) { LogUpdate("Verify: no downloaded zip found"); return; }
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
            var log = GetLogPath("update.log");
            File.AppendAllText(log, $"[{DateTime.UtcNow:O}] {Redact(msg)}{Environment.NewLine}");
        }
        catch { }
    }
}
