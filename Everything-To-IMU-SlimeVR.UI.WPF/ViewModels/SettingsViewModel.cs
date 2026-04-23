using System.Diagnostics;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everything_To_IMU_SlimeVR.UI.Services;

namespace Everything_To_IMU_SlimeVR.UI.ViewModels;

public record ThemeOption(AppThemeMode Mode, string DisplayResourceKey);
public record LanguageOption(string Code, string DisplayName);

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private int _pollingRate = 8;
    [ObservableProperty] private int _wiiPollingRate = 32;
    [ObservableProperty] private bool _simulateThighs;
    [ObservableProperty] private bool _audioHapticsActive = true;

    public string AppVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

    public IReadOnlyList<ThemeOption> Themes { get; } = new[]
    {
        new ThemeOption(AppThemeMode.System, "Str.Settings.ThemeSystem"),
        new ThemeOption(AppThemeMode.Light,  "Str.Settings.ThemeLight"),
        new ThemeOption(AppThemeMode.Dark,   "Str.Settings.ThemeDark"),
    };

    public IReadOnlyList<LanguageOption> Languages { get; } = new[]
    {
        new LanguageOption("en", "English"),
        new LanguageOption("es", "Español"),
    };

    private ThemeOption _selectedTheme;
    public ThemeOption SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (_selectedTheme == value) return;
            _selectedTheme = value;
            OnPropertyChanged();
            ThemeManager.Apply(value.Mode, Application.Current?.MainWindow);
            Persist(c => c.Theme = ThemeManager.ToConfigString(value.Mode));
        }
    }

    private LanguageOption _selectedLanguage;
    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (_selectedLanguage == value) return;
            _selectedLanguage = value;
            OnPropertyChanged();
            LanguageService.Apply(value.Code);
            Persist(c => c.Language = value.Code);
            (Application.Current?.MainWindow?.DataContext as MainWindowViewModel)?.OnLanguageChanged();
        }
    }

    partial void OnPollingRateChanged(int value) => Persist(c => { c.PollingRate = value; AppServices.Instance.TrackerManager.PollingRate = value; });
    partial void OnWiiPollingRateChanged(int value) => Persist(c => c.WiiPollingRate = (byte)Math.Clamp(value, 1, 255));
    partial void OnSimulateThighsChanged(bool value) => Persist(c => c.SimulatesThighs = value);
    partial void OnAudioHapticsActiveChanged(bool value) => Persist(c => c.AudioHapticsActive = value);

    [RelayCommand]
    private void OpenLogs()
    {
        var dlg = new Views.LogsDialog { Owner = Application.Current?.MainWindow };
        dlg.ShowDialog();
    }

    [RelayCommand]
    private void OpenAppFolder()
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
            psi.ArgumentList.Add(AppContext.BaseDirectory);
            Process.Start(psi);
        }
        catch { }
    }

    public SettingsViewModel()
    {
        var cfg = AppServices.Instance.Configuration;
        if (cfg != null)
        {
            PollingRate = cfg.PollingRate;
            WiiPollingRate = cfg.WiiPollingRate;
            SimulateThighs = cfg.SimulatesThighs;
            AudioHapticsActive = cfg.AudioHapticsActive;
            var currentMode = ThemeManager.Parse(cfg.Theme);
            _selectedTheme = Themes.FirstOrDefault(t => t.Mode == currentMode) ?? Themes[0];
            _selectedLanguage = Languages.FirstOrDefault(l => l.Code == cfg.Language) ?? Languages[0];
        }
        else
        {
            _selectedTheme = Themes[0];
            _selectedLanguage = Languages[0];
        }
    }

    private static void Persist(Action<Configuration> mutate)
    {
        var cfg = AppServices.Instance.Configuration;
        if (cfg == null) return;
        try { mutate(cfg); cfg.SaveConfig(); } catch { }
    }
}
