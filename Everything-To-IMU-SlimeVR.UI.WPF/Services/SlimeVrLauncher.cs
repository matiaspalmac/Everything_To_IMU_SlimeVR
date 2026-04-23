using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Everything_To_IMU_SlimeVR.UI.Services;

/// <summary>
/// Detects local SlimeVR server install + exposes commands to launch it or open its web dashboard.
/// </summary>
public static class SlimeVrLauncher
{
    private const string DownloadUrl = "https://slimevr.dev/download";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    private static readonly string[] CandidatePaths = new[]
    {
        // Typical install locations for SlimeVR Server on Windows.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SlimeVR", "slimevr.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlimeVR", "slimevr.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SlimeVR Server", "slimevr.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SlimeVR Server", "slimevr.exe"),
    };

    public static string? FindInstalledExecutable()
    {
        foreach (var p in CandidatePaths)
        {
            try { if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    public static bool IsInstalled() => FindInstalledExecutable() != null;

    /// <summary>
    /// Try to spawn the installed SlimeVR server. Returns true if the process was started.
    /// </summary>
    public static bool LaunchServer()
    {
        var exe = FindInstalledExecutable();
        if (exe == null) return false;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Bring the SlimeVR Server desktop window to the foreground if running, otherwise launch it.
    /// SlimeVR's GUI is a Tauri desktop app (not a web dashboard).
    /// </summary>
    public static void OpenDashboard()
    {
        // Try to find the existing SlimeVR Server process window and focus it.
        foreach (var candidate in new[] { "slimevr-ui", "slimevr", "SlimeVR Server", "SlimeVR" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(candidate))
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindow(p.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(p.MainWindowHandle);
                    return;
                }
            }
            catch { }
        }
        // Not running — launch it.
        if (!LaunchServer()) OpenDownloadPage();
    }

    public static void OpenDownloadPage() => OpenUrl(DownloadUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch { }
    }
}
