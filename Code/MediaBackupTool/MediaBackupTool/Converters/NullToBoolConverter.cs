using System.Globalization;
using System.Windows.Data;

namespace MediaBackupTool.Converters;

/// <summary>
/// Converts null to bool.
/// Not null = true, null = false.
/// </summary>
public class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasValue = value != null;

        // If parameter is "Invert", reverse the logic
        if (parameter is string param && param.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            return !hasValue;
        }

        return hasValue;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("NullToBoolConverter does not support ConvertBack");
    }
}
