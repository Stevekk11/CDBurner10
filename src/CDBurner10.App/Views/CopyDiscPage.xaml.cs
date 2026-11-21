using System.Diagnostics;
using System.Text;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views;

public sealed partial class CopyDiscPage : Page
{
    private readonly DiscGrabber _grabber = new();
    private readonly ImageBurner _imageBurner = new();
    private CancellationTokenSource? _cts;
    private bool _isBusy;

    public CopyDiscPage()
    {
        InitializeComponent();
        Loaded += CopyDiscPage_Loaded;
        Unloaded += CopyDiscPage_Unloaded;
    }

    private void CopyDiscPage_Loaded(object sender, RoutedEventArgs e)
    {
        var s = AppServices.Current.Settings.Current;
        IgnoreBadBlocksCheckBox.IsChecked = s.CopyIgnoreUnreadableData;
        DisableHwEcCheckBox.IsChecked = s.CopyDisableHardwareEC;
        ReadRetriesBox.Value = Math.Clamp(s.CopyReadRetries, 0, 20);
        FinalizeCheckBox.IsChecked = s.CopyFinaliseDisc;
        EjectCheckBox.IsChecked = s.CopyEjectDisc;
        if (!string.IsNullOrWhiteSpace(s.DataCopyImagePath))
            TargetImagePathBox.Text = s.DataCopyImagePath;

        AppServices.Current.DrivesRefreshed += OnDrivesRefreshed;
        PopulateDrives();
    }

    private void CopyDiscPage_Unloaded(object sender, RoutedEventArgs e)
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
        var drive = SourceDriveComboBox.SelectedItem as DriveModel;
        ReadSpeedComboBox.ItemsSource = drive?.SupportedReadSpeeds ?? [new DriveSpeedModel(0, 0)];
        ReadSpeedComboBox.DisplayMemberPath = nameof(DriveSpeedModel.DisplayText);
        ReadSpeedComboBox.SelectedIndex = 0;

        if (drive is null)
        {
            SourceDiscStatusText.Text = "No optical drive selected.";
            return;
        }

