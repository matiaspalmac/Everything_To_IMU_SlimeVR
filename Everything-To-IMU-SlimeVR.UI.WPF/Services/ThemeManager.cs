using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Everything_To_IMU_SlimeVR.UI.Services;

public enum AppThemeMode { System, Light, Dark }

public static class ThemeManager
{
    private static Window? _watchedWindow;

    public static AppThemeMode Current { get; private set; } = AppThemeMode.System;

    public static void Apply(AppThemeMode mode, Window? window)
    {
        Current = mode;

        // Stop any previous watcher
        if (_watchedWindow != null)
        {
            try { SystemThemeWatcher.UnWatch(_watchedWindow); } catch { }
            ApplicationThemeManager.Changed -= OnAppThemeChanged;
            _watchedWindow = null;
        }

        if (mode == AppThemeMode.System)
        {
            var sysTheme = MapSystemTheme(ApplicationThemeManager.GetSystemTheme());
            ApplicationThemeManager.Apply(sysTheme, WindowBackdropType.Mica, updateAccent: true);
            SwapTokenDict(sysTheme);

            if (window != null)
            {
                _watchedWindow = window;
                SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, updateAccents: true);
                ApplicationThemeManager.Changed += OnAppThemeChanged;
            }
        }
        else
        {
            var theme = mode == AppThemeMode.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: true);
            SwapTokenDict(theme);
        }
    }

    private static ApplicationTheme MapSystemTheme(SystemTheme sys) => sys switch
    {
        SystemTheme.Dark or SystemTheme.Glow or SystemTheme.CapturedMotion => ApplicationTheme.Dark,
        _ => ApplicationTheme.Light,
    };

    private static void OnAppThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        Application.Current.Dispatcher.Invoke(() => SwapTokenDict(theme));
    }

    private static void SwapTokenDict(ApplicationTheme theme)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Theme.Dark") || src.Contains("Theme.Light"))
                dicts.RemoveAt(i);
        }
        string file = theme == ApplicationTheme.Dark ? "Theme.Dark.xaml" : "Theme.Light.xaml";
        dicts.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Resources/{file}")
        });
    }

    public static AppThemeMode Parse(string? s) => s switch
    {
        "Light" => AppThemeMode.Light,
        "Dark" => AppThemeMode.Dark,
        _ => AppThemeMode.System,
    };

    public static string ToConfigString(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => "Light",
        AppThemeMode.Dark => "Dark",
        _ => "System",
    };
}
