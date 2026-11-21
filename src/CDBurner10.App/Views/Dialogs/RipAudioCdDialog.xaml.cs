using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed record AudioCdTrackRow(int TrackNumber, string TrackLabel, string SizeLabel, string DurationLabel);

public sealed partial class RipAudioCdDialog : ContentDialog
{
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
        _ = LoadTracksAsync();
    }

    private async Task LoadTracksAsync()
    {
        var drive = DriveComboBox.SelectedItem as DriveModel;
        if (drive is null)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Informational;
            StatusInfoBar.Title = "No Optical Drive";
            StatusInfoBar.Message = "Connect an optical drive containing an Audio CD.";
            StatusInfoBar.IsOpen = true;
            AudioTracksListView.ItemsSource = null;
            IsPrimaryButtonEnabled = false;
            return;
        }

        RipStatusText.Text = "Scanning Audio CD tracks...";
        var info = await drive.RefreshDiscInfoAsync();
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
            RipStatusText.Text = string.Empty;
        }
        else
        {
            StatusInfoBar.IsOpen = false;
            AudioTracksListView.SelectAll();
            IsPrimaryButtonEnabled = true;
            RipStatusText.Text = $"{audioTracks.Count} audio track(s) available.";
        }
    }

    private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => _ = LoadTracksAsync();
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = LoadTracksAsync();

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var drive = DriveComboBox.SelectedItem as DriveModel;
        var selected = AudioTracksListView.SelectedItems.OfType<AudioCdTrackRow>().ToList();
        if (drive is null || selected.Count == 0) return;

        var deferral = args.GetDeferral();
        IsPrimaryButtonEnabled = false;
        RipProgressBar.Visibility = Visibility.Visible;
        RipProgressBar.Value = 0;

        try
        {
            var grabber = new DiscGrabber();
            var ripped = new List<string>();

            for (int i = 0; i < selected.Count; i++)
            {
                var tr = selected[i];
                var wavPath = AppServices.Current.Settings.GetTempRipFileName($"Track_{tr.TrackNumber:D2}", ".wav");
                int idx = i;
                var prog = new Progress<BurnProgress>(p =>
                {
                    RipProgressBar.Value = Math.Clamp((idx * 100.0 + p.Percent) / selected.Count, 0, 100);
                    RipStatusText.Text = $"Ripping Track {tr.TrackNumber:D2} ({idx + 1}/{selected.Count})... {p.Percent}%";
                });

                var opts = new GrabOptions
                {
                    Drive = drive,
                    TargetPath = wavPath
                };
                await grabber.GrabTrackAsync(opts, tr.TrackNumber, isAudio: true, prog);
                ripped.Add(wavPath);
            }

            RippedFiles = ripped;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Rip Failed";
            StatusInfoBar.Message = ex.Message;
            StatusInfoBar.IsOpen = true;
            IsPrimaryButtonEnabled = true;
        }
        finally
        {
            RipProgressBar.Visibility = Visibility.Collapsed;
            deferral.Complete();
        }
    }
}
