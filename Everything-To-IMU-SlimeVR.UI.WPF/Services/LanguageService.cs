using System.Windows;

namespace Everything_To_IMU_SlimeVR.UI.Services;

public static class LanguageService
{
    public static string Current { get; private set; } = "en";

    public static void Apply(string lang)
    {
        lang = lang switch { "es" => "es", _ => "en" };
        Current = lang;

        var dicts = Application.Current.Resources.MergedDictionaries;
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Strings.en") || src.Contains("Strings.es"))
                dicts.RemoveAt(i);
        }
        dicts.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Resources/Strings.{lang}.xaml")
        });
    }
}

public static class ThemeApplier
{
    public static void Apply(string theme)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Theme.Dark") || src.Contains("Theme.Light"))
                dicts.RemoveAt(i);
        }
        string file = theme.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "Theme.Light.xaml" : "Theme.Dark.xaml";
        dicts.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Resources/{file}")
        });
    }
}
