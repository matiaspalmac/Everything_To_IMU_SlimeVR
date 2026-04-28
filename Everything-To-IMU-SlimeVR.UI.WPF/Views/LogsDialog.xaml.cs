using System.Diagnostics;
using System.IO;
using System.Windows;
using Wpf.Ui.Controls;

namespace Everything_To_IMU_SlimeVR.UI.Views;

public partial class LogsDialog : FluentWindow
{
    private readonly string _baseDir = AppContext.BaseDirectory;

    public LogsDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LogFileCombo.ItemsSource = new[] { "errors.log", "update.log", "fatalcrash.log" };
        LogFileCombo.SelectedIndex = 0;
    }

    private void LoadSelected()
    {
        if (LogFileCombo.SelectedItem is not string name)
        {
            LogText.Text = "";
            return;
        }
        var path = Path.Combine(_baseDir, name);
        if (!File.Exists(path))
        {
            LogText.Text = $"(empty — {name} has not been written yet)";
            return;
        }
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // Seek to the last 200 KB instead of reading the whole file then trimming. A 2 GB
            // log hand-loaded into memory froze the dialog (and pressured GC for the rest of
            // the session). Seek-from-end keeps memory and load time bounded regardless of
            // log size.
            const long TailBytes = 200_000;
            string content;
            if (fs.Length > TailBytes)
            {
                fs.Seek(-TailBytes, SeekOrigin.End);
                using var sr = new StreamReader(fs);
                // Drop the partial first line — we likely landed mid-character.
                _ = sr.ReadLine();
                content = "... (truncated to last 200 KB)\n" + sr.ReadToEnd();
            }
            else
            {
                using var sr = new StreamReader(fs);
                content = sr.ReadToEnd();
            }
            LogText.Text = content;
        }
        catch (Exception ex)
        {
            LogText.Text = $"(failed to read: {ex.Message})";
        }
        LogText.ScrollToEnd();
    }

    private void LogFileCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => LoadSelected();

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadSelected();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
            psi.ArgumentList.Add(_baseDir);
            Process.Start(psi);
        }
        catch { }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogText.Text ?? ""); } catch { }
    }
}
