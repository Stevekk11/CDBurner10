using System.Diagnostics;
using System.Text;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views;

public sealed record DvdTitleChoice(int TitleNumber, string DisplayName);

public sealed partial class CopyDiscPage : Page
{
    private readonly DiscGrabber _grabber = new();
    private readonly ImageBurner _imageBurner = new();
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private int _sourceScanVersion;
    private DiscInfoModel _sourceDiscInfo = DiscInfoModel.Empty;
    private bool _sourceIsDvdVideo;
    private bool _sourceIsUnfinalizedDvd;
    private UnfinalizedDvdScanResult? _unfinalizedScan;
    private string? _lastGrabbedVideoFile;

    public CopyDiscPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += CopyDiscPage_Loaded;
        Unloaded += CopyDiscPage_Unloaded;
    }

    private bool IsNavigatedMpgMode =>
        _sourceIsDvdVideo && DvdGrabModeComboBox?.SelectedIndex == 1;

    private bool WantsMpgOutput =>
        _sourceIsUnfinalizedDvd || IsNavigatedMpgMode;

    private void CopyDiscPage_Loaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.DrivesRefreshed += OnDrivesRefreshed;
        if (_isBusy) return;

        var s = AppServices.Current.Settings.Current;
        IgnoreBadBlocksCheckBox.IsChecked = s.CopyIgnoreUnreadableData;
        DisableHwEcCheckBox.IsChecked = s.CopyDisableHardwareEC;
        ReadRetriesBox.Value = Math.Clamp(s.CopyReadRetries, 0, 20);
        FinalizeCheckBox.IsChecked = s.CopyFinaliseDisc;
        EjectCheckBox.IsChecked = s.CopyEjectDisc;
        if (!string.IsNullOrWhiteSpace(s.DataCopyImagePath) && string.IsNullOrWhiteSpace(TargetImagePathBox.Text))
            TargetImagePathBox.Text = s.DataCopyImagePath;

        PopulateDrives();
    }

    private void CopyDiscPage_Unloaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.DrivesRefreshed -= OnDrivesRefreshed;
    }

    private void OnDrivesRefreshed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isBusy) PopulateDrives();
        });
    }

    private void PopulateDrives()
    {
        var drives = AppServices.Current.Drives;
        var prevSource = SourceDriveComboBox.SelectedItem as DriveModel ?? AppServices.Current.SelectedDrive;
        var prevTarget = TargetDriveComboBox.SelectedItem as DriveModel ?? AppServices.Current.SelectedDrive;

        SourceDriveComboBox.ItemsSource = null;
        SourceDriveComboBox.ItemsSource = drives;
        SourceDriveComboBox.DisplayMemberPath = nameof(DriveModel.DisplayName);
        SourceDriveComboBox.SelectedItem = drives.FirstOrDefault(d => d.Letter == prevSource?.Letter) ?? drives.FirstOrDefault();

        TargetDriveComboBox.ItemsSource = null;
        TargetDriveComboBox.ItemsSource = drives;
        TargetDriveComboBox.DisplayMemberPath = nameof(DriveModel.DisplayName);
        TargetDriveComboBox.SelectedItem = drives.FirstOrDefault(d => d.Letter == prevTarget?.Letter) ?? drives.FirstOrDefault();

        if (drives.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, "No Optical Drive Detected",
                "Connect an optical drive to copy or grab a disc.");
        }
        else if (StatusInfoBar.Title == "No Optical Drive Detected")
        {
            StatusInfoBar.IsOpen = false;
        }

        UpdateSourceInfo();
        PopulateTargetSpeeds();
    }

    private async void UpdateSourceInfo()
    {
        int scanVersion = ++_sourceScanVersion;
        var drive = SourceDriveComboBox.SelectedItem as DriveModel;
        PopulateReadSpeeds(drive);

        _sourceIsDvdVideo = false;
        _sourceIsUnfinalizedDvd = false;
        _unfinalizedScan = null;

        if (drive is null)
        {
            _sourceDiscInfo = DiscInfoModel.Empty;
            SourceDiscStatusText.Text = "No optical drive selected.";
            UpdateDvdAndTargetUi([]);
            return;
        }

        SourceDiscStatusText.Text = "Checking inserted disc...";
        var info = await drive.RefreshDiscInfoAsync();
        if (scanVersion != _sourceScanVersion) return;

        PopulateReadSpeeds(drive);
        _sourceDiscInfo = info;

        if (!info.HasDisc)
        {
            SourceDiscStatusText.Text = "No disc inserted in source drive.";
            if (StatusInfoBar.Title == "Unfinalized DVD Detected")
                StatusInfoBar.IsOpen = false;
            UpdateDvdAndTargetUi([]);
            return;
        }

        var probe = await Task.Run(() =>
        {
            bool isDvdVideo = DvdNavigationWalker.IsDvdVideoDrive(drive);
            IReadOnlyList<DvdTitleInfo> titles = [];
            UnfinalizedDvdScanResult? unfinalized = null;

            if (isDvdVideo && DvdNavigationWalker.IsAvailable && !string.IsNullOrWhiteSpace(drive.Letter))
            {
                try
                {
                    titles = DvdNavigationWalker.EnumerateTitles(drive.Letter);
                }
                catch
                {
                    titles = [];
                }
            }
            else if (!isDvdVideo && !info.IsBlank && !info.IsCd && !string.IsNullOrWhiteSpace(drive.Letter))
            {
                try
                {
                    UnfinalizedDvdReader.TryScanDrive(drive.Letter, out unfinalized);
                }
                catch
                {
                    unfinalized = null;
                }
            }

            return (isDvdVideo, titles, unfinalized);
        });

        if (scanVersion != _sourceScanVersion) return;

        _sourceIsDvdVideo = probe.isDvdVideo;
        _sourceIsUnfinalizedDvd = probe.unfinalized is not null;
        _unfinalizedScan = probe.unfinalized;

        int trackCount = info.Sessions.Sum(s => s.Tracks.Count);
        string protection = info.IsCssProtected ? " • CSS protected" : string.Empty;
        string dvdBadge = _sourceIsDvdVideo
            ? (probe.titles.Count > 0 ? $" • DVD-Video ({probe.titles.Count} title(s))" : " • DVD-Video")
            : _sourceIsUnfinalizedDvd
                ? $" • Unfinalized DVD (raw MPEG-2 from sector {probe.unfinalized!.FirstMpegSector:N0})"
                : string.Empty;

        SourceDiscStatusText.Text =
            $"{info.DiscTypeDescription} • {FormatHelper.FormatBytes(info.UsedSizeBytes)} used • " +
            $"{info.Sessions.Count} session(s), {trackCount} track(s){protection}{dvdBadge}";

        UpdateDvdAndTargetUi(probe.titles);

        if (_sourceIsUnfinalizedDvd)
        {
            ShowStatus(
                InfoBarSeverity.Informational,
                "Unfinalized DVD Detected",
                $"This disc was not finalized and has no ISO/UDF filesystem. Switch to Hard Disc to recover raw MPEG-2 video (.mpg) from sector {probe.unfinalized!.FirstMpegSector:N0}, or click 'Open in DVD to MP4...' to convert directly.");
        }
        else if (StatusInfoBar.Title == "Unfinalized DVD Detected")
        {
            StatusInfoBar.IsOpen = false;
        }
    }

    private void UpdateDvdAndTargetUi(IReadOnlyList<DvdTitleInfo> titles)
    {
        if (DvdReadModePanel is null || DvdTitleComboBox is null || TargetImagePathBox is null || HardDiscHintText is null)
            return;

        DvdReadModePanel.Visibility = _sourceIsDvdVideo ? Visibility.Visible : Visibility.Collapsed;

        var titleChoices = new List<DvdTitleChoice>
        {
            new(0, titles.Count > 0
                ? $"Auto — Longest (Title {DvdNavigationWalker.SelectMainTitle(titles).TitleNumber})"
                : "Auto (Longest Title)")
        };
        foreach (var t in titles)
        {
            titleChoices.Add(new DvdTitleChoice(
                t.TitleNumber,
                $"Title {t.TitleNumber} — {FormatHelper.FormatDuration(t.Duration)} ({t.Parts} ch)"));
        }

        int prevTitle = (DvdTitleComboBox.SelectedItem as DvdTitleChoice)?.TitleNumber ?? 0;
        DvdTitleComboBox.ItemsSource = titleChoices;
        DvdTitleComboBox.SelectedItem = titleChoices.FirstOrDefault(c => c.TitleNumber == prevTitle) ?? titleChoices[0];
        DvdTitleComboBox.Visibility = IsNavigatedMpgMode ? Visibility.Visible : Visibility.Collapsed;

        OpenInDvdToMp4Button.Visibility = (_sourceIsDvdVideo || _sourceIsUnfinalizedDvd) && !_isBusy
            ? Visibility.Visible
            : Visibility.Collapsed;

        SyncTargetFormatUi();
    }

    private void SyncTargetFormatUi()
    {
        if (TargetImagePathBox is null || HardDiscHintText is null || StartCopyButtonText is null || TargetModeRadio is null)
            return;

        bool wantsMpg = WantsMpgOutput;
        bool toHardDisc = TargetModeRadio.SelectedIndex == 1;

        TargetImagePathBox.Header = wantsMpg
            ? "Output Video Stream (.mpg)"
            : "Output Image File (.iso)";
        TargetImagePathBox.PlaceholderText = wantsMpg
            ? "Choose destination path for MPEG-2 video (.mpg)..."
            : "Choose destination path for disc image (.iso)...";

        string currentPath = TargetImagePathBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            string ext = Path.GetExtension(currentPath);
            if (wantsMpg && ext.Equals(".iso", StringComparison.OrdinalIgnoreCase))
            {
                TargetImagePathBox.Text = Path.ChangeExtension(currentPath, ".mpg");
            }
            else if (!wantsMpg && (ext.Equals(".mpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mpeg", StringComparison.OrdinalIgnoreCase)))
            {
                TargetImagePathBox.Text = Path.ChangeExtension(currentPath, ".iso");
            }
        }

        if (DvdTitleComboBox is not null)
            DvdTitleComboBox.Visibility = IsNavigatedMpgMode ? Visibility.Visible : Visibility.Collapsed;

        if (_sourceIsUnfinalizedDvd)
        {
            HardDiscHintText.Text =
                "Unfinalized DVD recovery mode: raw MPEG-2 video sectors will be extracted into a playable .mpg file (unfinalized discs lack a mountable ISO/UDF filesystem).";
        }
        else if (IsNavigatedMpgMode)
        {
            HardDiscHintText.Text =
                "Main Movie (.mpg) mode walks DVD-Video playback cells via libdvdnav, skipping structural/ARccOS dummy bad sectors to extract a clean MPEG-2 stream.";
        }
        else if (_sourceIsDvdVideo)
        {
            HardDiscHintText.Text =
                "Full Disc Image (.iso) performs a linear 1:1 sector copy (decrypted with libdvdcss if CSS-protected). Switch to Main Movie (.mpg) if the disc uses ARccOS structural bad sectors.";
        }
        else
        {
            HardDiscHintText.Text =
                "Creates a standard 1:1 sector-exact ISO image on your hard disc. CSS-protected DVDs are decrypted automatically via libdvdcss.";
        }

        StartCopyButtonText.Text = !toHardDisc
            ? "Copy Disc"
            : wantsMpg
                ? "Extract Video (.mpg)"
                : "Grab Disc to Image";
    }

    private void PopulateTargetSpeeds()
    {
        var drive = TargetDriveComboBox.SelectedItem as DriveModel;
        WriteSpeedComboBox.ItemsSource = drive?.SupportedWriteSpeeds ?? [new DriveSpeedModel(0, 0)];
        WriteSpeedComboBox.DisplayMemberPath = nameof(DriveSpeedModel.DisplayText);
        WriteSpeedComboBox.SelectedIndex = 0;
    }

    private void PopulateReadSpeeds(DriveModel? drive)
    {
        int prevSpeed = (ReadSpeedComboBox.SelectedItem as DriveSpeedModel)?.SpeedKBps ?? 0;
        var speeds = drive?.SupportedReadSpeeds is { Count: > 0 } ? drive.SupportedReadSpeeds : [new DriveSpeedModel(0, 0)];
        ReadSpeedComboBox.ItemsSource = speeds;
        ReadSpeedComboBox.DisplayMemberPath = nameof(DriveSpeedModel.DisplayText);
        ReadSpeedComboBox.SelectedItem = speeds.FirstOrDefault(s => s.SpeedKBps == prevSpeed) ?? speeds[0];
    }

    private void SourceDriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isBusy) UpdateSourceInfo();
    }

    private void TargetDriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => PopulateTargetSpeeds();

    private void RefreshSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBusy) UpdateSourceInfo();
    }

    private void DvdGrabModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncTargetFormatUi();
    }

    private void TargetModeRadio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpticalTargetPanel is null || HardDiscTargetPanel is null) return;

        bool toHardDisc = TargetModeRadio.SelectedIndex == 1;
        OpticalTargetPanel.Visibility = toHardDisc ? Visibility.Collapsed : Visibility.Visible;
        HardDiscTargetPanel.Visibility = toHardDisc ? Visibility.Visible : Visibility.Collapsed;
        SyncTargetFormatUi();
    }

    private async void BrowseTargetImageButton_Click(object sender, RoutedEventArgs e)
    {
        bool wantsMpg = WantsMpgOutput;
        string defaultName = wantsMpg ? "DiscVideo" : "DiscImage";
        var fileTypes = wantsMpg
            ? new Dictionary<string, IList<string>> { ["MPEG-2 Video Stream"] = [".mpg"] }
            : new Dictionary<string, IList<string>> { ["ISO Disc Image"] = [".iso"] };

        var path = await FilePickerHelper.PickSaveFileAsync(defaultName, fileTypes);
        if (!string.IsNullOrWhiteSpace(path))
        {
            TargetImagePathBox.Text = path;
        }
    }

    private void OpenInDvdToMp4Button_Click(object sender, RoutedEventArgs e)
    {
        var sourceDrive = SourceDriveComboBox.SelectedItem as DriveModel;
        if (!string.IsNullOrWhiteSpace(sourceDrive?.Letter))
        {
            AppServices.Current.NavigateTo("mp4", $"{sourceDrive.Letter}:");
        }
    }

    private async void StartCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var sourceDrive = SourceDriveComboBox.SelectedItem as DriveModel;
        if (sourceDrive is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No Source Drive", "Select a source optical drive first.");
            return;
        }

        bool toHardDisc = TargetModeRadio.SelectedIndex == 1;
        bool navigatedMpg = toHardDisc && IsNavigatedMpgMode;
        bool unfinalizedMpg = toHardDisc && _sourceIsUnfinalizedDvd;
        bool wantsMpg = navigatedMpg || unfinalizedMpg;

        if (!toHardDisc && _sourceIsUnfinalizedDvd)
        {
            ShowStatus(
                InfoBarSeverity.Warning,
                "Unfinalized DVD Source",
                "This DVD was not finalized in the recorder and lacks an ISO/UDF filesystem for direct disc-to-disc imaging. Switch Target Destination to Hard Disc (.mpg) or click 'Open in DVD to MP4...'.");
            return;
        }

        bool cssProtected = _sourceDiscInfo.IsCssProtected;
        if (!navigatedMpg && !unfinalizedMpg && cssProtected && !DvdCssReader.IsAvailable)
        {
            ShowStatus(InfoBarSeverity.Error, "libdvdcss Missing",
                $"CSS-protected DVD detected, but '{DvdCssReader.DllName}' was not found next to the application.");
            return;
        }

        if (navigatedMpg && !DvdNavigationWalker.IsAvailable)
        {
            ShowStatus(InfoBarSeverity.Error, "libdvdnav Missing",
                "Main Movie (.mpg) navigation requires the bundled libdvdnav library, which was not found.");
            return;
        }

        DriveModel? targetDrive = null;
        string targetPath = string.Empty;

        if (toHardDisc)
        {
            targetPath = TargetImagePathBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                ShowStatus(
                    InfoBarSeverity.Warning,
                    "Output Path Required",
                    wantsMpg ? "Choose a destination .mpg file path." : "Choose a destination .iso file path.");
                return;
            }

            string requiredExt = wantsMpg ? ".mpg" : ".iso";
            if (!Path.GetExtension(targetPath).Equals(requiredExt, StringComparison.OrdinalIgnoreCase))
            {
                targetPath = Path.ChangeExtension(targetPath, requiredExt);
                TargetImagePathBox.Text = targetPath;
            }
        }
        else
        {
            targetDrive = TargetDriveComboBox.SelectedItem as DriveModel;
            if (targetDrive is null)
            {
                ShowStatus(InfoBarSeverity.Warning, "No Target Drive", "Select a target optical writer.");
                return;
            }
        }

        bool sourceWasVideoCapable = _sourceIsDvdVideo || _sourceIsUnfinalizedDvd;

        var s = AppServices.Current.Settings.Current;
        s.CopyIgnoreUnreadableData = IgnoreBadBlocksCheckBox.IsChecked == true;
        s.CopyDisableHardwareEC = DisableHwEcCheckBox.IsChecked == true;
        s.CopyReadRetries = Math.Max(0, (int)ReadRetriesBox.Value);
        s.CopyFinaliseDisc = FinalizeCheckBox.IsChecked == true;
        s.CopyEjectDisc = EjectCheckBox.IsChecked == true;
        if (toHardDisc && !wantsMpg) s.DataCopyImagePath = targetPath;
        AppServices.Current.Settings.Save();

        _isBusy = true;
        SetBusyUi(true);
        _lastGrabbedVideoFile = null;
        ConvertToMp4Button.Visibility = Visibility.Collapsed;
        ProgressCard.Visibility = Visibility.Visible;
        CopyProgressBar.Value = 0;

        var log = new StringBuilder();
        void AppendLog(string msg)
        {
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            EventLogBox.Text = log.ToString();
            DiagnosticLogger.Info("CopyDisc", msg);
        }

        var sw = Stopwatch.StartNew();
        _cts = new CancellationTokenSource();
        string lastLogged = string.Empty;
        int lastMilestone = -1;
        BurnPhase lastPhase = BurnPhase.Preparing;
        string? tempIso = null;

        var progress = new Progress<BurnProgress>(p =>
        {
            CopyProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            ProgressPhaseText.Text = string.IsNullOrWhiteSpace(p.Message) ? $"{p.Phase} ({p.Percent}%)" : p.Message;
            var rem = p.TimeRemaining > TimeSpan.Zero ? $" • Remaining: {FormatHelper.FormatDuration(p.TimeRemaining)}" : string.Empty;
            var errs = p.ReadErrors > 0 ? $" • Read errors: {p.ReadErrors}" : string.Empty;
            ProgressTimeText.Text = $"Elapsed: {FormatHelper.FormatDuration(sw.Elapsed)}{rem}{errs}";
            if (!string.IsNullOrWhiteSpace(p.Message) && p.Message != lastLogged && p.Phase is not (BurnPhase.Burning or BurnPhase.Grabbing))
            {
                lastLogged = p.Message;
                AppendLog(p.Message);
            }

            if (p.Phase != lastPhase)
            {
                lastPhase = p.Phase;
                lastMilestone = -1;
            }
            if (p.Phase is BurnPhase.Burning or BurnPhase.Grabbing or BurnPhase.Verifying)
            {
                int milestone = p.Percent / 10 * 10;
                if (milestone > 0 && milestone > lastMilestone && milestone < 100)
                {
                    lastMilestone = milestone;
                    var extra = new List<string> { $"elapsed {FormatHelper.FormatDuration(sw.Elapsed)}" };
                    if (p.TimeRemaining > TimeSpan.Zero) extra.Add($"ETA {FormatHelper.FormatDuration(p.TimeRemaining)}");
                    if (p.Phase == BurnPhase.Burning && p.BufferPercent > 0) extra.Add($"buffer {p.BufferPercent}%");
                    if (p.TotalTracks > 0) extra.Add($"track {p.CurrentTrack}/{p.TotalTracks}");
                    if (p.ReadErrors > 0) extra.Add($"read errors {p.ReadErrors}");
                    AppendLog($"{p.Phase}: {p.Percent}% ({string.Join(", ", extra)})");
                }
            }
        });

        try
        {
            int readSpeed = (ReadSpeedComboBox.SelectedItem as DriveSpeedModel)?.SpeedKBps ?? 0;
            int selectedTitle = (DvdTitleComboBox.SelectedItem as DvdTitleChoice)?.TitleNumber ?? 0;

            AppendLog($"Source: {sourceDrive.DisplayName}");
            AppendLog(toHardDisc
                ? $"Target: file '{targetPath}'"
                : $"Target: {targetDrive!.DisplayName}");
            AppendLog($"Options: read speed {(readSpeed > 0 ? readSpeed + " KB/s" : "max")}, ignore bad blocks: {s.CopyIgnoreUnreadableData}, disable hardware EC: {s.CopyDisableHardwareEC}, read retries: {s.CopyReadRetries}");
            if (!toHardDisc)
                AppendLog($"Write options: write speed {((WriteSpeedComboBox.SelectedItem as DriveSpeedModel)?.SpeedKBps is int ws and > 0 ? ws + " KB/s" : "max")}, finalize: {s.CopyFinaliseDisc}, eject: {s.CopyEjectDisc}, copies: {Math.Max(1, (int)CopiesBox.Value)}");
            if (cssProtected && !navigatedMpg && !unfinalizedMpg)
                AppendLog("CSS copy protection detected. Decrypting sectors linearly with libdvdcss...");

            if (toHardDisc)
            {
                string modeDesc = unfinalizedMpg
                    ? $"unfinalized raw MPEG-2 recovery (sector {_unfinalizedScan?.FirstMpegSector ?? 0:N0})"
                    : navigatedMpg
                        ? $"libdvdnav title stream ({(selectedTitle > 0 ? $"Title {selectedTitle}" : "Auto longest title")})"
                        : "linear 1:1 sector image (.iso)";
                AppendLog($"Grabbing disc ({modeDesc}) from {sourceDrive.DisplayName} to '{targetPath}'...");

                var grabOpts = new GrabOptions
                {
                    Drive = sourceDrive,
                    TargetPath = targetPath,
                    ReadSpeedKBps = readSpeed,
                    IgnoreBadBlocks = s.CopyIgnoreUnreadableData,
                    DisableHardwareErrorCorrection = s.CopyDisableHardwareEC,
                    NumberOfReadRetries = s.CopyReadRetries,
                    IsDvdVideo = _sourceIsDvdVideo,
                    DecryptCss = true,
                    ReadMode = unfinalizedMpg
                        ? null
                        : navigatedMpg
                            ? DiscReadMode.Navigated
                            : DiscReadMode.Linear,
                    TargetTitle = selectedTitle,
                    AllowUnfinalizedRecovery = unfinalizedMpg,
                    ForceUnfinalizedRecovery = unfinalizedMpg
                };

                var grabResult = await _grabber.GrabDiscAsync(grabOpts, progress, _cts.Token);
                long imgSize = File.Exists(targetPath) ? new FileInfo(targetPath).Length : grabResult.BytesWritten;

                if (grabResult.BadSectorCount > 0)
                {
                    AppendLog(
                        $"WARNING: {grabResult.BadSectorCount} unreadable sector(s) were zero-filled across " +
                        $"{grabResult.BadRanges.Count} LBA range(s): {grabResult.FormatRangesSummary(12)}");
                }

                AppendLog(imgSize > 0
                    ? $"Saved '{Path.GetFileName(targetPath)}' ({FormatHelper.FormatBytes(imgSize)}) in {FormatHelper.FormatDuration(sw.Elapsed)}."
                    : $"Grab completed in {FormatHelper.FormatDuration(sw.Elapsed)}.");

                bool canConvertToMp4 =
                    (sourceWasVideoCapable || grabResult.WasUnfinalizedRecovery || grabResult.ModeUsed == DiscReadMode.Navigated) &&
                    File.Exists(targetPath);

                if (canConvertToMp4)
                {
                    _lastGrabbedVideoFile = targetPath;
                    ConvertToMp4Button.Visibility = Visibility.Visible;
                }

                if (grabResult.BadSectorCount > 0)
                {
                    ShowStatus(
                        InfoBarSeverity.Warning,
                        "Grab Completed with Read Errors",
                        $"Saved '{Path.GetFileName(targetPath)}' with {grabResult.BadSectorCount} unreadable sector(s) zero-filled. Check the Event Log for affected LBA ranges.");
                }
                else
                {
                    ShowStatus(
                        InfoBarSeverity.Success,
                        wantsMpg ? "Video Stream Extracted" : "Grab Completed",
                        $"Saved '{Path.GetFileName(targetPath)}'.");
                }
            }
            else
            {
                tempIso = AppServices.Current.Settings.GetTempFileName(".iso");
                AppendLog($"Phase 1/2: Reading source disc linearly from {sourceDrive.DisplayName} to temporary ISO...");
                var grabOpts = new GrabOptions
                {
                    Drive = sourceDrive,
                    TargetPath = tempIso,
                    ReadSpeedKBps = readSpeed,
                    IgnoreBadBlocks = s.CopyIgnoreUnreadableData,
                    DisableHardwareErrorCorrection = s.CopyDisableHardwareEC,
                    NumberOfReadRetries = s.CopyReadRetries,
                    IsDvdVideo = false,
                    DecryptCss = true,
                    ReadMode = DiscReadMode.Linear,
                    AllowUnfinalizedRecovery = false,
                    ForceUnfinalizedRecovery = false
                };
                var grabResult = await _grabber.GrabDiscAsync(grabOpts, progress, _cts.Token);
                if (grabResult.BadSectorCount > 0)
                {
                    AppendLog(
                        $"WARNING (Phase 1/2): {grabResult.BadSectorCount} unreadable sector(s) were zero-filled across " +
                        $"{grabResult.BadRanges.Count} LBA range(s): {grabResult.FormatRangesSummary(12)}");
                }
                AppendLog($"Phase 1/2 complete: source disc read in {FormatHelper.FormatDuration(sw.Elapsed)}.");

                if (sourceDrive.Letter.Equals(targetDrive!.Letter, StringComparison.OrdinalIgnoreCase))
                {
                    await sourceDrive.EjectAsync();
                    bool cont = await AppServices.Current.Dialogs.ConfirmAsync(
                        XamlRoot,
                        "Insert Blank Disc",
                        $"Source disc has been read and ejected from {sourceDrive.DisplayName}.\nPlease insert a blank writable disc and click Continue.",
                        "Continue",
                        "Cancel");
                    if (!cont)
                    {
                        throw new OperationCanceledException();
                    }
                    await targetDrive.LoadAsync();
                }

                AppendLog($"Phase 2/2: Burning image to {targetDrive.DisplayName}...");
                var burnOpts = new ImageBurnOptions
                {
                    Drive = targetDrive,
                    ImageFileName = tempIso,
                    WriteSpeedKBps = (WriteSpeedComboBox.SelectedItem as DriveSpeedModel)?.SpeedKBps ?? 0,
                    FinalizeDisc = FinalizeCheckBox.IsChecked == true,
                    EjectAfterBurn = EjectCheckBox.IsChecked == true,
                    Copies = Math.Max(1, (int)CopiesBox.Value)
                };
                await _imageBurner.BurnAsync(burnOpts, progress, _cts.Token);
                AppendLog($"Disc copy completed in {FormatHelper.FormatDuration(sw.Elapsed)}.");

                if (grabResult.BadSectorCount > 0)
                {
                    ShowStatus(
                        InfoBarSeverity.Warning,
                        "Copy Completed with Read Errors",
                        $"Disc copied, but {grabResult.BadSectorCount} unreadable source sector(s) were zero-filled. Check the Event Log for affected LBAs.");
                }
                else
                {
                    ShowStatus(InfoBarSeverity.Success, "Copy Completed", "Optical disc copied successfully.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog($"Operation cancelled by user after {FormatHelper.FormatDuration(sw.Elapsed)}.");
            ShowStatus(InfoBarSeverity.Warning, "Cancelled", "Disc copy operation was cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR after {FormatHelper.FormatDuration(sw.Elapsed)}: {ex.Message}");
            ShowStatus(InfoBarSeverity.Error, "Copy Failed", ex.Message);
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempIso))
                PathHelper.TryDeleteFile(tempIso);
            _cts?.Dispose();
            _cts = null;
            _isBusy = false;
            SetBusyUi(false);
        }
    }

    private void ConvertToMp4Button_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastGrabbedVideoFile) && File.Exists(_lastGrabbedVideoFile))
            AppServices.Current.NavigateTo("mp4", _lastGrabbedVideoFile);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _grabber.Cancel();
        _imageBurner.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void SetBusyUi(bool busy)
    {
        StartCopyButton.IsEnabled = !busy;
        SourceDriveComboBox.IsEnabled = !busy;
        ReadSpeedComboBox.IsEnabled = !busy;
        RefreshSourceButton.IsEnabled = !busy;

        TargetModeRadio.IsEnabled = !busy;
        TargetDriveComboBox.IsEnabled = !busy;
        WriteSpeedComboBox.IsEnabled = !busy;
        CopiesBox.IsEnabled = !busy;
        FinalizeCheckBox.IsEnabled = !busy;
        EjectCheckBox.IsEnabled = !busy;

        TargetImagePathBox.IsEnabled = !busy;
        BrowseTargetImageButton.IsEnabled = !busy;
        DvdGrabModeComboBox.IsEnabled = !busy;
        DvdTitleComboBox.IsEnabled = !busy;

        IgnoreBadBlocksCheckBox.IsEnabled = !busy;
        DisableHwEcCheckBox.IsEnabled = !busy;
        ReadRetriesBox.IsEnabled = !busy;

        OpenInDvdToMp4Button.IsEnabled = !busy;
        OpenInDvdToMp4Button.Visibility = (_sourceIsDvdVideo || _sourceIsUnfinalizedDvd) && !busy
            ? Visibility.Visible
            : Visibility.Collapsed;
        ConvertToMp4Button.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
