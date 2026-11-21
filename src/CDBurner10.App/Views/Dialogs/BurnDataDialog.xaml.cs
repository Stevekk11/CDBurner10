using System.Diagnostics;
using System.Text;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed partial class BurnDataDialog : ContentDialog
{
    private readonly DataCompilation _compilation;
    private CancellationTokenSource? _cts;
    private bool _isBurning;

    public BurnDataDialog(DataCompilation compilation, DriveModel? preferredDrive)
    {
        InitializeComponent();
        _compilation = compilation;

        var burner = compilation.DataBurner;
        CompilationSummaryText.Text =
            $"{burner.FileCount} file(s), {burner.FolderCount} folder(s) • {FormatHelper.FormatBytes(burner.TotalSizeBytes)} • FS: {(burner.FileSystemType == DataFileSystem.Udf ? $"UDF ({burner.UdfVersion})" : "ISO9660 / Joliet")}";

        var s = AppServices.Current.Settings.Current;
        FinalizeRadio.SelectedIndex = s.DataBurnFinalize ? 1 : 0;
        VerifyCheckBox.IsChecked = s.DataBurnVerify;
        EjectCheckBox.IsChecked = s.DataBurnEject;

        VolumeLabelBox.Text = burner.VolumeName;
        PublisherBox.Text = burner.PublisherName;
        AppNameBox.Text = burner.ApplicationName;
        BootStatusText.Text = burner.BootImage.Enabled
            ? $"Bootable disc enabled: {Path.GetFileName(burner.BootImage.FileName)} ({burner.BootImage.EmulationType})"
            : "Bootable disc: Disabled (configure via Boot Options toolbar button)";

        var drives = AppServices.Current.Drives;
        DriveComboBox.ItemsSource = drives;
        DriveComboBox.DisplayMemberPath = nameof(DriveModel.DisplayName);
        DriveComboBox.SelectedItem = drives.FirstOrDefault(d => d.Letter == preferredDrive?.Letter)
                                     ?? drives.FirstOrDefault();

        if (drives.Count == 0)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Title = "No Optical Drive Detected";
            StatusInfoBar.Message = "Connect an optical writer before burning. You can still save this compilation as an ISO from the toolbar.";
            StatusInfoBar.IsOpen = true;
            IsPrimaryButtonEnabled = false;
        }

        PopulateSpeeds();
    }

    private void PopulateSpeeds()
    {
        var drive = DriveComboBox.SelectedItem as DriveModel;
        SpeedComboBox.ItemsSource = drive?.SupportedWriteSpeeds ?? [new DriveSpeedModel(0, 0)];
        SpeedComboBox.DisplayMemberPath = nameof(DriveSpeedModel.DisplayText);
        SpeedComboBox.SelectedIndex = 0;
    }

    private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => PopulateSpeeds();

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_isBurning) return;

        var drive = DriveComboBox.SelectedItem as DriveModel;
        if (drive is null)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "No Drive Selected";
            StatusInfoBar.Message = "Select an optical writer first.";
            StatusInfoBar.IsOpen = true;
            return;
        }

        var burner = _compilation.DataBurner;
        burner.VolumeName = string.IsNullOrWhiteSpace(VolumeLabelBox.Text) ? "Disc" : VolumeLabelBox.Text.Trim();
        burner.PublisherName = PublisherBox.Text?.Trim() ?? string.Empty;
        burner.ApplicationName = string.IsNullOrWhiteSpace(AppNameBox.Text) ? "CDBurner10" : AppNameBox.Text.Trim();

        int customSpeed = (int)CustomSpeedBox.Value;
        int speedKBps = customSpeed > 0
            ? customSpeed
            : (SpeedComboBox.SelectedItem as DriveSpeedModel)?.SpeedKBps ?? 0;

        bool finalize = FinalizeRadio.SelectedIndex == 1;
        var s = AppServices.Current.Settings.Current;
        s.DataBurnFinalize = finalize;
        s.DataBurnVerify = VerifyCheckBox.IsChecked == true;
        s.DataBurnEject = EjectCheckBox.IsChecked == true;
        AppServices.Current.Settings.Save();

        var options = new DataBurnOptions
        {
            Drive = drive,
            WriteSpeedKBps = speedKBps,
            TestBurn = SimulateCheckBox.IsChecked == true,
            FinalizeDisc = finalize,
            VerifyData = VerifyCheckBox.IsChecked == true,
            EjectAfterBurn = EjectCheckBox.IsChecked == true,
            Copies = Math.Max(1, (int)CopiesBox.Value),
            VolumeName = burner.VolumeName,
            PublisherName = burner.PublisherName,
            ApplicationName = burner.ApplicationName
        };

        // Switch to Phase 2
        _isBurning = true;
        OptionsPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        IsPrimaryButtonEnabled = false;
        CloseButtonText = string.Empty;
        StatusInfoBar.IsOpen = false;
        CancelBurnButton.IsEnabled = true;

        var log = new StringBuilder();
        void AppendLog(string msg)
        {
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            EventLogBox.Text = log.ToString();
        }

        AppendLog($"Starting data burn to {drive.DisplayName}...");
        var sw = Stopwatch.StartNew();
        _cts = new CancellationTokenSource();
        string lastLogged = string.Empty;

        var progress = new Progress<BurnProgress>(p =>
        {
            WriteProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            WritePercentText.Text = $"Write Progress: {p.Percent}%";
            BufferProgressBar.Value = Math.Clamp(p.BufferPercent, 0, 100);
            BufferPercentText.Text = $"Device Buffer: {p.BufferPercent}%";
            PhaseText.Text = string.IsNullOrWhiteSpace(p.Message) ? p.Phase.ToString() : p.Message;
            var rem = p.TimeRemaining > TimeSpan.Zero ? $" • Remaining: {FormatHelper.FormatDuration(p.TimeRemaining)}" : string.Empty;
            TimeText.Text = $"Elapsed: {FormatHelper.FormatDuration(sw.Elapsed)}{rem}";
            if (!string.IsNullOrWhiteSpace(p.Message) && p.Message != lastLogged && p.Phase != BurnPhase.Burning)
            {
                lastLogged = p.Message;
                AppendLog(p.Message);
            }
        });

        try
        {
            await burner.BurnAsync(options, progress, _cts.Token);
            AppendLog("Burn completed successfully.");
            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "Burn Completed";
            StatusInfoBar.Message = "Your data disc has been burned successfully.";
            StatusInfoBar.IsOpen = true;

            if (ShutdownCheckBox.IsChecked == true && !_cts.IsCancellationRequested)
            {
                AppendLog("Initiating system shutdown...");
                SystemPowerHelper.ShutdownComputer();
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Burn cancelled by user.");
            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Title = "Burn Cancelled";
            StatusInfoBar.Message = "The burn operation was cancelled.";
            StatusInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Burn Failed";
            StatusInfoBar.Message = ex.Message;
            StatusInfoBar.IsOpen = true;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _isBurning = false;
            CancelBurnButton.IsEnabled = false;
            CloseButtonText = "Close";
        }
    }

    private void CancelBurnButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _compilation.DataBurner.Cancel();
        CancelBurnButton.IsEnabled = false;
    }

    private void ContentDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_isBurning)
        {
            args.Cancel = true;
        }
    }
}
