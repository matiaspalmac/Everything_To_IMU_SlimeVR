using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Everything_To_IMU_SlimeVR.UI.Converters;

public class ResourceKeyToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && Application.Current?.Resources[key] is string s)
            return s;
        return value ?? string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
