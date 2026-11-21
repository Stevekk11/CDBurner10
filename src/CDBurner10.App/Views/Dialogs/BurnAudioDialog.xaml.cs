using System.Diagnostics;
using System.Text;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed partial class BurnAudioDialog : ContentDialog
{
    private readonly AudioCompilation _compilation;
    private readonly AudioBurner _burner = new();
    private CancellationTokenSource? _cts;
    private bool _isBurning;

    public BurnAudioDialog(AudioCompilation compilation, DriveModel? preferredDrive)
    {
        InitializeComponent();
        _compilation = compilation;

        SummaryText.Text = $"{compilation.Tracks.Count} track(s) • Total duration: {FormatHelper.FormatDuration(compilation.TotalDuration)}";

        var s = AppServices.Current.Settings.Current;
        PauseSecondsBox.Value = Math.Clamp(s.AudioPauseLength, 0, 10);
        CrossfadeSecondsBox.Value = s.AudioCrossfadingDuration > 0 ? Math.Clamp(s.AudioCrossfadingDuration, 0.5, 15) : 3.0;
        if (s.AudioCrossfadingDuration > 0) GapModeRadio.SelectedIndex = 3;
        else if (s.AudioBurnGapless) GapModeRadio.SelectedIndex = 1;
        else GapModeRadio.SelectedIndex = 0;

        CdTextCheckBox.IsChecked = s.CDTextEnabled;
        FinalizeCheckBox.IsChecked = s.AudioCloseDisc;
        EjectCheckBox.IsChecked = s.AudioBurnEject;

        var drives = AppServices.Current.Drives;
        DriveComboBox.ItemsSource = drives;
        DriveComboBox.DisplayMemberPath = nameof(DriveModel.DisplayName);
        DriveComboBox.SelectedItem = drives.FirstOrDefault(d => d.Letter == preferredDrive?.Letter)
                                     ?? drives.FirstOrDefault();

        if (drives.Count == 0)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Title = "No Optical Drive Detected";
            StatusInfoBar.Message = "Connect a CD writer before burning an Audio CD.";
            StatusInfoBar.IsOpen = true;
            IsPrimaryButtonEnabled = false;
        }
        else if (compilation.Tracks.Count == 0)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Title = "No Tracks";
            StatusInfoBar.Message = "Add at least one audio track to the compilation first.";
            StatusInfoBar.IsOpen = true;
            IsPrimaryButtonEnabled = false;
        }

        PopulateSpeeds();
        UpdateGapInputs();
    }

    private void PopulateSpeeds()
    {
        var drive = DriveComboBox.SelectedItem as DriveModel;
        SpeedComboBox.ItemsSource = drive?.SupportedWriteSpeeds ?? [new DriveSpeedModel(0, 0)];
        SpeedComboBox.DisplayMemberPath = nameof(DriveSpeedModel.DisplayText);
        SpeedComboBox.SelectedIndex = 0;
    }

    private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => PopulateSpeeds();

    private void GapModeRadio_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateGapInputs();

    private void UpdateGapInputs()
    {
        if (PauseSecondsBox is null || CrossfadeSecondsBox is null) return;
        PauseSecondsBox.IsEnabled = GapModeRadio.SelectedIndex == 0;
        CrossfadeSecondsBox.IsEnabled = GapModeRadio.SelectedIndex == 3;
    }

    private AudioGapMode GetGapMode() => GapModeRadio.SelectedIndex switch
    {
        1 => AudioGapMode.DaoGapless,
        2 => AudioGapMode.CustomGaps,
        3 => AudioGapMode.Crossfade,
        _ => AudioGapMode.TaoPause
    };

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_isBurning) return;

        var drive = DriveComboBox.SelectedItem as DriveModel;
        if (drive is null) return;

        var gapMode = GetGapMode();
        var s = AppServices.Current.Settings.Current;
        s.AudioPauseLength = (int)PauseSecondsBox.Value;
        s.AudioBurnGapless = gapMode == AudioGapMode.DaoGapless;
        s.AudioCrossfadingDuration = gapMode == AudioGapMode.Crossfade ? CrossfadeSecondsBox.Value : 0;
        s.CDTextEnabled = CdTextCheckBox.IsChecked == true;
        s.AudioCloseDisc = FinalizeCheckBox.IsChecked == true;
        s.AudioBurnEject = EjectCheckBox.IsChecked == true;
        AppServices.Current.Settings.Save();

        var options = new AudioBurnOptions
        {
            Drive = drive,
            WriteSpeedKBps = (SpeedComboBox.SelectedItem as DriveSpeedModel)?.SpeedKBps ?? 0,
            GapMode = gapMode,
            PauseSeconds = Math.Max(0, (int)PauseSecondsBox.Value),
            CrossfadeSeconds = gapMode == AudioGapMode.Crossfade ? CrossfadeSecondsBox.Value : 0,
            EnableCdText = CdTextCheckBox.IsChecked == true,
            ReplayGainMode = s.AudioReplayGain,
            FinalizeDisc = FinalizeCheckBox.IsChecked == true,
            TestBurn = SimulateCheckBox.IsChecked == true,
            EjectAfterBurn = EjectCheckBox.IsChecked == true,
            Copies = Math.Max(1, (int)CopiesBox.Value)
        };

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

        AppendLog($"Starting Audio CD burn ({_compilation.Tracks.Count} tracks) to {drive.DisplayName}...");
        var sw = Stopwatch.StartNew();
        _cts = new CancellationTokenSource();
        string lastLogged = string.Empty;

        var progress = new Progress<BurnProgress>(p =>
        {
            BurnProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            PhaseText.Text = string.IsNullOrWhiteSpace(p.Message) ? $"{p.Phase} ({p.Percent}%)" : p.Message;
            BufferText.Text = $"Device Buffer: {p.BufferPercent}%";
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
            await _burner.BurnAsync(_compilation, options, progress, _cts.Token);
            AppendLog("Audio CD burned successfully.");
            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "Audio CD Burned";
            StatusInfoBar.Message = "Your Audio CD has been created successfully.";
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
            StatusInfoBar.Title = "Cancelled";
            StatusInfoBar.Message = "Audio CD burn was cancelled.";
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
        _burner.Cancel();
        CancelBurnButton.IsEnabled = false;
    }

    private void ContentDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_isBurning) args.Cancel = true;
    }
}
