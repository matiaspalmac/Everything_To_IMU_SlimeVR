using System.Globalization;
using System.Windows.Data;

namespace Everything_To_IMU_SlimeVR.UI.Converters;

public class ShortcutKeyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s.Contains('|') ? s.Split('|', 2)[0] : value ?? "";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class ShortcutDescConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s.Contains('|') ? s.Split('|', 2)[1] : value ?? "";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
