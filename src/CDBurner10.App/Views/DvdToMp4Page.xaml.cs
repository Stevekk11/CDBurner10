using System.Diagnostics;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Dvd;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace CDBurner10.App.Views;

public sealed record DvdSubtitleChoice(int? Ordinal, string DisplayName);

public sealed partial class DvdToMp4Page : Page
{
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _probeCts;
    private DvdVideoInfo? _info;
    private bool _isBusy;
    private string _lastOutput = string.Empty;
    private string? _autoDestination;

    public DvdToMp4Page()
    {
        InitializeComponent();
        Loaded += DvdToMp4Page_Loaded;
        Unloaded += DvdToMp4Page_Unloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string source && !string.IsNullOrWhiteSpace(source))
            SourcePathBox.Text = source;
    }

    private void DvdToMp4Page_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshFfmpegStatus();
    }

    private void DvdToMp4Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _probeCts?.Cancel();
        _probeCts = null;
    }

    private void RefreshFfmpegStatus()
    {
        if (!DvdVideoConverter.IsAvailable)
            DvdVideoConverter.ResetToolCache();

        if (DvdVideoConverter.IsAvailable)
        {
            FfmpegStatusText.Text = $"ffmpeg ready: {Path.GetFileName(DvdVideoConverter.FfmpegPath)}";
            ToolTipService.SetToolTip(FfmpegStatusText, DvdVideoConverter.FfmpegPath);
            if (StatusInfoBar.Title == "ffmpeg Not Found")
                StatusInfoBar.IsOpen = false;
        }
        else
        {
            FfmpegStatusText.Text = "ffmpeg not found";
            ShowStatus(InfoBarSeverity.Warning, "ffmpeg Not Found", DvdVideoConverter.InstallHint);
        }
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (_isBusy) return;
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (_isBusy || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (item is StorageFolder folder && !string.IsNullOrWhiteSpace(folder.Path))
                {
                    SourcePathBox.Text = folder.Path;
                    return;
                }
                if (item is StorageFile file &&
                    !string.IsNullOrWhiteSpace(file.Path) &&
                    file.Path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    SourcePathBox.Text = file.Path;
                    return;
                }
            }
        }
        catch
        {
            // Ignore shell drag-drop errors.
        }
    }

    private async void TitleNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isBusy || !IsLoaded) return;
        string source = SourcePathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source)) return;
        await ProbeSourceAsync(source);
    }

    private int GetSelectedTitleNumber()
    {
        double val = TitleNumberBox.Value;
        return double.IsNaN(val) ? 0 : Math.Clamp((int)Math.Round(val), 0, 99);
    }

    private async void BrowseIsoButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSingleFileAsync([".iso"]);
        if (!string.IsNullOrWhiteSpace(path))
            SourcePathBox.Text = path;
    }

    private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
            SourcePathBox.Text = path;
    }

    private async void BrowseDestButton_Click(object sender, RoutedEventArgs e)
    {
        string suggested = BuildSuggestedOutputName(SourcePathBox.Text?.Trim() ?? string.Empty);
        var path = await FilePickerHelper.PickSaveFileAsync(suggested, new Dictionary<string, IList<string>>
        {
            ["MP4 Video"] = [".mp4"]
        });
        if (!string.IsNullOrWhiteSpace(path))
        {
            DestinationPathBox.Text = path;
            _autoDestination = null;
        }
    }

    private async void SourcePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDefaultDestination();

        if (_isBusy) return;

        string source = SourcePathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
        {
            ResetInfo();
            return;
        }

        await Task.Delay(350);
        if (SourcePathBox.Text?.Trim() != source) return;

        await ProbeSourceAsync(source);
    }

    private async Task<DvdVideoInfo?> ProbeSourceAsync(string source)
    {
        var cts = new CancellationTokenSource();
        var previous = _probeCts;
        _probeCts = cts;
        previous?.Cancel();

        try
        {
            int titleNumber = GetSelectedTitleNumber();
            DvdInfoText.Text = titleNumber > 0
                ? $"Reading DVD-Video title {titleNumber}..."
                : "Reading DVD-Video structure...";
            var info = await DvdVideoConverter.ProbeAsync(source, titleNumber, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_probeCts, cts)) return null;

            _info = info;
            PopulateTracks(info);
            string titleLabel = titleNumber > 0 ? $"Title {titleNumber}" : "Main title";
            DvdInfoText.Text =
                $"{titleLabel}: {FormatHelper.FormatDuration(info.Duration)} • {info.Width}×{info.Height} • " +
                $"{info.AudioTracks.Count} audio track(s) • {info.SubtitleTracks.Count} subtitle track(s)";
            if (StatusInfoBar.Title == "Cannot Read DVD")
                StatusInfoBar.IsOpen = false;
            return info;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_probeCts, cts))
            {
                _info = null;
                ResetTrackLists();
                DvdInfoText.Text = "Could not read the DVD-Video source.";
                ShowStatus(InfoBarSeverity.Error, "Cannot Read DVD", ex.Message);
            }
            return null;
        }
        finally
        {
            if (ReferenceEquals(_probeCts, cts))
                _probeCts = null;
        }
    }

    private void PopulateTracks(DvdVideoInfo info)
    {
        AudioComboBox.ItemsSource = info.AudioTracks;
        AudioComboBox.DisplayMemberPath = nameof(DvdAudioTrack.DisplayName);
        AudioComboBox.SelectedIndex = info.AudioTracks.Count > 0 ? 0 : -1;

        var subtitleChoices = new List<DvdSubtitleChoice> { new(null, "None (no subtitles)") };
        subtitleChoices.AddRange(info.SubtitleTracks.Select(s => new DvdSubtitleChoice(s.Ordinal, s.DisplayName)));
        SubtitleComboBox.ItemsSource = subtitleChoices;
        SubtitleComboBox.DisplayMemberPath = nameof(DvdSubtitleChoice.DisplayName);
        SubtitleComboBox.SelectedIndex = 0;
    }

    private void ResetInfo()
    {
        _info = null;
        ResetTrackLists();
        DvdInfoText.Text = "Choose a source to detect the movie length and available audio/subtitle tracks.";
    }

    private void ResetTrackLists()
    {
        AudioComboBox.ItemsSource = null;
        SubtitleComboBox.ItemsSource = null;
    }

    private void UpdateDefaultDestination()
    {
        string source = SourcePathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source)) return;

        string suggested = BuildSuggestedOutputPath(source);
        if (string.IsNullOrWhiteSpace(DestinationPathBox.Text) ||
            DestinationPathBox.Text == _autoDestination)
        {
            DestinationPathBox.Text = suggested;
            _autoDestination = suggested;
        }
    }

    private static string BuildSuggestedOutputPath(string source)
    {
        try
        {
            string full = Path.GetFullPath(source);
            if (Directory.Exists(full))
            {
                string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(trimmed);
                if (name.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
                {
                    string? parent = Path.GetDirectoryName(trimmed);
                    name = string.IsNullOrWhiteSpace(parent) ? "DVDMovie" : Path.GetFileName(parent);
                }
                return Path.Combine(Path.GetDirectoryName(trimmed) ?? full, name + ".mp4");
            }

            return Path.ChangeExtension(full, ".mp4");
        }
        catch
        {
            return "DVDMovie.mp4";
        }
    }

    private static string BuildSuggestedOutputName(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "DVDMovie";
        try
        {
            string path = BuildSuggestedOutputPath(source);
            string name = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(name) ? "DVDMovie" : name;
        }
        catch
        {
            return "DVDMovie";
        }
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        RefreshFfmpegStatus();
        if (!DvdVideoConverter.IsAvailable)
        {
            ShowStatus(InfoBarSeverity.Error, "ffmpeg Not Found", DvdVideoConverter.InstallHint);
            return;
        }

        string source = SourcePathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
        {
            ShowStatus(InfoBarSeverity.Warning, "Source Missing", "Select a DVD-Video ISO or VIDEO_TS folder first.");
            return;
        }

        DvdVideoInfo? info = _info;
        if (info is null)
        {
            info = await ProbeSourceAsync(source);
            if (info is null) return;
        }

        string destination = DestinationPathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(destination))
        {
            ShowStatus(InfoBarSeverity.Warning, "Output Missing", "Choose an output MP4 file path.");
            return;
        }

        if (!destination.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            destination += ".mp4";
            DestinationPathBox.Text = destination;
        }

        var selectedAudio = AudioComboBox.SelectedItem as DvdAudioTrack;
        var selectedSubtitle = SubtitleComboBox.SelectedItem as DvdSubtitleChoice;

        int crf = 21;
        string preset = "medium";
        if (QualityComboBox.SelectedItem is ComboBoxItem quality && quality.Tag is string tag)
        {
            var parts = tag.Split(';');
            if (parts.Length == 2 && int.TryParse(parts[0], out int parsedCrf))
            {
                crf = parsedCrf;
                preset = parts[1];
            }
        }

        var options = new DvdMp4Options
        {
            TitleNumber = GetSelectedTitleNumber(),
            AudioOrdinal = selectedAudio?.Ordinal ?? 0,
            SubtitleOrdinal = selectedSubtitle?.Ordinal,
            Crf = crf,
            Preset = preset,
            StereoDownmix = StereoDownmixCheckBox.IsChecked == true,
            Deinterlace = DeinterlaceCheckBox.IsChecked == true
        };

        _isBusy = true;
        SetBusyUi(true);
        _lastOutput = destination;
        OpenMp4Button.Visibility = Visibility.Collapsed;
        ShowInFolderButton.Visibility = Visibility.Collapsed;
        ProgressCard.Visibility = Visibility.Visible;
        ConvertProgressBar.Value = 0;
        ProgressPercentText.Text = "0%";
        ProgressStatusText.Text = $"Converting {Path.GetFileName(source)}...";
        ProgressDetailText.Text = string.Empty;

        _cts = new CancellationTokenSource();
        var progress = new Progress<DvdMp4Progress>(p =>
        {
            ConvertProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            ProgressPercentText.Text = $"{p.Percent}%";
            ProgressStatusText.Text = p.Message;
            var details = new List<string>();
            if (p.Fps > 0) details.Add($"{p.Fps:0} fps");
            if (p.Speed > 0) details.Add($"{p.Speed:0.0}×");
            if (p.TimeRemaining > TimeSpan.Zero) details.Add($"ETA {FormatHelper.FormatDuration(p.TimeRemaining)}");
            ProgressDetailText.Text = string.Join(" • ", details);
        });

        try
        {
            await DvdVideoConverter.ConvertAsync(source, destination, info, options, progress, _cts.Token);
            ConvertProgressBar.Value = 100;
            ProgressPercentText.Text = "100%";
            ProgressStatusText.Text = "Conversion complete.";
            ProgressDetailText.Text = $"Saved {Path.GetFileName(destination)}";
            OpenMp4Button.Visibility = Visibility.Visible;
            ShowInFolderButton.Visibility = Visibility.Visible;
            ShowStatus(InfoBarSeverity.Success, "MP4 Created", $"Saved '{Path.GetFileName(destination)}'.");
        }
        catch (OperationCanceledException)
        {
            ProgressStatusText.Text = "Conversion cancelled.";
            ShowStatus(InfoBarSeverity.Warning, "Cancelled", "DVD to MP4 conversion was cancelled.");
        }
        catch (Exception ex)
        {
            ProgressStatusText.Text = "Conversion failed.";
            ShowStatus(InfoBarSeverity.Error, "Conversion Failed", ex.Message);
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
        CancelButton.IsEnabled = false;
    }

    private void OpenMp4Button_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOutput) || !File.Exists(_lastOutput)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_lastOutput) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Cannot Open MP4", ex.Message);
        }
    }

    private void ShowInFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOutput) || !File.Exists(_lastOutput)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Cannot Open Folder", ex.Message);
        }
    }

    private void SetBusyUi(bool busy)
    {
        ConvertButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        SourcePathBox.IsEnabled = !busy;
        DestinationPathBox.IsEnabled = !busy;
        BrowseIsoButton.IsEnabled = !busy;
        BrowseFolderButton.IsEnabled = !busy;
        BrowseDestButton.IsEnabled = !busy;
        AudioComboBox.IsEnabled = !busy;
        SubtitleComboBox.IsEnabled = !busy;
        QualityComboBox.IsEnabled = !busy;
        StereoDownmixCheckBox.IsEnabled = !busy;
        DeinterlaceCheckBox.IsEnabled = !busy;
        TitleNumberBox.IsEnabled = !busy;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}

