using System.Diagnostics;
using System.Text;
using CDBurner10.Core.Dvd;
using CDBurner10.Core.Utilities;
using CDBurner10.Upscaler.Engine;
using CDBurner10.Upscaler.Services;
using CDBurner10.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace CDBurner10.Upscaler.Views;

public sealed partial class UpscalePage : Page
{
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _probeCts;
    private VideoSourceInfo? _sourceInfo;
    private bool _isBusy;
    private bool _initialized;
    private string? _autoOutputFolder;
    private string _lastOutputFile = string.Empty;

    public UpscalePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += UpscalePage_Loaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_isBusy && e.Parameter is string path && IsSupportedVideoFile(path))
        {
            SourcePathBox.Text = path;
        }
    }

    private void UpscalePage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        var s = UpscalerAppServices.Current.Settings.Current;

        PresetComboBox.ItemsSource = UpscalerPresets.All;
        var savedPreset = UpscalerPresets.FindById(s.LastPresetId);
        PresetComboBox.SelectedItem = savedPreset;

        SelectComboByTag(ScaleComboBox, (s.ScaleFactor is 4 ? 4 : 2).ToString());
        SelectComboByTag(OutputFormatComboBox,
            string.Equals(s.LastOutputFormat, "mkv", StringComparison.OrdinalIgnoreCase) ? "mkv" : "mp4");

        if (!string.IsNullOrWhiteSpace(s.DefaultOutputFolder) && string.IsNullOrWhiteSpace(OutputFolderBox.Text))
            OutputFolderBox.Text = s.DefaultOutputFolder;

        UpdatePresetSpecCard();
        UpdateOutputPreview();
    }

    private UpscalerPreset SelectedPreset =>
        PresetComboBox.SelectedItem as UpscalerPreset ?? UpscalerPresets.All[0];

    private string SelectedFormat =>
        ((OutputFormatComboBox.SelectedItem as ComboBoxItem)?.Tag as string) ?? "mp4";

    private int SelectedScale =>
        int.TryParse((ScaleComboBox.SelectedItem as ComboBoxItem)?.Tag as string, out int sc) && sc == 4 ? 4 : 2;

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
                if (item is StorageFile file &&
                    !string.IsNullOrWhiteSpace(file.Path) &&
                    IsSupportedVideoFile(file.Path))
                {
                    SourcePathBox.Text = file.Path;
                    return;
                }
            }

            ShowStatus(InfoBarSeverity.Warning, "Unsupported File", "Drop an .mp4 or .mkv video file.");
        }
        catch
        {
        }
    }

    private async void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSingleFileAsync([".mp4", ".mkv"]);
        if (!string.IsNullOrWhiteSpace(path))
            SourcePathBox.Text = path;
    }

    private async void BrowseOutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await FilePickerHelper.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            OutputFolderBox.Text = folder;
            _autoOutputFolder = null;
        }
    }

    private async void SourcePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDefaultOutputFolder();
        UpdateOutputPreview();

        if (_isBusy) return;

        string path = SourcePathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            _sourceInfo = null;
            SourceInfoText.Text = "Select an .mp4 or .mkv video to inspect resolution, framerate, and audio streams.";
            return;
        }

        await Task.Delay(300);
        if ((SourcePathBox.Text?.Trim() ?? string.Empty) != path) return;

        await ProbeSourceAsync(path);
    }

    private async Task<VideoSourceInfo?> ProbeSourceAsync(string path)
    {
        var cts = new CancellationTokenSource();
        var prev = _probeCts;
        _probeCts = cts;
        prev?.Cancel();

        try
        {
            SourceInfoText.Text = "Inspecting video stream with ffprobe...";
            var info = await VideoProbeService.ProbeAsync(path, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_probeCts, cts))
                return null;

            _sourceInfo = info;
            UpdateSourceInfoLabel();
            if (StatusInfoBar.Title == "Cannot Read Video")
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
                _sourceInfo = null;
                SourceInfoText.Text = "Could not inspect video file.";
                ShowStatus(InfoBarSeverity.Error, "Cannot Read Video", ex.Message);
            }

            return null;
        }
        finally
        {
            if (ReferenceEquals(_probeCts, cts))
                _probeCts = null;
        }
    }

    private void UpdateSourceInfoLabel()
    {
        if (_sourceInfo is null) return;
        int scale = SelectedScale;
        int outW = _sourceInfo.Width * scale;
        int outH = _sourceInfo.Height * scale;
        string audio = _sourceInfo.HasAudio
            ? $"{_sourceInfo.AudioCodec?.ToUpperInvariant() ?? "Audio"} ({_sourceInfo.AudioChannels} ch)"
            : "No audio";

        SourceInfoText.Text =
            $"{_sourceInfo.ResolutionLabel} → {outW}×{outH} ({scale}×) • {_sourceInfo.FpsLabel} • " +
            $"{FileSizeFormatter.FormatDuration(_sourceInfo.Duration)} (~{_sourceInfo.EstimatedFrames:N0} frames) • " +
            $"{_sourceInfo.VideoCodec.ToUpperInvariant()} • {audio} • {FileSizeFormatter.Format(_sourceInfo.FileSizeBytes)}";
    }

    private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        UpscalerAppServices.Current.Settings.Current.LastPresetId = SelectedPreset.Id;
        UpscalerAppServices.Current.Settings.Save();
        UpdatePresetSpecCard();
        UpdateOutputPreview();
    }

    private void ScaleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        UpscalerAppServices.Current.Settings.Current.ScaleFactor = SelectedScale;
        UpscalerAppServices.Current.Settings.Save();
        UpdateSourceInfoLabel();
    }

    private void OutputFormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        UpscalerAppServices.Current.Settings.Current.LastOutputFormat = SelectedFormat;
        UpscalerAppServices.Current.Settings.Save();
        UpdateOutputPreview();
    }

    private void OutputFolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateOutputPreview();
    }

    private void UpdatePresetSpecCard()
    {
        var p = SelectedPreset;
        SpecArchText.Text = p.Architecture;
        SpecLayersText.Text = p.ResidualBlocksOrLayers;
        SpecFiltersText.Text = p.Filters;
        SpecParamsText.Text = p.ParameterCount;
        SpecBackendText.Text = p.BackendLabel;
        SpecUsageText.Text = $"Recommended usage: {p.RecommendedUsage}";
    }

    private void UpdateDefaultOutputFolder()
    {
        var s = UpscalerAppServices.Current.Settings.Current;
        if (!string.IsNullOrWhiteSpace(s.DefaultOutputFolder) &&
            (string.IsNullOrWhiteSpace(OutputFolderBox.Text) || OutputFolderBox.Text == _autoOutputFolder))
        {
            OutputFolderBox.Text = s.DefaultOutputFolder;
            _autoOutputFolder = s.DefaultOutputFolder;
            return;
        }

        string src = SourcePathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(src)) return;

        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(src));
            if (!string.IsNullOrWhiteSpace(dir) &&
                (string.IsNullOrWhiteSpace(OutputFolderBox.Text) || OutputFolderBox.Text == _autoOutputFolder))
            {
                OutputFolderBox.Text = dir;
                _autoOutputFolder = dir;
            }
        }
        catch
        {
        }
    }

    private void UpdateOutputPreview()
    {
        string src = SourcePathBox.Text?.Trim() ?? string.Empty;
        string folder = OutputFolderBox.Text?.Trim() ?? string.Empty;
        var preset = SelectedPreset;
        string fmt = SelectedFormat;

        if (string.IsNullOrWhiteSpace(src))
        {
            OutputPreviewText.Text = $"Output filename pattern: {preset.FilePrefix}_<video>.{fmt}";
            return;
        }

        string baseName = Path.GetFileNameWithoutExtension(src);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "video";
        string fileName = $"{preset.FilePrefix}_{baseName}.{fmt}";
        OutputPreviewText.Text = string.IsNullOrWhiteSpace(folder)
            ? $"Output filename: {fileName}"
            : $"Output path: {Path.Combine(folder, fileName)}";
    }

    private async void UpscaleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        await Task.Run(() => UpscalerAppServices.Current.RefreshEngineStatus());
        if (!DvdVideoConverter.IsAvailable)
        {
            ShowStatus(InfoBarSeverity.Error, "ffmpeg Not Found", DvdVideoConverter.InstallHint);
            return;
        }

        var preset = SelectedPreset;
        if (preset.Backend == UpscalerBackendKind.MpvGlslShader && !UpscalerAppServices.Current.IsLibMpvAvailable)
        {
            ShowStatus(
                InfoBarSeverity.Error,
                "libmpv-2.dll Not Found",
                "libmpv-2.dll is required for GLSL shader presets. Open Settings to configure or download libmpv-2.dll.");
            return;
        }

        string sourcePath = SourcePathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            ShowStatus(InfoBarSeverity.Warning, "Source Missing",
                "Select or drop an existing .mp4 or .mkv video file.");
            return;
        }

        if (!IsSupportedVideoFile(sourcePath))
        {
            ShowStatus(InfoBarSeverity.Warning, "Unsupported Input", "Input file must be an .mp4 or .mkv video.");
            return;
        }

        string outputFolder = OutputFolderBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            ShowStatus(InfoBarSeverity.Warning, "Output Folder Missing",
                "Select an output folder for the upscaled video.");
            return;
        }

        VideoSourceInfo? info = _sourceInfo;
        if (info is null || !string.Equals(info.FilePath, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            info = await ProbeSourceAsync(sourcePath);
            if (info is null) return;
        }

        string outputFormat = SelectedFormat;
        var settings = UpscalerAppServices.Current.Settings.Current;
        string expectedOutput = UpscalerPresets.BuildOutputFilePath(sourcePath, preset, outputFolder, outputFormat);

        _isBusy = true;
        SetBusyUi(true);
        _lastOutputFile = expectedOutput;
        OpenVideoButton.Visibility = Visibility.Collapsed;
        ShowInFolderButton.Visibility = Visibility.Collapsed;
        ProgressCard.Visibility = Visibility.Visible;
        UpscaleProgressBar.Value = 0;
        ProgressPercentText.Text = "0%";
        ProgressStatusText.Text = $"Upscaling {Path.GetFileName(sourcePath)} with {preset.FilePrefix}...";
        ProgressDetailText.Text = string.Empty;
        EventLogBox.Text = string.Empty;

        var log = new StringBuilder();

        void AppendLog(string msg)
        {
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            EventLogBox.Text = log.ToString();
            DiagnosticLogger.Info("Upscaler", msg);
        }

        AppendLog(
            $"Source: {sourcePath} ({info.ResolutionLabel}, {info.FpsLabel}, {FileSizeFormatter.FormatDuration(info.Duration)})");
        AppendLog($"Preset: {preset.DisplayName} ({preset.BackendLabel})");
        AppendLog($"Output: {expectedOutput}");

        int lastLoggedMilestone = -1;
        var watch = Stopwatch.StartNew();
        _cts = new CancellationTokenSource();

        using var op = UpscalerAppServices.Current.Operations.Begin(
            $"Upscale ({preset.FilePrefix})",
            $"Upscaling '{Path.GetFileName(sourcePath)}' to '{Path.GetFileName(expectedOutput)}'...");

        var progress = new Progress<UpscaleProgress>(p =>
        {
            if (!string.IsNullOrWhiteSpace(p.LogLine))
                AppendLog(p.LogLine);

            if (p.Percent < 0) return;

            UpscaleProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            ProgressPercentText.Text = $"{p.Percent}%";
            ProgressStatusText.Text = p.Message;
            op.Report(p.Percent, p.TimeRemaining);

            var details = new List<string>();
            if (p.TotalFrames > 0) details.Add($"Frame {p.CurrentFrame:N0}/{p.TotalFrames:N0}");
            if (p.Fps > 0) details.Add($"{p.Fps:0.0} fps");
            if (p.Speed > 0) details.Add($"{p.Speed:0.00}×");
            if (p.TimeRemaining > TimeSpan.Zero)
                details.Add($"ETA {FileSizeFormatter.FormatDuration(p.TimeRemaining)}");
            ProgressDetailText.Text = string.Join(" • ", details);

            int milestone = (p.Percent / 10) * 10;
            if (milestone > 0 && milestone > lastLoggedMilestone && milestone < 100)
            {
                lastLoggedMilestone = milestone;
                string extra = details.Count > 0 ? $" ({string.Join(", ", details)})" : string.Empty;
                AppendLog($"Progress: {p.Percent}%{extra}");
            }
        });

        try
        {
            string actualOutput = await Task.Run(
                () => UpscalePipeline.UpscaleAsync(info, preset, outputFolder, outputFormat, settings, progress,
                    _cts.Token),
                _cts.Token);

            _lastOutputFile = actualOutput;
            UpscaleProgressBar.Value = 100;
            ProgressPercentText.Text = "100%";
            ProgressStatusText.Text = "Upscale complete.";
            ProgressDetailText.Text = $"Saved {Path.GetFileName(actualOutput)}";

            long outSize = File.Exists(actualOutput) ? new FileInfo(actualOutput).Length : 0;
            AppendLog(outSize > 0
                ? $"Completed successfully in {FileSizeFormatter.FormatDuration(watch.Elapsed)}. Saved '{Path.GetFileName(actualOutput)}' ({FileSizeFormatter.Format(outSize)})."
                : $"Completed successfully in {FileSizeFormatter.FormatDuration(watch.Elapsed)}. Saved '{Path.GetFileName(actualOutput)}'.");

            OpenVideoButton.Visibility = Visibility.Visible;
            ShowInFolderButton.Visibility = Visibility.Visible;
            ShowStatus(InfoBarSeverity.Success, "Video Upscaled", $"Saved '{Path.GetFileName(actualOutput)}'.");
            op.Complete($"Saved '{Path.GetFileName(actualOutput)}'.");
        }
        catch (OperationCanceledException)
        {
            op.Cancel();
            ProgressStatusText.Text = "Upscale cancelled.";
            AppendLog($"Upscale cancelled by user after {FileSizeFormatter.FormatDuration(watch.Elapsed)}.");
            ShowStatus(InfoBarSeverity.Warning, "Cancelled", "Video upscaling was cancelled.");
        }
        catch (Exception ex)
        {
            op.Fail(ex.Message);
            ProgressStatusText.Text = "Upscale failed.";
            AppendLog($"ERROR after {FileSizeFormatter.FormatDuration(watch.Elapsed)}: {ex.Message}");
            ShowStatus(InfoBarSeverity.Error, "Upscale Failed", ex.Message);
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

    private void OpenVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOutputFile) || !File.Exists(_lastOutputFile)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_lastOutputFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Cannot Open Video", ex.Message);
        }
    }

    private void ShowInFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOutputFile) || !File.Exists(_lastOutputFile)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutputFile}\"")
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Cannot Open Folder", ex.Message);
        }
    }

    private void SetBusyUi(bool busy)
    {
        UpscaleButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        SourcePathBox.IsEnabled = !busy;
        BrowseSourceButton.IsEnabled = !busy;
        PresetComboBox.IsEnabled = !busy;
        ScaleComboBox.IsEnabled = !busy;
        OutputFormatComboBox.IsEnabled = !busy;
        OutputFolderBox.IsEnabled = !busy;
        BrowseOutputFolderButton.IsEnabled = !busy;
    }

    private static bool IsSupportedVideoFile(string path) =>
        path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}