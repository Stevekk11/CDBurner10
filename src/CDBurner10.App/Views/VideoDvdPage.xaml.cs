using System.Diagnostics;
using System.Text;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CDBurner10.App.Views;

public sealed partial class VideoDvdPage : Page
{
    private readonly DvdVideoBurner _burner = new();
    private CancellationTokenSource? _cts;
    private bool _isBusy;

    public VideoDvdPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += VideoDvdPage_Loaded;
        Unloaded += VideoDvdPage_Unloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_isBusy && e.Parameter is string folder && !string.IsNullOrWhiteSpace(folder))
        {
            VideoTsPathBox.Text = folder;
        }
    }

    private void VideoDvdPage_Loaded(object sender, RoutedEventArgs e)
    {
        AppServices.Current.DrivesRefreshed += OnDrivesRefreshed;
        if (_isBusy) return;

        var settings = AppServices.Current.Settings.Current;
        DoubleLayerCheckBox.IsChecked = settings.DoubleLayerCompatible;
        PadDvdCheckBox.IsChecked = settings.PadDvdSize;
        PatchIfoBupCheckBox.IsChecked = settings.ModifyIfoBupFiles;
        EjectCheckBox.IsChecked = settings.DataBurnEject;
        if (string.IsNullOrWhiteSpace(VideoTsPathBox.Text) && !string.IsNullOrWhiteSpace(settings.DataVideoLayoutPath))
        {
            VideoTsPathBox.Text = settings.DataVideoLayoutPath;
        }

        PopulateDrives();
        ValidateFolder();
    }

    private void VideoDvdPage_Unloaded(object sender, RoutedEventArgs e)
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
            ShowStatus(InfoBarSeverity.Informational, "No Optical Drive Detected",
                "Connect an optical writer to burn directly to DVD, or use 'Save as ISO...' to create a DVD-Video image file.");
        }
        else if (StatusInfoBar.Title == "No Optical Drive Detected")
        {
            StatusInfoBar.IsOpen = false;
        }

        PopulateSpeeds();
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
    }

    private async void BrowseVideoTsButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await FilePickerHelper.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            VideoTsPathBox.Text = folder;
        }
    }

    private async void BrowseAudioTsButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await FilePickerHelper.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            AudioTsPathBox.Text = folder;
        }
    }

    private async void BrowseExtraFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await FilePickerHelper.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            ExtraFilesPathBox.Text = folder;
        }
    }

    private void VideoTsPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateFolder();
    }

    private bool ValidateFolder()
    {
        var path = VideoTsPathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            ValidationInfoBar.IsOpen = false;
            return false;
        }

        var (isValid, message, totalBytes) = DvdVideoBurner.ValidateVideoTsFolder(path);
        ValidationInfoBar.Severity = isValid ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ValidationInfoBar.Title = isValid ? $"Valid VIDEO_TS ({FormatHelper.FormatBytes(totalBytes)})" : "Invalid VIDEO_TS Folder";
        ValidationInfoBar.Message = message;
        ValidationInfoBar.IsOpen = true;
        return isValid;
    }

    private DvdVideoBurnOptions BuildOptions(DriveModel? drive)
    {
        var speed = (SpeedComboBox.SelectedItem as DriveSpeedModel)?.SpeedKBps ?? 0;
        return new DvdVideoBurnOptions
        {
            Drive = drive,
            VideoTsDirectory = VideoTsPathBox.Text.Trim(),
            AudioTsDirectory = string.IsNullOrWhiteSpace(AudioTsPathBox.Text) ? null : AudioTsPathBox.Text.Trim(),
            ExtraFilesDirectory = string.IsNullOrWhiteSpace(ExtraFilesPathBox.Text) ? null : ExtraFilesPathBox.Text.Trim(),
            VolumeName = string.IsNullOrWhiteSpace(DiscNameBox.Text) ? "DVD_VIDEO" : DiscNameBox.Text.Trim(),
            WriteSpeedKBps = speed,
            DoubleLayerCompatibleMode = DoubleLayerCheckBox.IsChecked == true,
            Padding = PadDvdCheckBox.IsChecked == true,
            PatchingIfoBup = PatchIfoBupCheckBox.IsChecked == true,
            VerifyData = VerifyCheckBox.IsChecked == true,
            EjectAfterBurn = EjectCheckBox.IsChecked == true,
            Copies = Math.Max(1, (int)CopiesBox.Value)
        };
    }

    private void SavePreferences()
    {
        var s = AppServices.Current.Settings.Current;
        s.DataVideoLayoutPath = VideoTsPathBox.Text.Trim();
        s.DoubleLayerCompatible = DoubleLayerCheckBox.IsChecked == true;
        s.PadDvdSize = PadDvdCheckBox.IsChecked == true;
        s.ModifyIfoBupFiles = PatchIfoBupCheckBox.IsChecked == true;
        AppServices.Current.Settings.Save();
    }

    private async void BurnButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (!ValidateFolder())
        {
            ShowStatus(InfoBarSeverity.Error, "Invalid VIDEO_TS Folder", "Please select a valid VIDEO_TS folder containing .IFO and .VOB files.");
            return;
        }

        var drive = DriveComboBox.SelectedItem as DriveModel;
        if (drive is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "No Drive Selected", "Select an optical writer before burning, or use 'Save as ISO...'.");
            return;
        }

        SavePreferences();
        var options = BuildOptions(drive);
        await ExecuteOperationAsync($"Burning DVD-Video to {drive.DisplayName}",
            (progress, ct) => _burner.BurnAsync(options, progress, ct),
            allowShutdown: ShutdownCheckBox.IsChecked == true);
    }

    private async void SaveIsoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (!ValidateFolder())
        {
            ShowStatus(InfoBarSeverity.Error, "Invalid VIDEO_TS Folder", "Please select a valid VIDEO_TS folder before saving an ISO.");
            return;
        }

        var suggested = string.IsNullOrWhiteSpace(DiscNameBox.Text) ? "DVD_VIDEO" : DiscNameBox.Text.Trim();
        var isoPath = await FilePickerHelper.PickSaveFileAsync(suggested, new Dictionary<string, IList<string>>
        {
            ["ISO Disc Image"] = [".iso"]
        });
        if (string.IsNullOrWhiteSpace(isoPath)) return;

        SavePreferences();
        var options = BuildOptions(null);
        await ExecuteOperationAsync($"Saving DVD-Video ISO to {Path.GetFileName(isoPath)}",
            (progress, ct) => _burner.SaveIsoAsync(options, isoPath, progress, ct),
            allowShutdown: false);
    }

    private async Task ExecuteOperationAsync(
        string startMessage,
        Func<IProgress<BurnProgress>, CancellationToken, Task> work,
        bool allowShutdown)
    {
        _isBusy = true;
        SetBusyUi(true);
        ProgressCard.Visibility = Visibility.Visible;
        BurnProgressBar.Value = 0;
        ProgressPhaseText.Text = startMessage;
        ProgressTimeText.Text = "Elapsed: 00:00";

        var log = new StringBuilder();
        void AppendLog(string msg)
        {
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            EventLogBox.Text = log.ToString();
            CDBurner10.Core.Utilities.DiagnosticLogger.Info("VideoDvd", msg);
        }

        AppendLog(startMessage);
        var sw = Stopwatch.StartNew();
        _cts = new CancellationTokenSource();
        string lastLoggedMsg = string.Empty;

        using var op = AppServices.Current.Operations.Begin(Loc.T("Video DVD"), startMessage);

        var progress = new Progress<BurnProgress>(p =>
        {
            BurnProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            ProgressPhaseText.Text = string.IsNullOrWhiteSpace(p.Message) ? $"{p.Phase} ({p.Percent}%)" : p.Message;
            var remText = p.TimeRemaining > TimeSpan.Zero ? $" • Remaining: {FormatHelper.FormatDuration(p.TimeRemaining)}" : string.Empty;
            ProgressTimeText.Text = $"Elapsed: {FormatHelper.FormatDuration(sw.Elapsed)}{remText}";
            op.Report(p);

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
            AppendLog("Operation completed successfully.");
            ShowStatus(InfoBarSeverity.Success, "Completed", "DVD-Video operation finished successfully.");
            op.Complete("DVD-Video operation finished successfully.");

            if (allowShutdown && !_cts.IsCancellationRequested)
            {
                AppendLog("Initiating system shutdown...");
                SystemPowerHelper.ShutdownComputer();
            }
        }
        catch (OperationCanceledException)
        {
            op.Cancel();
            AppendLog("Operation cancelled by user.");
            ShowStatus(InfoBarSeverity.Warning, "Cancelled", "DVD-Video operation was cancelled.");
        }
        catch (Exception ex)
        {
            op.Fail(ex.Message);
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
        _burner.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void SetBusyUi(bool busy)
    {
        BurnButton.IsEnabled = !busy;
        SaveIsoButton.IsEnabled = !busy;
        BrowseVideoTsButton.IsEnabled = !busy;
        DriveComboBox.IsEnabled = !busy;
        SpeedComboBox.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private void BurnFromFile_Click(object sender, RoutedEventArgs e) => AppServices.Current.NavigateTo("dvdfile");
}
