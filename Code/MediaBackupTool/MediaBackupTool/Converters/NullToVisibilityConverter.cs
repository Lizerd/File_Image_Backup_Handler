using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MediaBackupTool.Converters;

/// <summary>
/// Converts null/empty to Visibility.
/// Not null/not empty = Visible, null/empty = Collapsed.
/// For strings, empty string is treated as null.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasValue;

        if (value is string str)
        {
            hasValue = !string.IsNullOrWhiteSpace(str);
        }
        else
        {
            hasValue = value != null;
        }

        // If parameter is "Invert", reverse the logic
        if (parameter is string param && param.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            return hasValue ? Visibility.Collapsed : Visibility.Visible;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("NullToVisibilityConverter does not support ConvertBack");
    }
}
