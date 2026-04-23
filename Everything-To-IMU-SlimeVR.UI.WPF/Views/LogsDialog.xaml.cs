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
            using var sr = new StreamReader(fs);
            var content = sr.ReadToEnd();
            // Keep to last ~200 KB to avoid UI freeze on huge logs.
            if (content.Length > 200_000) content = "... (truncated to last 200 KB)\n" + content[^200_000..];
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
