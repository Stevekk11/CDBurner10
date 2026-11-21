using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Burning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed record ImportTrackRow(int GlobalTrackNumber, string SessionLabel, string TrackLabel, string TypeLabel, string SizeLabel, bool IsAudio);

public sealed partial class ImportSessionDialog : ContentDialog
{
    public int? SelectedTrackNumber => (TracksListView.SelectedItem as ImportTrackRow)?.GlobalTrackNumber;

    public ImportSessionDialog(DriveModel? preferredDrive)
    {
        InitializeComponent();

        var drives = AppServices.Current.Drives;
        DriveComboBox.ItemsSource = drives;
        DriveComboBox.DisplayMemberPath = nameof(DriveModel.DisplayName);
        DriveComboBox.SelectedItem = drives.FirstOrDefault(d => d.Letter == preferredDrive?.Letter)
                                     ?? drives.FirstOrDefault();

        IsPrimaryButtonEnabled = false;
        _ = LoadSessionsAsync();
    }

    private async Task LoadSessionsAsync()
    {
        var drive = DriveComboBox.SelectedItem as DriveModel;
        if (drive is null)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Informational;
            StatusInfoBar.Title = "No Optical Drive";
            StatusInfoBar.Message = "Connect an optical drive containing a multi-session disc.";
            StatusInfoBar.IsOpen = true;
            MediaSummaryText.Text = "No drive available.";
            TracksListView.ItemsSource = null;
            IsPrimaryButtonEnabled = false;
            return;
        }

        MediaSummaryText.Text = "Reading disc sessions...";
        var info = await drive.RefreshDiscInfoAsync();

        if (!info.HasDisc || info.IsBlank)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Title = info.HasDisc ? "Blank Disc" : "No Disc Inserted";
            StatusInfoBar.Message = "Insert a disc with at least one existing data session to import.";
            StatusInfoBar.IsOpen = true;
            MediaSummaryText.Text = info.DiscTypeDescription;
            TracksListView.ItemsSource = null;
            IsPrimaryButtonEnabled = false;
            return;
        }

        StatusInfoBar.IsOpen = false;
        MediaSummaryText.Text = $"{info.DiscTypeDescription} • {(info.IsNextSessionAllowed ? "Open (Appendable)" : "Finalized")} • {info.Sessions.Count} session(s)";

        var rows = new List<ImportTrackRow>();
        int globalNum = 1;
        foreach (var s in info.Sessions)
        {
            foreach (var t in s.Tracks)
            {
                rows.Add(new ImportTrackRow(
                    globalNum++,
                    $"Session {s.SessionNumber}",
                    $"Track {t.TrackNumber:D2}",
                    t.IsAudio ? "Audio" : "Data",
                    FormatHelper.FormatBytes(t.SizeBytes),
                    t.IsAudio));
            }
        }

        TracksListView.ItemsSource = rows;
        var lastData = rows.LastOrDefault(r => !r.IsAudio);
        if (lastData is not null)
        {
            TracksListView.SelectedItem = lastData;
        }
    }

    private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => _ = LoadSessionsAsync();
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = LoadSessionsAsync();

    private void TracksListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = TracksListView.SelectedItem as ImportTrackRow;
        IsPrimaryButtonEnabled = row is not null && !row.IsAudio;
    }
}
