using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace CDBurner10.App.Views.Dialogs;

public sealed record AudioCdTrackRow(int TrackNumber, string TrackLabel, string SizeLabel, string DurationLabel);

public sealed partial class RipAudioCdDialog : ContentDialog
{
    private CancellationTokenSource? _ripCts;
    private DiscGrabber? _activeGrabber;
    private bool _isRipping;

    public IReadOnlyList<string> RippedFiles { get; private set; } = [];

    public RipAudioCdDialog(DriveModel? preferredDrive)
    {
        InitializeComponent();

        var drives = AppServices.Current.Drives;
        DriveComboBox.ItemsSource = drives;
        DriveComboBox.DisplayMemberPath = nameof(DriveModel.DisplayName);
        DriveComboBox.SelectedItem = drives.FirstOrDefault(d => d.Letter == preferredDrive?.Letter)
                                     ?? drives.FirstOrDefault();

        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        PopulateReadSpeeds(DriveComboBox.SelectedItem as DriveModel);
        _ = LoadTracksAsync();
    }

    private void PopulateReadSpeeds(DriveModel? drive)
    {
        int prevSpeed = (ReadSpeedComboBox.SelectedItem as DriveSpeedModel)?.SpeedKBps ?? 0;
        var speeds = DriveModel.BuildReadSpeedPresets(drive?.MaxReadSpeed ?? 0, OpticalDiscType.CdRom);
        ReadSpeedComboBox.ItemsSource = speeds;
        ReadSpeedComboBox.DisplayMemberPath = nameof(DriveSpeedModel.DisplayText);
        ReadSpeedComboBox.SelectedItem = speeds.FirstOrDefault(s => s.SpeedKBps == prevSpeed) ?? speeds[0];
    }

    private async Task LoadTracksAsync()
    {
        var drive = DriveComboBox.SelectedItem as DriveModel;
        PopulateReadSpeeds(drive);
        if (drive is null)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Informational;
            StatusInfoBar.Title = "No Optical Drive";
            StatusInfoBar.Message = "Connect an optical drive containing an Audio CD.";
            StatusInfoBar.IsOpen = true;
            AudioTracksListView.ItemsSource = null;
            IsPrimaryButtonEnabled = false;
            IsSecondaryButtonEnabled = false;
            return;
        }

        RipStatusText.Text = "Scanning Audio CD tracks...";
        var info = await drive.RefreshDiscInfoAsync();
        PopulateReadSpeeds(drive);
        var audioTracks = info.Sessions
            .SelectMany(s => s.Tracks)
            .Where(t => t.IsAudio)
            .Select(t => new AudioCdTrackRow(
                t.TrackNumber,
                $"Track {t.TrackNumber:D2}",
                FormatHelper.FormatBytes(t.SizeBytes),
                FormatHelper.FormatDuration(t.AudioDuration)))
            .ToList();

