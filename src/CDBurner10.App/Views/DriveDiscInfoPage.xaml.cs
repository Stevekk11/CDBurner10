using System.Text;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RocketDivision.StarBurnX;
using Windows.ApplicationModel.DataTransfer;

namespace CDBurner10.App.Views;

public sealed record DiscTrackRow(string SessionText, string TrackText, string TypeText, string SizeText);
public sealed record MediaCapabilityRow(string FormatName, string ReadText, string WriteText);

public sealed partial class DriveDiscInfoPage : Page
{
    private DiscInfoModel _lastDiscInfo = DiscInfoModel.Empty;

    private static readonly (string Name, STARBURN_DISC_TYPE Flag)[] MediaFormats =
    [
        ("CD-ROM", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_CD_ROM),
        ("CD-R", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_CD_R),
        ("CD-RW", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_CD_RW),
        ("DVD-ROM", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_ROM),
        ("DVD-R", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_R),
        ("DVD-RW", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_RW),
        ("DVD-R Dual Layer", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_R_DL),
        ("DVD+R", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_PLUS_R),
        ("DVD+RW", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_PLUS_RW),
        ("DVD+R Dual Layer", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_PLUS_R_DL),
        ("DVD-RAM", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_DVD_RAM),
        ("BD-ROM (Blu-Ray)", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_BD),
        ("BD-R (Blu-Ray)", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_BD_R),
        ("BD-R Dual Layer", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_BD_R_DL),
        ("BD-RE (Blu-Ray)", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_BD_RE),
        ("HD-DVD-ROM", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_HDDVD_ROM),
        ("HD-DVD-R", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_HDDVD_R),
        ("HD-DVD-RW", STARBURN_DISC_TYPE.STARBURN_DISC_TYPE_HDDVD_RW)
    ];

    public DriveDiscInfoPage()
    {
        InitializeComponent();
        Loaded += DriveDiscInfoPage_Loaded;
        Unloaded += DriveDiscInfoPage_Unloaded;
    }

