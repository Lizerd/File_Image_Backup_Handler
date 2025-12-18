using System.Globalization;
using System.Windows.Data;

namespace MediaBackupTool.Converters;

/// <summary>
/// Converts file size in bytes to human-readable format (KB, MB, GB).
/// </summary>
public class FileSizeConverter : IValueConverter
{
    private static readonly string[] Suffixes = { "B", "KB", "MB", "GB", "TB" };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            return FormatFileSize(bytes);
        }
        if (value is int intBytes)
        {
            return FormatFileSize(intBytes);
        }
        return "0 B";
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes == 0) return "0 B";

        int suffixIndex = 0;
        double size = bytes;

        while (size >= 1024 && suffixIndex < Suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        return $"{size:0.##} {Suffixes[suffixIndex]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
