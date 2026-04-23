using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Everything_To_IMU_SlimeVR.UI.Converters;

/// <summary>
/// Converts an enum value to a localized display string. Looks up
/// Str.Enum.{EnumTypeName}.{EnumValueName} in merged resources, falls back to value.ToString().
/// </summary>
public class EnumDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null) return "";
        var type = value.GetType();
        if (!type.IsEnum) return value.ToString() ?? "";
        var key = $"Str.Enum.{type.Name}.{value}";
        if (Application.Current?.Resources[key] is string s) return s;
        return value.ToString() ?? "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
