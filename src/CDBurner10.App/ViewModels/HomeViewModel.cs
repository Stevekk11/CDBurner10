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
    public ObservableCollection<HomeTaskCard> TaskCards { get; } = [];

    [ObservableProperty]
    private string _driveSummaryMessage = Loc.T("Detecting optical drives...");

    public HomeViewModel()
    {
        PopulateTaskCards();
        LocalizationService.Current.LanguageChanged += (_, _) =>
        {
            PopulateTaskCards();
            RefreshDriveSummary();
        };
    }

    private void PopulateTaskCards()
    {
        TaskCards.Clear();
        TaskCards.Add(new("data", Loc.T("Data Disc"), "ISO9660 / Joliet / UDF", Loc.T("Burn files and folders to CD, DVD, or Blu-ray. Supports bootable discs, multi-session, and ISO creation."), "\uE8B7"));
        TaskCards.Add(new("video", Loc.T("Video DVD"), "DVD-Video", Loc.T("Burn a VIDEO_TS folder to a DVD-Video disc compatible with standalone players, or save as ISO."), "\uE714"));
        TaskCards.Add(new("audio", Loc.T("Audio Disc"), "Red Book CD-DA", Loc.T("Create standard Audio CDs from MP3, FLAC, WAV, OGG, and playlists with CD-Text, ReplayGain, and crossfading."), "\uE8D6"));
        TaskCards.Add(new("iso", Loc.T("Burn ISO Image"), "ISO / BIN / NRG", Loc.T("Write an existing ISO or disc image directly to optical media with verification."), "\uE958"));
        TaskCards.Add(new("copy", Loc.T("Copy Disc"), Loc.T("Direct or Image Grab"), Loc.T("Duplicate an optical disc to another disc or save it to your hard drive as an ISO image."), "\uE8C8"));
        TaskCards.Add(new("mp4", Loc.T("DVD to MP4"), Loc.T("Direct single-pass"), Loc.T("Convert a DVD directly from your optical drive (or ISO / VIDEO_TS) to MP4 in one pass with GPU acceleration, skipping menus and adverts."), "\uE8B2"));
        TaskCards.Add(new("erase", Loc.T("Erase Disc"), Loc.T("Quick / Full Erase"), Loc.T("Erase rewritable CD-RW, DVD±RW, or BD-RE discs quickly or securely."), "\uE75C"));
    }

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
            DriveSummaryMessage = Loc.T("No optical drive detected. You can still create ISO images or prepare compilations.");
            return;
        }

        var drive = services.SelectedDrive ?? services.Drives[0];
        var disc = services.SelectedDriveDiscInfo;
        string mediaText = disc.HasDisc
            ? (disc.IsBlank
                ? $"{disc.DiscTypeDescription} • Blank ({FormatHelper.FormatBytes(disc.FreeSizeBytes)} free)"
                : $"{disc.DiscTypeDescription} • {FormatHelper.FormatBytes(disc.UsedSizeBytes)} used, {FormatHelper.FormatBytes(disc.FreeSizeBytes)} free")
            : Loc.T("No disc inserted");

        DriveSummaryMessage = services.Drives.Count == 1
            ? $"{drive.DisplayName} — {mediaText}"
            : $"{services.Drives.Count} optical drives detected • Active: {drive.DisplayName} ({mediaText})";
    }
}
