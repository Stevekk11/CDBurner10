using CDBurner10.Core.Burning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed partial class CdTextDialog : ContentDialog
{
    private readonly AudioCompilation _compilation;
    private readonly AudioTrackItem? _selectedTrack;

    public CdTextDialog(AudioCompilation compilation, AudioTrackItem? selectedTrack)
    {
        InitializeComponent();
        _compilation = compilation;
        _selectedTrack = selectedTrack;

        DiscArtistBox.Text = compilation.Artist;
        DiscTitleBox.Text = compilation.Title;

        if (selectedTrack is null)
        {
            TrackPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            TrackHeaderText.Text = $"Selected Track: {Path.GetFileName(selectedTrack.FileName)}";
            TrackArtistBox.Text = string.IsNullOrWhiteSpace(selectedTrack.CDTextArtist) ? selectedTrack.Artist : selectedTrack.CDTextArtist;
            TrackTitleBox.Text = string.IsNullOrWhiteSpace(selectedTrack.CDTextTitle) ? selectedTrack.Title : selectedTrack.CDTextTitle;
            PreGapBox.Value = Math.Clamp(selectedTrack.PreGap.TotalSeconds, 0, 30);
            PostGapBox.Value = Math.Clamp(selectedTrack.PostGap.TotalSeconds, 0, 30);
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _compilation.Artist = AudioTrackItem.StripToCdTextSize(DiscArtistBox.Text);
        _compilation.Title = AudioTrackItem.StripToCdTextSize(DiscTitleBox.Text);

        if (_selectedTrack is not null)
        {
            _selectedTrack.CDTextArtist = AudioTrackItem.StripToCdTextSize(TrackArtistBox.Text);
            _selectedTrack.CDTextTitle = AudioTrackItem.StripToCdTextSize(TrackTitleBox.Text);
            _selectedTrack.PreGap = TimeSpan.FromSeconds(Math.Max(0, PreGapBox.Value));
            _selectedTrack.PostGap = TimeSpan.FromSeconds(Math.Max(0, PostGapBox.Value));
        }
    }
}
