using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MediaBackupTool.Converters;

/// <summary>
/// Converts int to Visibility.
/// Non-zero = Visible, zero = Collapsed.
/// </summary>
public class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int intValue = 0;

        if (value is int i)
            intValue = i;
        else if (value is long l)
            intValue = (int)l;
        else if (value != null && int.TryParse(value.ToString(), out var parsed))
            intValue = parsed;

        bool hasValue = intValue != 0;

        // If parameter is "Invert", reverse the logic
        if (parameter is string param && param.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            return hasValue ? Visibility.Collapsed : Visibility.Visible;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("IntToVisibilityConverter does not support ConvertBack");
    }
}
