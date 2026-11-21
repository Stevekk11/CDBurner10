using System.Globalization;

namespace CDBurner10.Core.Utilities;

public static class FileSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes, bool useSingleMbUnit = false)
    {
        if (bytes < 0) bytes = 0;

        if (useSingleMbUnit)
        {
            double mb = bytes / (1024.0 * 1024.0);
            return $"{mb.ToString("N2", CultureInfo.CurrentCulture)} MB";
        }

        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024.0 && unitIndex < Units.Length - 1)
        {
            value /= 1024.0;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes:N0} {Units[0]}"
            : $"{value.ToString("N2", CultureInfo.CurrentCulture)} {Units[unitIndex]}";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{(int)duration.TotalMinutes:D2}:{duration.Seconds:D2}";
    }
}