        SourceDiscStatusText.Text = "Checking inserted disc...";
        var info = await drive.RefreshDiscInfoAsync();
        if (!info.HasDisc)
        {
            SourceDiscStatusText.Text = "No disc inserted in source drive.";
        }
        else
        {
            int trackCount = info.Sessions.Sum(s => s.Tracks.Count);
            SourceDiscStatusText.Text = $"{info.DiscTypeDescription} • {FormatHelper.FormatBytes(info.UsedSizeBytes)} used • {info.Sessions.Count} session(s), {trackCount} track(s)";
        }
    }

    private void PopulateTargetSpeeds()
    {
        var drive = TargetDriveComboBox.SelectedItem as DriveModel;
        WriteSpeedComboBox.ItemsSource = drive?.SupportedWriteSpeeds ?? [new DriveSpeedModel(0, 0)];
        WriteSpeedComboBox.DisplayMemberPath = nameof(DriveSpeedModel.DisplayText);
        WriteSpeedComboBox.SelectedIndex = 0;
    }

    private void SourceDriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSourceInfo();
    private void TargetDriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => PopulateTargetSpeeds();
    private void RefreshSourceButton_Click(object sender, RoutedEventArgs e) => UpdateSourceInfo();

    private void TargetModeRadio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool toHardDisc = TargetModeRadio.SelectedIndex == 1;
        OpticalTargetPanel.Visibility = toHardDisc ? Visibility.Collapsed : Visibility.Visible;
        HardDiscTargetPanel.Visibility = toHardDisc ? Visibility.Visible : Visibility.Collapsed;
        StartCopyButtonText.Text = toHardDisc ? "Grab Disc to Image" : "Copy Disc";
    }

    private async void BrowseTargetImageButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSaveFileAsync("DiscImage", new Dictionary<string, IList<string>>
        {
            ["ISO Disc Image"] = [".iso"],
            ["Media Descriptor Image"] = [".mds"]
        });
        if (!string.IsNullOrWhiteSpace(path))
        {
            TargetImagePathBox.Text = path;
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
        DriveModel? targetDrive = null;
        string targetPath = string.Empty;

        if (toHardDisc)
        {
            targetPath = TargetImagePathBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                ShowStatus(InfoBarSeverity.Warning, "Output Path Required", "Choose a destination .iso or .mds file path.");
                return;
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

        var s = AppServices.Current.Settings.Current;
        s.CopyIgnoreUnreadableData = IgnoreBadBlocksCheckBox.IsChecked == true;
        s.CopyDisableHardwareEC = DisableHwEcCheckBox.IsChecked == true;
        s.CopyReadRetries = Math.Max(0, (int)ReadRetriesBox.Value);
        s.CopyFinaliseDisc = FinalizeCheckBox.IsChecked == true;
        s.CopyEjectDisc = EjectCheckBox.IsChecked == true;
        if (toHardDisc) s.DataCopyImagePath = targetPath;
        AppServices.Current.Settings.Save();

        _isBusy = true;
        SetBusyUi(true);
        ProgressCard.Visibility = Visibility.Visible;
        CopyProgressBar.Value = 0;

        var log = new StringBuilder();
        void AppendLog(string msg)
        {
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            EventLogBox.Text = log.ToString();
        }

        var sw = Stopwatch.StartNew();
        _cts = new CancellationTokenSource();
        string lastLogged = string.Empty;
        string? tempIso = null;

        var progress = new Progress<BurnProgress>(p =>
        {
            CopyProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            ProgressPhaseText.Text = string.IsNullOrWhiteSpace(p.Message) ? $"{p.Phase} ({p.Percent}%)" : p.Message;
            var rem = p.TimeRemaining > TimeSpan.Zero ? $" • Remaining: {FormatHelper.FormatDuration(p.TimeRemaining)}" : string.Empty;
            ProgressTimeText.Text = $"Elapsed: {FormatHelper.FormatDuration(sw.Elapsed)}{rem}";
            if (!string.IsNullOrWhiteSpace(p.Message) && p.Message != lastLogged && p.Phase is not (BurnPhase.Burning or BurnPhase.Grabbing))
            {
                lastLogged = p.Message;
                AppendLog(p.Message);
            }
        });

        try
        {
            int readSpeed = (ReadSpeedComboBox.SelectedItem as DriveSpeedModel)?.SpeedKBps ?? 0;

            if (toHardDisc)
            {
                AppendLog($"Grabbing disc from {sourceDrive.DisplayName} to '{targetPath}'...");
                var grabOpts = new GrabOptions
                {
                    Drive = sourceDrive,
                    TargetPath = targetPath,
                    ReadSpeedKBps = readSpeed,
                    IgnoreBadBlocks = IgnoreBadBlocksCheckBox.IsChecked == true,
                    DisableHardwareErrorCorrection = DisableHwEcCheckBox.IsChecked == true,
                    NumberOfReadRetries = Math.Max(0, (int)ReadRetriesBox.Value),
                    IsDvdVideo = DvdVideoGrabCheckBox.IsChecked == true
                };
                await _grabber.GrabDiscAsync(grabOpts, progress, _cts.Token);
                AppendLog("Disc image created successfully.");
                ShowStatus(InfoBarSeverity.Success, "Grab Completed", $"Saved disc image to '{Path.GetFileName(targetPath)}'.");
            }
            else
            {
                tempIso = AppServices.Current.Settings.GetTempFileName(".iso");
                AppendLog($"Phase 1/2: Reading source disc from {sourceDrive.DisplayName} to temporary image...");
                var grabOpts = new GrabOptions
                {
                    Drive = sourceDrive,
                    TargetPath = tempIso,
                    ReadSpeedKBps = readSpeed,
                    IgnoreBadBlocks = IgnoreBadBlocksCheckBox.IsChecked == true,
                    DisableHardwareErrorCorrection = DisableHwEcCheckBox.IsChecked == true,
                    NumberOfReadRetries = Math.Max(0, (int)ReadRetriesBox.Value)
                };
                await _grabber.GrabDiscAsync(grabOpts, progress, _cts.Token);

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
                AppendLog("Disc copy completed successfully.");
                ShowStatus(InfoBarSeverity.Success, "Copy Completed", "Optical disc copied successfully.");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Operation cancelled by user.");
            ShowStatus(InfoBarSeverity.Warning, "Cancelled", "Disc copy operation was cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
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
        TargetDriveComboBox.IsEnabled = !busy;
        TargetModeRadio.IsEnabled = !busy;
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
