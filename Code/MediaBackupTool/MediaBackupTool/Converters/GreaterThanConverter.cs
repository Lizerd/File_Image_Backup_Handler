using System.Globalization;
using System.Windows.Data;

namespace MediaBackupTool.Converters;

/// <summary>
/// Converter that returns true if the value is greater than the parameter.
/// Used for enabling/disabling pagination buttons.
/// </summary>
public class GreaterThanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intValue && parameter is string paramString && int.TryParse(paramString, out int compareValue))
        {
            return intValue > compareValue;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