        AudioTracksListView.ItemsSource = audioTracks;
        if (audioTracks.Count == 0)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Title = "No Audio Tracks Found";
            StatusInfoBar.Message = "Insert an Audio CD into the selected drive and click Refresh.";
            StatusInfoBar.IsOpen = true;
            IsPrimaryButtonEnabled = false;
            IsSecondaryButtonEnabled = false;
            RipStatusText.Text = string.Empty;
        }
        else
        {
            StatusInfoBar.IsOpen = false;
            AudioTracksListView.SelectAll();
            IsPrimaryButtonEnabled = true;
            IsSecondaryButtonEnabled = true;
            RipStatusText.Text = $"{audioTracks.Count} audio track(s) available.";
        }
    }

    private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isRipping) _ = LoadTracksAsync();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRipping) _ = LoadTracksAsync();
    }

    private void CancelRipButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRipButton.IsEnabled = false;
        RipStatusText.Text = "Cancelling rip...";
        _ripCts?.Cancel();
        _activeGrabber?.Cancel();
    }

    private void ContentDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_isRipping && args.Result == ContentDialogResult.None)
        {
            _ripCts?.Cancel();
            _activeGrabber?.Cancel();
        }
    }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var outcome = await RipSelectedAsync(exportFolder: null);
            if (outcome is null || outcome.RippedPaths.Count == 0)
            {
                args.Cancel = true;
                return;
            }
            RippedFiles = outcome.RippedPaths;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void ContentDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // Keep dialog open to show export result
        var deferral = args.GetDeferral();
        try
        {
            var folder = await FilePickerHelper.PickFolderAsync(PickerLocationId.MusicLibrary);
            if (string.IsNullOrWhiteSpace(folder)) return;

            var outcome = await RipSelectedAsync(exportFolder: folder);
            if (outcome is { RippedPaths.Count: > 0 })
            {
                if (outcome.TotalPaddedSectors > 0 || outcome.SkippedTracks.Count > 0)
                {
                    var details = new List<string>();
                    if (outcome.TotalPaddedSectors > 0)
                        details.Add($"{outcome.TotalPaddedSectors} scratched sector(s) padded across {string.Join(", ", outcome.PaddedTracks)}");
                    if (outcome.SkippedTracks.Count > 0)
                        details.Add($"skipped unreadable: {string.Join(", ", outcome.SkippedTracks)}");

                    StatusInfoBar.Severity = InfoBarSeverity.Warning;
                    StatusInfoBar.Title = "Tracks Exported with Read Warnings";
                    StatusInfoBar.Message = $"Exported {outcome.RippedPaths.Count} WAV track(s) to '{folder}' ({string.Join("; ", details)}).";
                    StatusInfoBar.IsOpen = true;
                }
                else
                {
                    StatusInfoBar.Severity = InfoBarSeverity.Success;
                    StatusInfoBar.Title = "Tracks Exported";
                    StatusInfoBar.Message = $"Exported {outcome.RippedPaths.Count} WAV track(s) to '{folder}'.";
                    StatusInfoBar.IsOpen = true;
                }
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private sealed record RipBatchOutcome(
        List<string> RippedPaths,
        int TotalPaddedSectors,
        List<string> PaddedTracks,
        List<string> SkippedTracks);

    private async Task<RipBatchOutcome?> RipSelectedAsync(string? exportFolder)
    {
        var drive = DriveComboBox.SelectedItem as DriveModel;
        var selected = AudioTracksListView.SelectedItems.OfType<AudioCdTrackRow>().ToList();
        if (drive is null || selected.Count == 0) return null;

        _isRipping = true;
        _ripCts = new CancellationTokenSource();
        var ct = _ripCts.Token;

        StatusInfoBar.IsOpen = false;
        SetRipBusyUi(true);
        RipProgressBar.Value = 0;

        var grabber = new DiscGrabber();
        _activeGrabber = grabber;
        var ripped = new List<string>();
        var paddedTracks = new List<string>();
        var skippedTracks = new List<string>();
        int totalPaddedSectors = 0;
        bool recoverScratches = RecoverBadSectorsCheckBox.IsChecked == true;
        int readSpeed = (ReadSpeedComboBox.SelectedItem as DriveSpeedModel)?.SpeedKBps ?? 0;

        try
        {
            for (int i = 0; i < selected.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var tr = selected[i];
                var wavPath = string.IsNullOrWhiteSpace(exportFolder)
                    ? AppServices.Current.Settings.GetTempRipFileName($"Track_{tr.TrackNumber:D2}", ".wav")
                    : GetUniqueExportPath(exportFolder, $"Track_{tr.TrackNumber:D2}", ".wav");
                int idx = i;
                var prog = new Progress<BurnProgress>(p =>
                {
                    RipProgressBar.Value = Math.Clamp((idx * 100.0 + p.Percent) / selected.Count, 0, 100);
                    RipStatusText.Text = $"Ripping Track {tr.TrackNumber:D2} ({idx + 1}/{selected.Count})... {p.Percent}%";
                });

                var opts = new GrabOptions
                {
                    Drive = drive,
                    TargetPath = wavPath,
                    ReadSpeedKBps = readSpeed,
                    IgnoreBadBlocks = recoverScratches,
                    NumberOfReadRetries = 3
                };

                try
                {
                    var result = await grabber.GrabTrackAsync(opts, tr.TrackNumber, isAudio: true, prog, ct);
                    ripped.Add(wavPath);
                    if (result.BadSectorCount > 0)
                    {
                        totalPaddedSectors += result.BadSectorCount;
                        paddedTracks.Add($"Track {tr.TrackNumber:D2} ({result.BadSectorCount})");
                    }
                }
                catch (OperationCanceledException)
                {
                    PathHelper.TryDeleteFile(wavPath);
                    throw;
                }
                catch (Exception trackEx)
                {
                    PathHelper.TryDeleteFile(wavPath);
                    skippedTracks.Add($"Track {tr.TrackNumber:D2}");
                    RipStatusText.Text = $"Skipped unreadable Track {tr.TrackNumber:D2}: {trackEx.Message}";
                }
            }

            if (ripped.Count == 0 && skippedTracks.Count > 0)
            {
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = "Rip Failed";
                StatusInfoBar.Message = $"Could not read any selected track ({string.Join(", ", skippedTracks)}). Enable scratch recovery or lower the read speed.";
                StatusInfoBar.IsOpen = true;
                RipStatusText.Text = "No tracks could be ripped.";
                return null;
            }

            string warnSuffix = totalPaddedSectors > 0 || skippedTracks.Count > 0
                ? $" ({totalPaddedSectors} sector(s) padded, {skippedTracks.Count} skipped)"
                : string.Empty;
            RipStatusText.Text = $"Finished ripping {ripped.Count} track(s){warnSuffix}.";
            return new RipBatchOutcome(ripped, totalPaddedSectors, paddedTracks, skippedTracks);
        }
        catch (OperationCanceledException)
        {
            if (string.IsNullOrWhiteSpace(exportFolder))
            {
                foreach (var tempFile in ripped)
                    PathHelper.TryDeleteFile(tempFile);
            }

            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Title = "Rip Cancelled";
            StatusInfoBar.Message = string.IsNullOrWhiteSpace(exportFolder) || ripped.Count == 0
                ? "Audio CD ripping was cancelled."
                : $"Cancelled after exporting {ripped.Count} completed track(s).";
            StatusInfoBar.IsOpen = true;
            RipStatusText.Text = "Rip cancelled.";
            return null;
        }
        catch (Exception ex)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Rip Failed";
            StatusInfoBar.Message = ex.Message;
            StatusInfoBar.IsOpen = true;
            return null;
        }
        finally
        {
            _activeGrabber = null;
            _ripCts?.Dispose();
            _ripCts = null;
            _isRipping = false;
            SetRipBusyUi(false);
        }
    }

    private void SetRipBusyUi(bool busy)
    {
        IsPrimaryButtonEnabled = !busy;
        IsSecondaryButtonEnabled = !busy;
        DriveComboBox.IsEnabled = !busy;
        ReadSpeedComboBox.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        AudioTracksListView.IsEnabled = !busy;
        RecoverBadSectorsCheckBox.IsEnabled = !busy;
        RipProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelRipButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelRipButton.IsEnabled = busy;
    }

    private static string GetUniqueExportPath(string folder, string baseName, string extension)
    {
        string candidate = Path.Combine(folder, baseName + extension);
        int suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(folder, $"{baseName}_{suffix}{extension}");
            suffix++;
        }
        return candidate;
    }
}
