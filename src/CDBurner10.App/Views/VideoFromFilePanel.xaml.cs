using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Dvd;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace CDBurner10.App.Views;

/// <summary>Represents a source video file queued for DVD authoring.</summary>
public sealed class SourceVideoItem : INotifyPropertyChanged
{
    private DvdMediaInfo? _info;
    private string _detailsText;

    public SourceVideoItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        _detailsText = "Probing media info...";
    }

    public string FilePath { get; }
    public string FileName { get; }

    public DvdMediaInfo? Info
    {
        get => _info;
        set
        {
            _info = value;
            OnPropertyChanged();
        }
    }

    public string DetailsText
    {
        get => _detailsText;
        set
        {
            if (_detailsText == value) return;
            _detailsText = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>UI panel for authoring and burning MP4/MKV files to a Video DVD (or Video Blu-ray placeholder).</summary>
public sealed partial class VideoFromFilePanel : UserControl
{
    private const long Dvd5UsableBytes = 4350L * 1024 * 1024;
    private const long Dvd9UsableBytes = 7900L * 1024 * 1024;

    private readonly ObservableCollection<SourceVideoItem> _sources = [];
    private readonly DvdFromFileAuthorer _authorer = new();

    private bool _bluRay;
    private bool _isBusy;
    private bool _suppressDriveSync;
    private CancellationTokenSource? _cts;

    public VideoFromFilePanel()
    {
        InitializeComponent();
        SourceListView.ItemsSource = _sources;
        Loaded += VideoFromFilePanel_Loaded;
        Unloaded += VideoFromFilePanel_Unloaded;
    }

    public void Configure(bool bluRay)
    {
        _bluRay = bluRay;
        string disc = bluRay ? "Video Blu-ray" : "Video DVD";
        BackButtonText.Text = $"Back to {disc}";
        TitleText.Text = $"Burn from MP4/MKV - {disc}";
        SubtitleText.Text = bluRay
            ? "Convert MP4 or MKV files into a Blu-ray compatible disc (BDMV)."
            : "Convert MP4 or MKV files into a DVD-Video compatible disc.";
        BurnButtonText.Text = $"Burn {disc}";

        Fill(DiscTypeCombo, bluRay ? ["BD-R 25 GB", "BD-R DL 50 GB", "BD-RE 25 GB"] : ["DVD 4.7 GB", "DVD DL 8.5 GB"]);
        Fill(StandardCombo, bluRay ? ["1080p 24 / 25 / 30", "1080i", "720p"] : ["PAL (576i)", "NTSC (480i)"]);
        Fill(AspectCombo, ["Auto-detect", "16:9", "4:3"]);
        Fill(QualityCombo, ["Best fit for disc", "High", "Medium", "Low"]);
        Fill(AudioTrackCombo, ["First track", "All tracks"]);
        Fill(SubtitleCombo, ["None", "Embedded", "External file (.srt)"]);

        DriveCard.Visibility = bluRay ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = bluRay ? Visibility.Collapsed : Visibility.Visible;
        DiscUsageRow.Visibility = bluRay ? Visibility.Collapsed : Visibility.Visible;

        if (bluRay)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Informational;
            StatusInfoBar.Title = "Preview";
            StatusInfoBar.Message = "This feature is a UI placeholder. Encoding and burning are not implemented yet.";
            StatusInfoBar.IsClosable = false;
            StatusInfoBar.IsOpen = true;
        }
        else
        {
            UpdateFfmpegAvailabilityBanner();
            UpdateDiscUsageSummary();
        }
    }

    private void VideoFromFilePanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_bluRay) return;

        AppServices.Current.DrivesRefreshed += OnDrivesRefreshed;
        if (!_isBusy)
        {
            UpdateFfmpegAvailabilityBanner();
            PopulateDrives();
            UpdateDiscUsageSummary();
        }
    }

    private void VideoFromFilePanel_Unloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.DrivesRefreshed -= OnDrivesRefreshed;
    }

    private void UpdateFfmpegAvailabilityBanner()
    {
        if (DvdVideoConverter.IsAvailable)
        {
            if (StatusInfoBar.Title is "Preview" or "FFmpeg Not Found")
            {
                StatusInfoBar.IsOpen = false;
            }
            StatusInfoBar.IsClosable = true;
        }
        else
        {
            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Title = "FFmpeg Not Found";
            StatusInfoBar.Message = DvdVideoConverter.InstallHint;
            StatusInfoBar.IsClosable = false;
            StatusInfoBar.IsOpen = true;
        }
    }

    private void OnDrivesRefreshed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(PopulateDrives);
    }

    private void PopulateDrives()
    {
        _suppressDriveSync = true;
        try
        {
            var drives = AppServices.Current.Drives;
            var prev = DriveComboBox.SelectedItem as DriveModel ?? AppServices.Current.SelectedDrive;
            DriveComboBox.ItemsSource = null;
            DriveComboBox.ItemsSource = drives;
            DriveComboBox.DisplayMemberPath = nameof(DriveModel.DisplayName);
            DriveComboBox.SelectedItem = drives.FirstOrDefault(d => d.Letter == prev?.Letter) ?? drives.FirstOrDefault();
            PopulateSpeeds();
        }
        finally
        {
            _suppressDriveSync = false;
        }
    }

    private void PopulateSpeeds()
    {
        var drive = DriveComboBox.SelectedItem as DriveModel;
        var speeds = drive?.SupportedWriteSpeeds ?? [new DriveSpeedModel(0, 0)];
        SpeedComboBox.ItemsSource = speeds;
        SpeedComboBox.DisplayMemberPath = nameof(DriveSpeedModel.DisplayText);
        SpeedComboBox.SelectedIndex = 0;
    }

    private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PopulateSpeeds();
        if (!_suppressDriveSync && DriveComboBox.SelectedItem is DriveModel drive)
        {
            AppServices.Current.SelectedDrive = drive;
        }
    }

    private static void Fill(ComboBox box, string[] items)
    {
        box.Items.Clear();
        foreach (var i in items) box.Items.Add(i);
        box.SelectedIndex = 0;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
        => AppServices.Current.NavigateTo(_bluRay ? "bluray" : "video");

    private async void AddVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var paths = await FilePickerHelper.PickFilesAsync([".mp4", ".mkv"], multiple: true, PickerLocationId.VideosLibrary);
        if (paths.Count == 0) return;

        var addedItems = new List<SourceVideoItem>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var item = new SourceVideoItem(path);
            if (_bluRay)
            {
                item.DetailsText = path;
            }
            _sources.Add(item);
            addedItems.Add(item);
        }

        if (addedItems.Count == 0) return;

        if (SourceListView.SelectedIndex < 0)
        {
            SourceListView.SelectedIndex = _sources.Count - 1;
        }

        UpdateSourceListUi();
        UpdateDiscUsageSummary();

        if (!_bluRay)
        {
            foreach (var item in addedItems)
            {
                _ = ProbeItemAsync(item);
            }
        }
    }

    private async Task ProbeItemAsync(SourceVideoItem item)
    {
        if (!DvdVideoConverter.IsAvailable)
        {
            item.DetailsText = "FFmpeg / ffprobe not available";
            return;
        }

        try
        {
            var info = await DvdMediaProber.ProbeAsync(item.FilePath);
            item.Info = info;
            var durationStr = FormatHelper.FormatDuration(info.Duration);
            var dims = info.Width > 0 && info.Height > 0 ? $"{info.Width}×{info.Height}" : "Unknown resolution";
            item.DetailsText = !string.IsNullOrWhiteSpace(info.Summary)
                ? info.Summary
                : $"{durationStr} • {dims} • {info.AudioStreamCount} audio";
        }
        catch (Exception ex)
        {
            item.DetailsText = $"Could not probe: {ex.Message}";
        }

        UpdateDiscUsageSummary();
    }

    private void RemoveVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        int idx = SourceListView.SelectedIndex;
        if (idx < 0 || idx >= _sources.Count) return;

        _sources.RemoveAt(idx);
        if (_sources.Count > 0)
        {
            SourceListView.SelectedIndex = Math.Min(idx, _sources.Count - 1);
        }

        UpdateSourceListUi();
        UpdateDiscUsageSummary();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        int idx = SourceListView.SelectedIndex;
        if (idx <= 0 || idx >= _sources.Count) return;

        _sources.Move(idx, idx - 1);
        SourceListView.SelectedIndex = idx - 1;
        UpdateSourceButtons();
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        int idx = SourceListView.SelectedIndex;
        if (idx < 0 || idx >= _sources.Count - 1) return;

        _sources.Move(idx, idx + 1);
        SourceListView.SelectedIndex = idx + 1;
        UpdateSourceButtons();
    }

    private void SourceListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSourceButtons();
    }

    private void UpdateSourceListUi()
    {
        bool hasItems = _sources.Count > 0;
        EmptySourceHintText.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        SourceListView.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        UpdateSourceButtons();
    }

    private void UpdateSourceButtons()
    {
        int idx = SourceListView.SelectedIndex;
        bool hasSel = !_isBusy && idx >= 0 && idx < _sources.Count;
        RemoveVideoButton.IsEnabled = hasSel;
        MoveUpButton.IsEnabled = hasSel && idx > 0;
        MoveDownButton.IsEnabled = hasSel && idx < _sources.Count - 1;
    }

    private void OptionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_bluRay)
        {
            UpdateDiscUsageSummary();
        }
    }

    private void SubtitleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool isExternal = GetSelectedSubtitleMode() == DvdSubtitleMode.ExternalSrt;
        ExternalSrtRow.Visibility = isExternal ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BrowseSrt_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var path = await FilePickerHelper.PickSingleFileAsync([".srt"], PickerLocationId.VideosLibrary);
        if (!string.IsNullOrWhiteSpace(path))
        {
            ExternalSrtPathBox.Text = path;
        }
    }

    private void UpdateDiscUsageSummary()
    {
        if (_bluRay) return;

        var targetDisc = GetSelectedTargetDisc();
        string capacityLabel = targetDisc == DvdTargetDisc.Dvd9_8_5GB ? "8.5 GB" : "4.7 GB";
        long maxBytes = targetDisc == DvdTargetDisc.Dvd9_8_5GB ? Dvd9UsableBytes : Dvd5UsableBytes;

        if (_sources.Count == 0)
        {
            DiscUsageText.Text = $"Estimated Disc Usage: 00:00 • ~0.00 GB / {capacityLabel}";
            return;
        }

        TimeSpan totalDuration = TimeSpan.Zero;
        int probedCount = 0;
        int maxAudioStreams = 1;

        foreach (var src in _sources)
        {
            if (src.Info is not null)
            {
                totalDuration += src.Info.Duration;
                probedCount++;
                if (src.Info.AudioStreamCount > maxAudioStreams)
                {
                    maxAudioStreams = src.Info.AudioStreamCount;
                }
            }
        }

        if (probedCount == 0 || totalDuration <= TimeSpan.Zero)
        {
            DiscUsageText.Text = $"Estimated Disc Usage: Probing {_sources.Count} video(s)... / {capacityLabel}";
            return;
        }

        var quality = GetSelectedQuality();
        var audioSel = GetSelectedAudioSelection();
        int audioStreams = audioSel == DvdAudioSelection.AllTracks ? Math.Clamp(maxAudioStreams, 1, 8) : 1;
        int videoBitrateKbps = DvdMediaProber.CalculateVideoBitrateKbps(totalDuration, targetDisc, quality, audioStreams);
        long estimatedBytes = DvdMediaProber.EstimateDiscBytes(totalDuration, videoBitrateKbps, audioStreams * 192);
        double estimatedGb = estimatedBytes / 1_000_000_000.0;

        string overCapacity = estimatedBytes > maxBytes ? " (exceeds disc capacity)" : string.Empty;
        DiscUsageText.Text =
            $"Estimated Disc Usage: {FormatHelper.FormatDuration(totalDuration)} • ~{estimatedGb:0.00} GB / {capacityLabel} ({videoBitrateKbps} kbps){overCapacity}";
    }

    private DvdTargetDisc GetSelectedTargetDisc()
        => DiscTypeCombo.SelectedIndex == 1 ? DvdTargetDisc.Dvd9_8_5GB : DvdTargetDisc.Dvd5_4_7GB;

    private DvdVideoStandard GetSelectedStandard()
        => StandardCombo.SelectedIndex == 1 ? DvdVideoStandard.Ntsc : DvdVideoStandard.Pal;

    private DvdAspectRatio GetSelectedAspectRatio() => AspectCombo.SelectedIndex switch
    {
        1 => DvdAspectRatio.Widescreen16x9,
        2 => DvdAspectRatio.Standard4x3,
        _ => DvdAspectRatio.Auto
    };

    private DvdQualityPreset GetSelectedQuality() => QualityCombo.SelectedIndex switch
    {
        1 => DvdQualityPreset.High,
        2 => DvdQualityPreset.Medium,
        3 => DvdQualityPreset.Low,
        _ => DvdQualityPreset.BestFit
    };

    private DvdAudioSelection GetSelectedAudioSelection()
        => AudioTrackCombo.SelectedIndex == 1 ? DvdAudioSelection.AllTracks : DvdAudioSelection.FirstTrack;

    private DvdSubtitleMode GetSelectedSubtitleMode() => SubtitleCombo.SelectedIndex switch
    {
        1 => DvdSubtitleMode.Embedded,
        2 => DvdSubtitleMode.ExternalSrt,
        _ => DvdSubtitleMode.None
    };

    private DvdAuthoringOptions BuildAuthoringOptions(DriveModel? drive)
    {
        var subtitleMode = GetSelectedSubtitleMode();
        string? externalSrt = subtitleMode == DvdSubtitleMode.ExternalSrt && !string.IsNullOrWhiteSpace(ExternalSrtPathBox.Text)
            ? ExternalSrtPathBox.Text.Trim()
            : null;

        var sources = _sources
            .Select(s => new DvdSourceVideo(s.FilePath, externalSrt))
            .ToList();

        int speedKBps = (SpeedComboBox.SelectedItem as DriveSpeedModel)?.SpeedKBps ?? 0;
        string volumeName = string.IsNullOrWhiteSpace(DiscLabelBox.Text) ? "MY_VIDEO" : DiscLabelBox.Text.Trim();

        return new DvdAuthoringOptions
        {
            Sources = sources,
            Drive = drive,
            WriteSpeedKBps = speedKBps,
            TargetDisc = GetSelectedTargetDisc(),
            Standard = GetSelectedStandard(),
            AspectRatio = GetSelectedAspectRatio(),
            Quality = GetSelectedQuality(),
            AudioSelection = GetSelectedAudioSelection(),
            SubtitleMode = subtitleMode,
            VolumeName = volumeName,
            CreateMenu = MenuToggle.IsOn,
            VerifyAfterBurn = VerifyToggle.IsOn,
            EjectAfterBurn = AppServices.Current.Settings.Current.DataBurnEject
        };
    }

    private bool ValidateBeforeAuthoring()
    {
        if (!DvdVideoConverter.IsAvailable)
        {
            UpdateFfmpegAvailabilityBanner();
            return false;
        }

        if (_sources.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "No Source Videos", "Add at least one .mp4 or .mkv file before starting.");
            return false;
        }

        if (GetSelectedSubtitleMode() == DvdSubtitleMode.ExternalSrt)
        {
            var srtPath = ExternalSrtPathBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(srtPath) || !File.Exists(srtPath))
            {
                ShowStatus(InfoBarSeverity.Warning, "Missing Subtitle File", "Select a valid external .srt file, or change Subtitles to None/Embedded.");
                return false;
            }
        }

        return true;
    }

    private async void Burn_Click(object sender, RoutedEventArgs e)
    {
        if (_bluRay)
        {
            ShowComingSoon();
            return;
        }

        if (_isBusy || !ValidateBeforeAuthoring()) return;

        var drive = DriveComboBox.SelectedItem as DriveModel;
        if (drive is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No Drive Selected", "Select an optical writer before burning, or use 'Save as ISO...'.");
            return;
        }

        var options = BuildAuthoringOptions(drive);
        await ExecuteOperationAsync(
            $"Authoring and burning Video DVD to {drive.DisplayName}...",
            (progress, ct) => _authorer.BurnAsync(options, progress, ct));
    }

    private async void SaveIso_Click(object sender, RoutedEventArgs e)
    {
        if (_bluRay)
        {
            ShowComingSoon();
            return;
        }

        if (_isBusy || !ValidateBeforeAuthoring()) return;

        var suggested = string.IsNullOrWhiteSpace(DiscLabelBox.Text) ? "MY_VIDEO" : DiscLabelBox.Text.Trim();
        var isoPath = await FilePickerHelper.PickSaveFileAsync(suggested, new Dictionary<string, IList<string>>
        {
            ["ISO Disc Image"] = [".iso"]
        });
        if (string.IsNullOrWhiteSpace(isoPath)) return;

        var options = BuildAuthoringOptions(null);
        await ExecuteOperationAsync(
            $"Authoring Video DVD ISO to {Path.GetFileName(isoPath)}...",
            (progress, ct) => _authorer.SaveIsoAsync(options, isoPath, progress, ct));
    }

    private async Task ExecuteOperationAsync(
        string startMessage,
        Func<IProgress<BurnProgress>, CancellationToken, Task> work)
    {
        _isBusy = true;
        SetBusyUi(true);
        ProgressCard.Visibility = Visibility.Visible;
        ProgressBar.Value = 0;
        ProgressPhaseText.Text = startMessage;
        ProgressTimeText.Text = "Elapsed: 00:00";

        var log = new StringBuilder();
        void AppendLog(string msg)
        {
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            EventLogBox.Text = log.ToString();
            CDBurner10.Core.Utilities.DiagnosticLogger.Info("VideoDvdFromFile", msg);
        }

        AppendLog(startMessage);
        var sw = Stopwatch.StartNew();
        _cts = new CancellationTokenSource();
        string lastLoggedMsg = string.Empty;

        var progress = new Progress<BurnProgress>(p =>
        {
            ProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            ProgressPhaseText.Text = string.IsNullOrWhiteSpace(p.Message) ? $"{p.Phase} ({p.Percent}%)" : p.Message;
            var remText = p.TimeRemaining > TimeSpan.Zero ? $" • Remaining: {FormatHelper.FormatDuration(p.TimeRemaining)}" : string.Empty;
            ProgressTimeText.Text = $"Elapsed: {FormatHelper.FormatDuration(sw.Elapsed)}{remText}";

            if (!string.IsNullOrWhiteSpace(p.Message) && p.Message != lastLoggedMsg && p.Phase != BurnPhase.Burning)
            {
                lastLoggedMsg = p.Message;
                AppendLog(p.Message);
            }
        });

        try
        {
            await work(progress, _cts.Token);
            sw.Stop();
            ProgressBar.Value = 100;
            ProgressPhaseText.Text = "Completed";
            ProgressTimeText.Text = $"Elapsed: {FormatHelper.FormatDuration(sw.Elapsed)}";
            AppendLog("Operation completed successfully.");
            ShowStatus(InfoBarSeverity.Success, "Completed", "Video DVD authoring operation finished successfully.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Operation cancelled by user.");
            ShowStatus(InfoBarSeverity.Warning, "Cancelled", "Video DVD authoring operation was cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            ShowStatus(InfoBarSeverity.Error, "Operation Failed", ex.Message);
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
        _authorer.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void SetBusyUi(bool busy)
    {
        BackButton.IsEnabled = !busy;
        AddVideoButton.IsEnabled = !busy;
        SourceListView.IsEnabled = !busy;
        DiscTypeCombo.IsEnabled = !busy;
        StandardCombo.IsEnabled = !busy;
        AspectCombo.IsEnabled = !busy;
        QualityCombo.IsEnabled = !busy;
        AudioTrackCombo.IsEnabled = !busy;
        SubtitleCombo.IsEnabled = !busy;
        ExternalSrtPathBox.IsEnabled = !busy;
        BrowseSrtButton.IsEnabled = !busy;
        DiscLabelBox.IsEnabled = !busy;
        MenuToggle.IsEnabled = !busy;
        VerifyToggle.IsEnabled = !busy;
        DriveComboBox.IsEnabled = !busy;
        SpeedComboBox.IsEnabled = !busy;
        BurnButton.IsEnabled = !busy;
        SaveIsoButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        UpdateSourceButtons();
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsClosable = true;
        StatusInfoBar.IsOpen = true;
    }

    private void ShowComingSoon()
    {
        StatusInfoBar.Severity = InfoBarSeverity.Warning;
        StatusInfoBar.Title = "Coming soon";
        StatusInfoBar.Message = _bluRay
            ? "Burning Video Blu-ray from MP4/MKV is not implemented yet."
            : "Burning Video DVD from MP4/MKV is not implemented yet.";
        StatusInfoBar.IsOpen = true;
    }
}
