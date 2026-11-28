using CDBurner10.Core.Utilities;

namespace CDBurner10.App.Converters;

public static class FormatHelper
{
    public static string FormatBytes(long bytes) =>
        FileSizeFormatter.Format(bytes, Core.Settings.SettingsService.Default.Current.OnlyUseSingleUnit);

    public static string FormatBytes(long bytes, bool singleUnitMb) =>
        FileSizeFormatter.Format(bytes, singleUnitMb);

    public static string FormatDuration(TimeSpan duration) =>
        FileSizeFormatter.FormatDuration(duration);
}