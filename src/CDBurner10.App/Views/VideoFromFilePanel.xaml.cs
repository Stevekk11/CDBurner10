using CDBurner10.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace CDBurner10.App.Views;

/// <summary>Placeholder UI for burning MP4/MKV files to a Video DVD or Video Blu-ray.</summary>
public sealed partial class VideoFromFilePanel : UserControl
{
    private bool _bluRay;

    public VideoFromFilePanel()
    {
        InitializeComponent();
    }

    public void Configure(bool bluRay)
    {
        _bluRay = bluRay;
        string disc = bluRay ? "Video Blu-ray" : "Video DVD";
        TitleText.Text = $"Burn from MP4/MKV - {disc}";
        SubtitleText.Text = bluRay
            ? "Convert MP4 or MKV files into a Blu-ray compatible disc (BDMV)."
            : "Convert MP4 or MKV files into a DVD-Video compatible disc.";
        BurnButton.Content = $"Burn {disc}";

        Fill(DiscTypeCombo, bluRay ? ["BD-R 25 GB", "BD-R DL 50 GB", "BD-RE 25 GB"] : ["DVD 4.7 GB", "DVD DL 8.5 GB"]);
        Fill(StandardCombo, bluRay ? ["1080p 24 / 25 / 30", "1080i", "720p"] : ["PAL (576i)", "NTSC (480i)"]);
        Fill(AspectCombo, ["Auto-detect", "16:9", "4:3"]);
        Fill(QualityCombo, ["Best fit for disc", "High", "Medium", "Low"]);
        Fill(AudioTrackCombo, ["First track", "All tracks"]);
        Fill(SubtitleCombo, ["None", "Embedded", "External file (.srt)"]);
    }

    private static void Fill(ComboBox box, string[] items)
    {
        box.Items.Clear();
        foreach (var i in items) box.Items.Add(i);
        box.SelectedIndex = 0;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSingleFileAsync([".mp4", ".mkv"], PickerLocationId.VideosLibrary);
        if (!string.IsNullOrWhiteSpace(path)) SourcePathBox.Text = path;
    }

    private void Burn_Click(object sender, RoutedEventArgs e) => ShowComingSoon();

    private void SaveIso_Click(object sender, RoutedEventArgs e) => ShowComingSoon();

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
