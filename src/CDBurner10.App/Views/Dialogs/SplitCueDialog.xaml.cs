using CDBurner10.App.Services;
using CDBurner10.Core.Audio;
using CDBurner10.Core.Audio.Cue;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Utilities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CDBurner10.App.Views.Dialogs;

public sealed record CueTrackRow(string NumberText, string DisplayText, string StartTimeText);

public sealed partial class SplitCueDialog : ContentDialog
{
    private CueSheet? _cueSheet;
    private string _cueFilePath = string.Empty;
    public IReadOnlyList<string> OutputFiles { get; private set; } = [];

    public SplitCueDialog()
    {
        InitializeComponent();
        IsPrimaryButtonEnabled = false;
    }

    private async void BrowseCueButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSingleFileAsync([".cue"]);
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            _cueFilePath = path;
            _cueSheet = CueSheet.LoadFromFile(path);
            CuePathBox.Text = path;
            if (string.IsNullOrWhiteSpace(OutputFolderBox.Text))
            {
                OutputFolderBox.Text = Path.Combine(
                    Path.GetDirectoryName(path) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                    Path.GetFileNameWithoutExtension(path) + "_Split");
            }

            var rows = _cueSheet.Tracks.Select(t =>
            {
                var idx = t.GetIndex(1) ?? t.Indices.FirstOrDefault();
                var title = string.IsNullOrWhiteSpace(t.Title) ? $"Track {t.TrackNumber:D2}" : t.Title;
                var display = string.IsNullOrWhiteSpace(t.Performer) ? title : $"{t.Performer} - {title}";
                return new CueTrackRow($"{t.TrackNumber:D2}", display, idx.FormatMsf());
            }).ToList();

            CueTracksListView.ItemsSource = rows;
            TrackPreviewHeader.Text = $"Tracks Preview ({rows.Count} track(s))";
            IsPrimaryButtonEnabled = rows.Count > 0;
            StatusInfoBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Invalid CUE Sheet";
            StatusInfoBar.Message = ex.Message;
            StatusInfoBar.IsOpen = true;
            IsPrimaryButtonEnabled = false;
        }
    }

    private async void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await FilePickerHelper.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            OutputFolderBox.Text = folder;
        }
    }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_cueSheet is null || _cueSheet.Tracks.Count == 0) return;

        var deferral = args.GetDeferral();
        IsPrimaryButtonEnabled = false;
        SplitProgressBar.Visibility = Visibility.Visible;
        SplitProgressBar.Value = 0;

        using var op = AppServices.Current.Operations.Begin(
            Loc.T("Split CUE Sheet"),
            $"Splitting '{Path.GetFileName(_cueFilePath)}' ({_cueSheet.Tracks.Count} track(s))...");

        try
        {
            var outDir = OutputFolderBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(outDir))
                outDir = AppServices.Current.Settings.GetTempRipDirectory();
            Directory.CreateDirectory(outDir);

            var cueDir = Path.GetDirectoryName(_cueFilePath) ?? string.Empty;
            var firstTrack = _cueSheet.Tracks[0];
            string audioPath = firstTrack.DataFileName;
            if (!Path.IsPathRooted(audioPath))
                audioPath = Path.Combine(cueDir, audioPath);

            if (!File.Exists(audioPath))
                throw new FileNotFoundException($"Referenced audio file not found: '{audioPath}'", audioPath);

            double[] splitPositions = _cueSheet.Tracks
                .Select(t => (t.GetIndex(1) ?? t.Indices.FirstOrDefault()).TotalSeconds)
                .ToArray();

            var progress = new Progress<double>(p =>
            {
                double clamped = Math.Clamp(p, 0, 100);
                SplitProgressBar.Value = clamped;
                op.Report(clamped, BurnPhase.Converting);
            });
            var created = await Task.Run(() =>
            {
                using var converter = new CrossfadingAudioConverter { TargetSampleRate = 44100 };
                var tempWavs = converter.ToWav(audioPath, splitPositions, 0f, 0f, progress);
                var finalFiles = new List<string>(tempWavs.Length);
                for (int i = 0; i < tempWavs.Length; i++)
                {
                    var track = i < _cueSheet.Tracks.Count ? _cueSheet.Tracks[i] : null;
                    string baseName = track is not null && !string.IsNullOrWhiteSpace(track.Title)
                        ? $"{track.TrackNumber:D2} - {PathHelper.ReplaceInvalidFileNameChars(track.Title)}"
                        : $"Track_{i + 1:D2}";
                    string dest = Path.Combine(outDir, baseName + ".wav");
                    File.Move(tempWavs[i], dest, overwrite: true);
                    finalFiles.Add(dest);
                }
                return finalFiles;
            });

            OutputFiles = created;
            op.Complete($"Created {created.Count} WAV track(s).");
        }
        catch (OperationCanceledException)
        {
            op.Cancel();
            args.Cancel = true;
            IsPrimaryButtonEnabled = true;
        }
        catch (Exception ex)
        {
            op.Fail(ex.Message);
            args.Cancel = true;
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "CUE Split Failed";
            StatusInfoBar.Message = ex.Message;
            StatusInfoBar.IsOpen = true;
            IsPrimaryButtonEnabled = true;
        }
        finally
        {
            SplitProgressBar.Visibility = Visibility.Collapsed;
            deferral.Complete();
        }
    }
}
