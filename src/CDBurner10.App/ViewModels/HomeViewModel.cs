using System.Collections.ObjectModel;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDBurner10.App.ViewModels;

public sealed record HomeTaskCard(
    string Tag,
    string Title,
    string Badge,
    string Subtitle,
    string IconGlyph);

public sealed partial class HomeViewModel : ObservableObject
{
    public ObservableCollection<HomeTaskCard> TaskCards { get; } =
    [
        new("data", "Data Disc", "ISO9660 / Joliet / UDF", "Burn files and folders to CD, DVD, or Blu-ray. Supports bootable discs, multi-session, and ISO creation.", "\uE8B7"),
        new("video", "Video DVD", "DVD-Video", "Burn a VIDEO_TS folder to a DVD-Video disc compatible with standalone players, or save as ISO.", "\uE714"),
        new("audio", "Audio Disc", "Red Book CD-DA", "Create standard Audio CDs from MP3, FLAC, WAV, OGG, and playlists with CD-Text, ReplayGain, and crossfading.", "\uE8D6"),
        new("iso", "Burn ISO Image", "ISO / BIN / NRG", "Write an existing ISO or disc image directly to optical media with verification.", "\uE958"),
        new("copy", "Copy Disc", "Direct or Image Grab", "Duplicate an optical disc to another disc or save it to your hard drive as an ISO image.", "\uE8C8"),
        new("mp4", "DVD to MP4", "Direct single-pass", "Convert a DVD directly from your optical drive (or ISO / VIDEO_TS) to MP4 in one pass with GPU acceleration, skipping menus and adverts.", "\uE8B2"),
        new("erase", "Erase Disc", "Quick / Full Erase", "Erase rewritable CD-RW, DVD±RW, or BD-RE discs quickly or securely.", "\uE75C")
    ];

    [ObservableProperty]
    private string _driveSummaryMessage = "Detecting optical drives...";

    public bool RememberLastProject
    {
        get => AppServices.Current.Settings.Current.StartLastUsedProjectType;
        set
        {
            if (AppServices.Current.Settings.Current.StartLastUsedProjectType == value) return;
            AppServices.Current.Settings.Current.StartLastUsedProjectType = value;
            AppServices.Current.Settings.Save();
            OnPropertyChanged();
        }
    }

    public void RefreshDriveSummary()
    {
        var services = AppServices.Current;
        if (!services.IsEngineInitialized)
        {
            DriveSummaryMessage = services.EngineStatusMessage;
            return;
        }

        if (services.Drives.Count == 0)
        {
            DriveSummaryMessage = "No optical drive detected. You can still create ISO images or prepare compilations.";
            return;
        }

        var drive = services.SelectedDrive ?? services.Drives[0];
        var disc = services.SelectedDriveDiscInfo;
        string mediaText = disc.HasDisc
            ? (disc.IsBlank
                ? $"{disc.DiscTypeDescription} • Blank ({FormatHelper.FormatBytes(disc.FreeSizeBytes)} free)"
                : $"{disc.DiscTypeDescription} • {FormatHelper.FormatBytes(disc.UsedSizeBytes)} used, {FormatHelper.FormatBytes(disc.FreeSizeBytes)} free")
            : "No disc inserted";

        DriveSummaryMessage = services.Drives.Count == 1
            ? $"{drive.DisplayName} — {mediaText}"
            : $"{services.Drives.Count} optical drives detected • Active: {drive.DisplayName} ({mediaText})";
    }
}
