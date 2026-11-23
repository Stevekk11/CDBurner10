using System.Diagnostics;
using System.Text;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Dvd;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace CDBurner10.App.Views;

public sealed record DvdSubtitleChoice(int? Ordinal, string DisplayName);

public sealed partial class DvdToMp4Page : Page
{
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _probeCts;
    private DvdVideoInfo? _info;
    private bool _isBusy;
    private bool _initialized;
    private bool _navigatedWithFile;
    private string _lastOutput = string.Empty;
    private string? _autoDestination;
    private bool _sourceCssProtected;

    public DvdToMp4Page()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += DvdToMp4Page_Loaded;
        Unloaded += DvdToMp4Page_Unloaded;
    }

    private bool IsOpticalMode => SourceModeRadio.SelectedIndex == 0;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_isBusy && e.Parameter is string source && !string.IsNullOrWhiteSpace(source))
        {
            _navigatedWithFile = true;
            SourceModeRadio.SelectedIndex = 1;
            SourcePathBox.Text = source;
        }
    }

    private async void DvdToMp4Page_Loaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.DrivesRefreshed += OnDrivesRefreshed;
        if (_isBusy) return;

        if (!_initialized)
        {
            _initialized = true;
            PopulateDrives();

            if (!_navigatedWithFile)
            {
                SourceModeRadio.SelectedIndex = AppServices.Current.Drives.Count > 0 ? 0 : 1;
            }
            UpdateSourceModeUi();
            await RefreshFfmpegStatusAsync();
        }
    }

    private void DvdToMp4Page_Unloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.DrivesRefreshed -= OnDrivesRefreshed;
        _probeCts?.Cancel();
        _probeCts = null;
    }

    private void OnDrivesRefreshed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(PopulateDrives);
    }

    private void PopulateDrives()
    {
        var drives = AppServices.Current.Drives;
        var prev = SourceDriveComboBox.SelectedItem as DriveModel ?? AppServices.Current.SelectedDrive;

        SourceDriveComboBox.ItemsSource = null;
        SourceDriveComboBox.ItemsSource = drives;
        SourceDriveComboBox.DisplayMemberPath = nameof(DriveModel.DisplayName);
        SourceDriveComboBox.SelectedItem = drives.FirstOrDefault(d => d.Letter == prev?.Letter) ?? drives.FirstOrDefault();

        if (IsOpticalMode)
            _ = UpdateSelectedDriveAsync();
    }

    private async Task RefreshFfmpegStatusAsync()
    {
        if (!DvdVideoConverter.IsAvailable)
            DvdVideoConverter.ResetToolCache();

        if (DvdVideoConverter.IsAvailable)
        {
            FfmpegStatusText.Text = "Checking GPU encoder...";
            string? hw = await Task.Run(DvdVideoConverter.DetectHardwareEncoder);
            string hwLabel = hw is not null ? $"GPU: {hw}" : "CPU: libx264";
            FfmpegStatusText.Text = $"ffmpeg ready • {hwLabel}";
            ToolTipService.SetToolTip(FfmpegStatusText, DvdVideoConverter.FfmpegPath);
            if (StatusInfoBar.Title == "ffmpeg Not Found")
                StatusInfoBar.IsOpen = false;
        }
        else
        {
            FfmpegStatusText.Text = "ffmpeg not found";
            ShowStatus(InfoBarSeverity.Warning, "ffmpeg Not Found", DvdVideoConverter.InstallHint);
        }
    }

    private void SourceModeRadio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateSourceModeUi();
    }

    private void UpdateSourceModeUi()
    {
        bool optical = IsOpticalMode;
        OpticalSourcePanel.Visibility = optical ? Visibility.Visible : Visibility.Collapsed;
        FileSourcePanel.Visibility = optical ? Visibility.Collapsed : Visibility.Visible;

        if (optical)
        {
            _ = UpdateSelectedDriveAsync();
        }
        else
        {
            UpdateDefaultDestination();
            string src = SourcePathBox.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(src))
                _ = ProbeSourceAsync(src);
            else
                ResetInfo();
        }
    }

    private void SourceDriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !IsOpticalMode || _isBusy) return;
        _ = UpdateSelectedDriveAsync();
    }

    private void RefreshDriveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _ = UpdateSelectedDriveAsync();
    }

    private async Task UpdateSelectedDriveAsync()
    {
        var drive = SourceDriveComboBox.SelectedItem as DriveModel;
        PopulateReadSpeeds(drive, OpticalDiscType.DvdRom);
        _sourceCssProtected = false;
        if (drive is null)
        {
            DriveStatusText.Text = "No optical drive detected. Switch to ISO / VIDEO_TS Folder mode to convert local files.";
            ResetInfo();
            return;
        }

        DriveStatusText.Text = "Checking inserted disc...";
        var info = await drive.RefreshDiscInfoAsync();
        if (!IsOpticalMode) return;
        PopulateReadSpeeds(drive, info.HasDisc ? info.DiscType : OpticalDiscType.DvdRom);

        if (!info.HasDisc)
        {
            DriveStatusText.Text = "No disc inserted in drive. Insert a DVD-Video disc and click Refresh.";
            ResetInfo();
            return;
        }

        _sourceCssProtected = info.IsCssProtected;
        string protection = info.IsCssProtected ? " • CSS protected" : string.Empty;
        string unfinalized = info.IsNextSessionAllowed && info.Sessions.Count == 0 && info.UsedSizeBytes > 0
            ? " • Unfinalized disc (raw MPEG-2 recovery supported)"
            : string.Empty;
        DriveStatusText.Text = $"{info.DiscTypeDescription} • {FormatHelper.FormatBytes(info.UsedSizeBytes)} used{protection}{unfinalized}";

        if (!string.IsNullOrWhiteSpace(drive.Letter))
        {
            string driveSource = DvdVideoConverter.GetDriveSourcePath(drive.Letter);
            UpdateDefaultDestination();
            await ProbeSourceAsync(driveSource);
        }
    }

    private void PopulateReadSpeeds(DriveModel? drive, OpticalDiscType mediaType)
    {
        int prevSpeed = (ReadSpeedComboBox.SelectedItem as DriveSpeedModel)?.SpeedKBps ?? 0;
        var speeds = DriveModel.BuildReadSpeedPresets(drive?.MaxReadSpeed ?? 0, mediaType);
        ReadSpeedComboBox.ItemsSource = speeds;
        ReadSpeedComboBox.DisplayMemberPath = nameof(DriveSpeedModel.DisplayText);
        ReadSpeedComboBox.SelectedItem = speeds.FirstOrDefault(s => s.SpeedKBps == prevSpeed) ?? speeds[0];
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (_isBusy) return;
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (_isBusy || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (item is StorageFolder folder && !string.IsNullOrWhiteSpace(folder.Path))
                {
                    SourceModeRadio.SelectedIndex = 1;
                    SourcePathBox.Text = folder.Path;
                    return;
                }
                if (item is StorageFile file &&
                    !string.IsNullOrWhiteSpace(file.Path) &&
                    file.Path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    SourceModeRadio.SelectedIndex = 1;
                    SourcePathBox.Text = file.Path;
                    return;
                }
            }
        }
        catch
        {
            // Ignore shell drag-drop errors.
        }
    }

    private async void TitleNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isBusy || !IsLoaded) return;
        string source = GetActiveSourcePath();
        if (string.IsNullOrWhiteSpace(source)) return;
        await ProbeSourceAsync(source);
    }

    private int GetSelectedTitleNumber()
    {
        double val = TitleNumberBox.Value;
        return double.IsNaN(val) ? 0 : Math.Clamp((int)Math.Round(val), 0, 99);
    }

    private string GetActiveSourcePath()
    {
        if (IsOpticalMode)
        {
            var drive = SourceDriveComboBox.SelectedItem as DriveModel;
            return !string.IsNullOrWhiteSpace(drive?.Letter)
                ? DvdVideoConverter.GetDriveSourcePath(drive.Letter)
                : string.Empty;
        }
        return SourcePathBox.Text?.Trim() ?? string.Empty;
    }

    private async void BrowseIsoButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSingleFileAsync([".iso"]);
        if (!string.IsNullOrWhiteSpace(path))
            SourcePathBox.Text = path;
    }

    private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
            SourcePathBox.Text = path;
    }

    private async void BrowseDestButton_Click(object sender, RoutedEventArgs e)
    {
        string suggested = BuildSuggestedOutputName(GetActiveSourcePath());
        var path = await FilePickerHelper.PickSaveFileAsync(suggested, new Dictionary<string, IList<string>>
        {
            ["MP4 Video"] = [".mp4"]
        });
        if (!string.IsNullOrWhiteSpace(path))
        {
            DestinationPathBox.Text = path;
            _autoDestination = null;
        }
    }

    private async void SourcePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsOpticalMode) return;
        UpdateDefaultDestination();

        if (_isBusy) return;

        string source = SourcePathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
        {
            ResetInfo();
            return;
        }

        await Task.Delay(350);
        if (SourcePathBox.Text?.Trim() != source) return;

        await ProbeSourceAsync(source);
    }

    private async Task<DvdVideoInfo?> ProbeSourceAsync(string source)
    {
        var cts = new CancellationTokenSource();
        var previous = _probeCts;
        _probeCts = cts;
        previous?.Cancel();

        try
        {
            int titleNumber = GetSelectedTitleNumber();
            DvdInfoText.Text = titleNumber > 0
                ? $"Reading DVD-Video title {titleNumber}..."
                : "Reading DVD-Video structure...";
            var info = await DvdVideoConverter.ProbeAsync(source, titleNumber, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_probeCts, cts)) return null;

            _info = info;
            PopulateTracks(info);
            string titleLabel = info.IsUnfinalizedRaw
                ? "Unfinalized DVD (raw MPEG-2 recovery)"
                : titleNumber > 0
                    ? $"Title {info.TitleNumber}"
                    : info.TotalTitles > 1
                        ? $"Main title (Title {info.TitleNumber} of {info.TotalTitles})"
                        : $"Main title (Title {info.TitleNumber})";
            string durPrefix = info.IsUnfinalizedRaw ? "~" : string.Empty;
            DvdInfoText.Text =
                $"{titleLabel}: {durPrefix}{FormatHelper.FormatDuration(info.Duration)} • {info.Width}×{info.Height} • " +
                $"{info.AudioTracks.Count} audio track(s) • {info.SubtitleTracks.Count} subtitle track(s)";
            if (StatusInfoBar.Title == "Cannot Read DVD")
                StatusInfoBar.IsOpen = false;
            return info;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_probeCts, cts))
            {
                _info = null;
                ResetTrackLists();
                DvdInfoText.Text = "Could not read the DVD-Video source.";
                ShowStatus(InfoBarSeverity.Error, "Cannot Read DVD", ex.Message);
            }
            return null;
        }
        finally
        {
            if (ReferenceEquals(_probeCts, cts))
                _probeCts = null;
        }
    }

    private void PopulateTracks(DvdVideoInfo info)
    {
        AudioComboBox.ItemsSource = info.AudioTracks;
        AudioComboBox.DisplayMemberPath = nameof(DvdAudioTrack.DisplayName);
        AudioComboBox.SelectedIndex = info.AudioTracks.Count > 0 ? 0 : -1;

        var subtitleChoices = new List<DvdSubtitleChoice> { new(null, "None (no subtitles)") };
        subtitleChoices.AddRange(info.SubtitleTracks.Select(s => new DvdSubtitleChoice(s.Ordinal, s.DisplayName)));
        SubtitleComboBox.ItemsSource = subtitleChoices;
        SubtitleComboBox.DisplayMemberPath = nameof(DvdSubtitleChoice.DisplayName);
        SubtitleComboBox.SelectedIndex = 0;
    }

    private void ResetInfo()
    {
        _info = null;
        ResetTrackLists();
        DvdInfoText.Text = "Insert a DVD-Video disc or choose an ISO / VIDEO_TS folder to detect movie length and tracks.";
    }

    private void ResetTrackLists()
    {
        AudioComboBox.ItemsSource = null;
        SubtitleComboBox.ItemsSource = null;
    }

    private void UpdateDefaultDestination()
    {
        string source = GetActiveSourcePath();
        if (string.IsNullOrWhiteSpace(source)) return;

        string suggested = BuildSuggestedOutputPath(source);
        if (string.IsNullOrWhiteSpace(DestinationPathBox.Text) ||
            DestinationPathBox.Text == _autoDestination)
        {
            DestinationPathBox.Text = suggested;
            _autoDestination = suggested;
        }
    }

    private static string BuildSuggestedOutputPath(string source)
    {
        try
        {
            if (DvdVideoConverter.TryExtractDriveLetter(source, out char letter))
            {
                string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (string.IsNullOrWhiteSpace(videos))
                    videos = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string label = DvdVideoConverter.GetSuggestedDriveOutputName(letter.ToString());
                return Path.Combine(videos, label + ".mp4");
            }

            string full = Path.GetFullPath(source);
            if (Directory.Exists(full))
            {
                string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(trimmed);
                if (name.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
                {
                    string? parent = Path.GetDirectoryName(trimmed);
                    name = string.IsNullOrWhiteSpace(parent) ? "DVDMovie" : Path.GetFileName(parent);
                }
                return Path.Combine(Path.GetDirectoryName(trimmed) ?? full, name + ".mp4");
            }

            return Path.ChangeExtension(full, ".mp4");
        }
        catch
        {
            return "DVDMovie.mp4";
        }
    }

    private static string BuildSuggestedOutputName(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "DVDMovie";
        try
        {
            string path = BuildSuggestedOutputPath(source);
            string name = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(name) ? "DVDMovie" : name;
        }
        catch
        {
            return "DVDMovie";
        }
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        await RefreshFfmpegStatusAsync();
        if (!DvdVideoConverter.IsAvailable)
        {
            ShowStatus(InfoBarSeverity.Error, "ffmpeg Not Found", DvdVideoConverter.InstallHint);
            return;
        }

        string source = GetActiveSourcePath();
        if (string.IsNullOrWhiteSpace(source))
        {
            ShowStatus(InfoBarSeverity.Warning, "Source Missing",
                IsOpticalMode
                    ? "Select an optical drive containing a DVD-Video disc first."
                    : "Select a DVD-Video ISO or VIDEO_TS folder first.");
            return;
        }

        DvdVideoInfo? info = _info;
        if (info is null)
        {
            info = await ProbeSourceAsync(source);
            if (info is null) return;
        }

        string destination = DestinationPathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(destination))
        {
            ShowStatus(InfoBarSeverity.Warning, "Output Missing", "Choose an output MP4 file path.");
            return;
        }

        if (!destination.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            destination += ".mp4";
            DestinationPathBox.Text = destination;
        }

        var selectedAudio = AudioComboBox.SelectedItem as DvdAudioTrack;
        var selectedSubtitle = SubtitleComboBox.SelectedItem as DvdSubtitleChoice;

        int crf = 21;
        string preset = "medium";
        if (QualityComboBox.SelectedItem is ComboBoxItem quality && quality.Tag is string tag)
        {
            var parts = tag.Split(';');
            if (parts.Length == 2 && int.TryParse(parts[0], out int parsedCrf))
            {
                crf = parsedCrf;
                preset = parts[1];
            }
        }

        var encoderPref = (EncoderComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "sw"
            ? DvdVideoEncoder.Software
            : DvdVideoEncoder.Auto;

        int readSpeed = IsOpticalMode
            ? (ReadSpeedComboBox.SelectedItem as DriveSpeedModel)?.SpeedKBps ?? 0
            : 0;

        var options = new DvdMp4Options
        {
            TitleNumber = GetSelectedTitleNumber(),
            AudioOrdinal = selectedAudio?.Ordinal ?? 0,
            SubtitleOrdinal = selectedSubtitle?.Ordinal,
            Crf = crf,
            Preset = preset,
            StereoDownmix = StereoDownmixCheckBox.IsChecked == true,
            Deinterlace = DeinterlaceCheckBox.IsChecked == true,
            Encoder = encoderPref,
            ReadSpeedKBps = readSpeed
        };

        string resolvedEncoder = await Task.Run(() => DvdVideoConverter.ResolveVideoEncoder(encoderPref));

        _isBusy = true;
        SetBusyUi(true);
        _lastOutput = destination;
        OpenMp4Button.Visibility = Visibility.Collapsed;
        ShowInFolderButton.Visibility = Visibility.Collapsed;
        ProgressCard.Visibility = Visibility.Visible;
        ConvertProgressBar.Value = 0;
        ProgressPercentText.Text = "0%";
        string sourceLabel = DvdVideoConverter.TryExtractDriveLetter(source, out char dl)
            ? $"DVD ({dl}:\\)"
            : Path.GetFileName(source);
        ProgressStatusText.Text = $"Converting {sourceLabel} ({resolvedEncoder})...";
        ProgressDetailText.Text = string.Empty;
        EventLogBox.Text = string.Empty;

        var log = new StringBuilder();
        void AppendLog(string msg)
        {
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            EventLogBox.Text = log.ToString();
        }

        AppendLog($"Source: {source} (Title {info.TitleNumber}, {FormatHelper.FormatDuration(info.Duration)}, {info.Width}×{info.Height})");
        AppendLog($"Output: {destination}");
        AppendLog($"Encoder: {resolvedEncoder}, CRF {crf} ({preset}), Audio: {selectedAudio?.DisplayName ?? "None"}, Subtitles: {selectedSubtitle?.DisplayName ?? "None"}, Deinterlace: {options.Deinterlace}");
        if (IsOpticalMode)
            AppendLog($"Read speed: {(readSpeed > 0 ? readSpeed + " KB/s" : "Maximum")}");
        if (IsOpticalMode && _sourceCssProtected)
            AppendLog("CSS copy protection detected. Decrypting the disc with libdvdcss...");
        if (info.IsUnfinalizedRaw)
            AppendLog($"Unfinalized DVD detected — streaming raw MPEG-2 sectors from LBA {info.UnfinalizedScan?.FirstMpegSector:N0} directly into ffmpeg...");
        else if (info.UseNavigatedWalker)
            AppendLog("Using libdvdnav navigation walker for disc reading...");

        int lastLoggedMilestone = -1;
        var convertWatch = Stopwatch.StartNew();
        _cts = new CancellationTokenSource();
        var progress = new Progress<DvdMp4Progress>(p =>
        {
            if (!string.IsNullOrWhiteSpace(p.LogLine))
            {
                AppendLog(p.LogLine);
            }

            if (p.Percent < 0) return;

            ConvertProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            ProgressPercentText.Text = $"{p.Percent}%";
            ProgressStatusText.Text = $"{p.Message} ({resolvedEncoder})";
            var details = new List<string>();
            if (p.Fps > 0) details.Add($"{p.Fps:0} fps");
            if (p.Speed > 0) details.Add($"{p.Speed:0.0}×");
            if (p.TimeRemaining > TimeSpan.Zero) details.Add($"ETA {FormatHelper.FormatDuration(p.TimeRemaining)}");
            ProgressDetailText.Text = string.Join(" • ", details);

            int milestone = p.Percent / 10 * 10;
            if (milestone > 0 && milestone > lastLoggedMilestone && milestone < 100)
            {
                lastLoggedMilestone = milestone;
                string extra = details.Count > 0 ? $" ({string.Join(", ", details)})" : string.Empty;
                AppendLog($"Progress: {p.Percent}%{extra}");
            }
        });

        try
        {
            await DvdVideoConverter.ConvertAsync(source, destination, info, options, progress, _cts.Token);
            ConvertProgressBar.Value = 100;
            ProgressPercentText.Text = "100%";
            ProgressStatusText.Text = "Conversion complete.";
            ProgressDetailText.Text = $"Saved {Path.GetFileName(destination)}";
            long outSize = File.Exists(destination) ? new FileInfo(destination).Length : 0;
            AppendLog(outSize > 0
                ? $"Completed successfully in {FormatHelper.FormatDuration(convertWatch.Elapsed)}. Saved '{Path.GetFileName(destination)}' ({FormatHelper.FormatBytes(outSize)})."
                : $"Completed successfully in {FormatHelper.FormatDuration(convertWatch.Elapsed)}. Saved '{Path.GetFileName(destination)}'.");
            OpenMp4Button.Visibility = Visibility.Visible;
            ShowInFolderButton.Visibility = Visibility.Visible;
            ShowStatus(InfoBarSeverity.Success, "MP4 Created", $"Saved '{Path.GetFileName(destination)}'.");
        }
        catch (OperationCanceledException)
        {
            ProgressStatusText.Text = "Conversion cancelled.";
            AppendLog($"Conversion cancelled by user after {FormatHelper.FormatDuration(convertWatch.Elapsed)}.");
            ShowStatus(InfoBarSeverity.Warning, "Cancelled", "DVD to MP4 conversion was cancelled.");
        }
        catch (Exception ex)
        {
            ProgressStatusText.Text = "Conversion failed.";
            AppendLog($"ERROR after {FormatHelper.FormatDuration(convertWatch.Elapsed)}: {ex.Message}");
            ShowStatus(InfoBarSeverity.Error, "Conversion Failed", ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _isBusy = false;
            SetBusyUi(false);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void OpenMp4Button_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOutput) || !File.Exists(_lastOutput)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_lastOutput) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Cannot Open MP4", ex.Message);
        }
    }

    private void ShowInFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOutput) || !File.Exists(_lastOutput)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Cannot Open Folder", ex.Message);
        }
    }

    private void SetBusyUi(bool busy)
    {
        ConvertButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        SourceModeRadio.IsEnabled = !busy;
        SourceDriveComboBox.IsEnabled = !busy;
        ReadSpeedComboBox.IsEnabled = !busy;
        RefreshDriveButton.IsEnabled = !busy;
        SourcePathBox.IsEnabled = !busy;
        DestinationPathBox.IsEnabled = !busy;
        BrowseIsoButton.IsEnabled = !busy;
        BrowseFolderButton.IsEnabled = !busy;
        BrowseDestButton.IsEnabled = !busy;
        AudioComboBox.IsEnabled = !busy;
        SubtitleComboBox.IsEnabled = !busy;
        QualityComboBox.IsEnabled = !busy;
        EncoderComboBox.IsEnabled = !busy;
        StereoDownmixCheckBox.IsEnabled = !busy;
        DeinterlaceCheckBox.IsEnabled = !busy;
        TitleNumberBox.IsEnabled = !busy;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