    private void DriveDiscInfoPage_Loaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.DrivesRefreshed += OnDrivesRefreshed;
        PopulateDrives();
    }

    private void DriveDiscInfoPage_Unloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.DrivesRefreshed -= OnDrivesRefreshed;
    }

    private void OnDrivesRefreshed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(PopulateDrives);
    }

    private void PopulateDrives()
    {
        var drives = AppServices.Current.Drives;
        var prev = DriveComboBox.SelectedItem as DriveModel ?? AppServices.Current.SelectedDrive;
        DriveComboBox.ItemsSource = null;
        DriveComboBox.ItemsSource = drives;
        DriveComboBox.DisplayMemberPath = nameof(DriveModel.DisplayName);
        DriveComboBox.SelectedItem = drives.FirstOrDefault(d => d.Letter == prev?.Letter) ?? drives.FirstOrDefault();

        if (drives.Count == 0)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Informational;
            StatusInfoBar.Title = "No Optical Drive Detected";
            StatusInfoBar.Message = "Connect an optical drive to inspect hardware capabilities and inserted discs.";
            StatusInfoBar.IsOpen = true;
        }
        else
        {
            StatusInfoBar.IsOpen = false;
        }

        _ = RefreshAllInfoAsync();
    }

    private async Task RefreshAllInfoAsync()
    {
        var drive = DriveComboBox.SelectedItem as DriveModel;
        if (drive is null)
        {
            _lastDiscInfo = DiscInfoModel.Empty;
            ClearUi();
            return;
        }

        // Drive capabilities
        DriveVendorText.Text = string.IsNullOrWhiteSpace(drive.Vendor) ? "Unknown" : drive.Vendor;
        DriveProductText.Text = string.IsNullOrWhiteSpace(drive.Product) ? "Unknown" : drive.Product;
        DriveFirmwareText.Text = $"{(string.IsNullOrWhiteSpace(drive.Revision) ? "—" : drive.Revision)} ({drive.Letter}:\\)";
        DriveMaxReadText.Text = drive.MaxReadSpeed > 0 ? $"{drive.MaxReadSpeed:N0} KB/s" : "—";
        DriveMaxWriteText.Text = drive.MaxWriteSpeed > 0 ? $"{drive.MaxWriteSpeed:N0} KB/s" : "Read-only";

        var features = new List<string>();
        if (drive.SupportBufferUnderrunProtection) features.Add("BURN-Proof");
        if (drive.SupportTestWrite) features.Add("Test Write");
        DriveFeaturesText.Text = features.Count > 0 ? string.Join(", ", features) : "Standard";

        var matrix = MediaFormats.Select(m =>
        {
            bool canRead = (drive.SupportedMediaRead & (int)m.Flag) != 0;
            bool canWrite = (drive.SupportedMediaWrite & (int)m.Flag) != 0;
            return new MediaCapabilityRow(m.Name, canRead ? "✓ Supported" : "—", canWrite ? "✓ Supported" : "—");
        }).ToList();
        MediaMatrixListView.ItemsSource = matrix;

        // Disc info
        _lastDiscInfo = await drive.RefreshDiscInfoAsync();
        if (!_lastDiscInfo.HasDisc)
        {
            DiscMediaTypeText.Text = "No Disc Inserted";
            DiscLabelText.Text = "—";
            DiscStatusText.Text = "Empty Tray";
            DiscErasableText.Text = "—";
            CapacitySummaryText.Text = "Capacity: —";
            CapacityFreeText.Text = "Free: —";
            CapacityProgressBar.Value = 0;
            TracksListView.ItemsSource = null;
            NoTracksText.Visibility = Visibility.Visible;
            RegionInfoText.Text = "No disc inserted.";
            return;
        }

        DiscMediaTypeText.Text = _lastDiscInfo.DiscTypeDescription;
        DiscLabelText.Text = string.IsNullOrWhiteSpace(_lastDiscInfo.DiscLabel) ? "(Untitled)" : _lastDiscInfo.DiscLabel;
        DiscStatusText.Text = _lastDiscInfo.IsBlank ? "Blank" : (_lastDiscInfo.IsNextSessionAllowed ? "Open (Multi-session)" : "Finalized");
        DiscErasableText.Text = _lastDiscInfo.IsErasable ? "Yes" : "No";

        CapacitySummaryText.Text = $"Used: {FormatHelper.FormatBytes(_lastDiscInfo.UsedSizeBytes)} / Total: {FormatHelper.FormatBytes(_lastDiscInfo.TotalSizeBytes)}";
        CapacityFreeText.Text = $"Free: {FormatHelper.FormatBytes(_lastDiscInfo.FreeSizeBytes)}";
        CapacityProgressBar.Value = _lastDiscInfo.TotalSizeBytes > 0
            ? Math.Clamp(_lastDiscInfo.UsedSizeBytes * 100.0 / _lastDiscInfo.TotalSizeBytes, 0, 100)
            : 0;

        var rows = new List<DiscTrackRow>();
        foreach (var session in _lastDiscInfo.Sessions)
        {
            foreach (var track in session.Tracks)
            {
                string typeText = track.IsAudio ? $"Audio ({FormatHelper.FormatDuration(track.AudioDuration)})" : "Data";
                rows.Add(new DiscTrackRow(
                    $"Session {session.SessionNumber}",
                    $"Track {track.TrackNumber:D2}",
                    typeText,
                    $"{FormatHelper.FormatBytes(track.SizeBytes)} ({track.SizeBlocks:N0} blocks)"));
            }
        }

        TracksListView.ItemsSource = rows;
        NoTracksText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var allowedRegions = _lastDiscInfo.RegionInfo.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        RegionInfoText.Text = allowedRegions.Count > 0
            ? string.Join("\n", allowedRegions)
            : "Region-free or not applicable to this media.";
    }

    private void ClearUi()
    {
        DiscMediaTypeText.Text = "No Drive";
        DiscLabelText.Text = "—";
        DiscStatusText.Text = "—";
        DiscErasableText.Text = "—";
        CapacityProgressBar.Value = 0;
        TracksListView.ItemsSource = null;
        NoTracksText.Visibility = Visibility.Visible;
        DriveVendorText.Text = "—";
        DriveProductText.Text = "—";
        DriveFirmwareText.Text = "—";
        DriveMaxReadText.Text = "—";
        DriveMaxWriteText.Text = "—";
        DriveFeaturesText.Text = "—";
        MediaMatrixListView.ItemsSource = null;
        RegionInfoText.Text = "No optical drive connected.";
    }

    private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => _ = RefreshAllInfoAsync();
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = RefreshAllInfoAsync();

    private void CopyInfoButton_Click(object sender, RoutedEventArgs e)
    {
        var drive = DriveComboBox.SelectedItem as DriveModel;
        var sb = new StringBuilder();
        sb.AppendLine("=== CDBurner10 Drive & Disc Report ===");
        if (drive is not null)
        {
            sb.AppendLine($"Drive: {drive.DisplayName} (Firmware {drive.Revision})");
            sb.AppendLine($"Max Read: {drive.MaxReadSpeed} KB/s | Max Write: {drive.MaxWriteSpeed} KB/s");
        }
        sb.AppendLine($"Disc: {_lastDiscInfo.DiscTypeDescription} | Label: {_lastDiscInfo.DiscLabel}");
        sb.AppendLine($"Total: {FormatHelper.FormatBytes(_lastDiscInfo.TotalSizeBytes)} | Used: {FormatHelper.FormatBytes(_lastDiscInfo.UsedSizeBytes)} | Free: {FormatHelper.FormatBytes(_lastDiscInfo.FreeSizeBytes)}");
        foreach (var s in _lastDiscInfo.Sessions)
        {
            sb.AppendLine($"  Session {s.SessionNumber}: {s.Tracks.Count} track(s)");
            foreach (var t in s.Tracks)
                sb.AppendLine($"    Track {t.TrackNumber:D2}: {(t.IsAudio ? "Audio" : "Data")} - {FormatHelper.FormatBytes(t.SizeBytes)}");
        }

        var pkg = new DataPackage();
        pkg.SetText(sb.ToString());
        Clipboard.SetContent(pkg);

        StatusInfoBar.Severity = InfoBarSeverity.Success;
        StatusInfoBar.Title = "Copied";
        StatusInfoBar.Message = "Drive and disc information copied to clipboard.";
        StatusInfoBar.IsOpen = true;
    }
}
