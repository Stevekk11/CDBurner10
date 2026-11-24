using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace CDBurner10.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool b = value is true;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        bool b = value is Visibility.Visible;
        return Invert ? !b : b;
    }
}

public sealed class StringNullOrEmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool hasText = !string.IsNullOrWhiteSpace(value as string);
        if (Invert) hasText = !hasText;
        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public static class FormatHelper
{
    public static string FormatBytes(long bytes) =>
        FormatBytes(bytes, CDBurner10.Core.Settings.SettingsService.Default.Current.OnlyUseSingleUnit);

    public static string FormatBytes(long bytes, bool singleUnitMb)
    {
        if (bytes < 0) return singleUnitMb ? "0 MB" : "0 B";
        if (singleUnitMb)
        {
            double mb = bytes / (1024.0 * 1024.0);
            return $"{mb:0.##} MB";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        return $"{duration.Minutes:D2}:{duration.Seconds:D2}";
    }
}
