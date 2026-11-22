using CDBurner10.App.Services;
using CDBurner10.App.ViewModels;
using CDBurner10.Core.Audio;
using CDBurner10.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace CDBurner10.App.Views;

public sealed partial class AudioCompilationPage : Page
{
    public AudioCompilationViewModel ViewModel { get; } = new();
    private readonly DispatcherTimer _playerTimer;

    public AudioCompilationPage()
    {
        InitializeComponent();
        ViewModel.TotalDurationChanged += (_, seconds) => CapacityBar.UsedSeconds = seconds;
        CapacityBar.UsedSeconds = ViewModel.Compilation.TotalDuration.TotalSeconds;

        var savedPath = AppServices.Current.Settings.Current.AudioExplorerPath;
        if (!string.IsNullOrWhiteSpace(savedPath) && Directory.Exists(savedPath))
            FileBrowser.NavigateTo(savedPath);
        else
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (Directory.Exists(music)) FileBrowser.NavigateTo(music);
        }

        FileBrowser.CurrentPathChanged += (_, path) =>
        {
            AppServices.Current.Settings.Current.AudioExplorerPath = path;
        };

        ReplayGainComboBox.SelectedIndex = (int)AppServices.Current.Settings.Current.AudioReplayGain;

        _playerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playerTimer.Tick += (_, _) =>
        {
            ViewModel.UpdatePlayerPosition();
            UpdatePlayPauseIcon();
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppServices.Current.ActiveCompilationMode = "Audio";
        AppServices.Current.FilesDroppedForAudio += OnExternalFilesDropped;
        ViewModel.Player.StateChanged += OnPlayerStateChanged;
        ViewModel.RefreshTracks();
        _playerTimer.Start();

        if (e.Parameter is string filePath && File.Exists(filePath))
        {
            if (filePath.EndsWith(".axp", StringComparison.OrdinalIgnoreCase))
            {
                var errors = ViewModel.LoadAxp(filePath);
                if (errors.Length > 0 && XamlRoot is not null)
                    await AppServices.Current.Dialogs.ShowFileAddErrorsDialogAsync(XamlRoot, errors);
            }
            else
            {
                await AddPathsAndReportErrorsAsync([filePath]);
            }
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _playerTimer.Stop();
        AppServices.Current.FilesDroppedForAudio -= OnExternalFilesDropped;
        ViewModel.Player.StateChanged -= OnPlayerStateChanged;
    }

    private void OnExternalFilesDropped(object? sender, IReadOnlyList<string> paths)
    {
        DispatcherQueue.TryEnqueue(async () => await AddPathsAndReportErrorsAsync(paths));
    }

    private void OnPlayerStateChanged(object? sender, AudioPlaybackState state)
    {
        DispatcherQueue.TryEnqueue(UpdatePlayPauseIcon);
    }

    private void UpdatePlayPauseIcon()
    {
        PlayPauseIcon.Glyph = ViewModel.Player.State == AudioPlaybackState.Playing ? "\uE769" : "\uE768";
    }

    private async Task AddPathsAndReportErrorsAsync(IReadOnlyList<string> paths)
    {
        var errors = ViewModel.AddPaths(paths);
        if (errors.Count > 0 && XamlRoot is not null)
            await AppServices.Current.Dialogs.ShowFileAddErrorsDialogAsync(XamlRoot, errors);
    }

    private async void FileBrowser_ItemsAddRequested(object? sender, IReadOnlyList<string> paths)
    {
        await AddPathsAndReportErrorsAsync(paths);
    }

    private void TracksListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.HasSelection = TracksListView.SelectedItems.Count > 0;
    }

    private void TracksListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TracksListView.SelectedItem is AudioTrackItem item)
        {
            ViewModel.PlayTrack(item);
            UpdatePlayPauseIcon();
        }
    }

    private void TrackList_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to Audio CD";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void TrackList_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count > 0)
            await AddPathsAndReportErrorsAsync(paths);
    }

    private async void AddAudioFiles_Click(object sender, RoutedEventArgs e)
    {
        var exts = BassAudioEngine.GetSupportedExtensions().ToList();
        var files = await FilePickerHelper.PickFilesAsync(exts, multiple: true, startLocation: PickerLocationId.MusicLibrary);
        if (files.Count > 0)
            await AddPathsAndReportErrorsAsync(files);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await FilePickerHelper.PickFolderAsync(PickerLocationId.MusicLibrary);
        if (!string.IsNullOrEmpty(folder))
            await AddPathsAndReportErrorsAsync([folder]);
    }

    private async void ImportPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var file = await FilePickerHelper.PickSingleFileAsync([".m3u", ".m3u8", ".wpl", ".nra", ".cue"], PickerLocationId.MusicLibrary);
        if (!string.IsNullOrEmpty(file))
            await AddPathsAndReportErrorsAsync([file]);
    }

    private async void SplitCue_Click(object sender, RoutedEventArgs e)
    {
        var splitFiles = await AppServices.Current.Dialogs.ShowSplitCueDialogAsync(XamlRoot);
        if (splitFiles is { Count: > 0 })
            await AddPathsAndReportErrorsAsync(splitFiles);
    }

    private async void RipCd_Click(object sender, RoutedEventArgs e)
    {
        var ripped = await AppServices.Current.Dialogs.ShowRipAudioCdDialogAsync(XamlRoot, AppServices.Current.SelectedDrive);
        if (ripped is { Count: > 0 })
            await AddPathsAndReportErrorsAsync(ripped);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        int idx = TracksListView.SelectedIndex;
        if (idx > 0)
        {
            ViewModel.MoveTrackUp(idx);
            TracksListView.SelectedIndex = idx - 1;
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        int idx = TracksListView.SelectedIndex;
        if (idx >= 0 && idx < ViewModel.Tracks.Count - 1)
        {
            ViewModel.MoveTrackDown(idx);
            TracksListView.SelectedIndex = idx + 1;
        }
    }

    private void Randomize_Click(object sender, RoutedEventArgs e) => ViewModel.RandomizeOrder();

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = TracksListView.SelectedItems.OfType<AudioTrackItem>().ToList();
        if (selected.Count > 0)
            ViewModel.RemoveTracks(selected);
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Tracks.Count == 0) return;
        if (await AppServices.Current.Dialogs.ConfirmAsync(XamlRoot, "Clear Audio Compilation", "Remove all audio tracks?", "Clear"))
            ViewModel.ClearAll();
    }

    private async void TrackProperties_Click(object sender, RoutedEventArgs e)
    {
        var selected = (TracksListView.SelectedItem as AudioTrackItem)?.Model;
        if (await AppServices.Current.Dialogs.ShowCdTextAndGapsDialogAsync(XamlRoot, ViewModel.Compilation, selected))
            ViewModel.RefreshTracks();
    }

    private void ReplayGainComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReplayGainComboBox.SelectedIndex >= 0)
        {
            AppServices.Current.Settings.Current.AudioReplayGain = (ReplayGainMode)ReplayGainComboBox.SelectedIndex;
            AppServices.Current.Settings.Save();
        }
    }

    private async void OpenAxp_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSingleFileAsync([".axp"], PickerLocationId.MusicLibrary);
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var errors = ViewModel.LoadAxp(path);
            if (errors.Length > 0)
                await AppServices.Current.Dialogs.ShowFileAddErrorsDialogAsync(XamlRoot, errors);
        }
        catch (Exception ex)
        {
            await AppServices.Current.Dialogs.ShowMessageAsync(XamlRoot, "Open Audio Compilation Failed", ex.Message);
        }
    }

    private async void SaveAxp_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSaveFileAsync(
            string.IsNullOrWhiteSpace(ViewModel.Compilation.Title) ? "AudioCD" : ViewModel.Compilation.Title,
            new Dictionary<string, IList<string>> { ["CDBurnerXP Audio Compilation"] = [".axp"] },
            PickerLocationId.MusicLibrary);
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            ViewModel.SaveAxp(path);
        }
        catch (Exception ex)
        {
            await AppServices.Current.Dialogs.ShowMessageAsync(XamlRoot, "Save Failed", ex.Message);
        }
    }

    private async void ExportPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickSaveFileAsync(
            string.IsNullOrWhiteSpace(ViewModel.Compilation.Title) ? "AudioCD" : ViewModel.Compilation.Title,
            new Dictionary<string, IList<string>>
            {
                ["CUE Sheet"] = [".cue"],
                ["M3U8 Playlist"] = [".m3u8"],
                ["M3U Playlist"] = [".m3u"]
            },
            PickerLocationId.MusicLibrary);
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (path.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
                ViewModel.Compilation.SaveAsCue(path);
            else
                ViewModel.Compilation.SaveAsM3u(path);
        }
        catch (Exception ex)
        {
            await AppServices.Current.Dialogs.ShowMessageAsync(XamlRoot, "Export Failed", ex.Message);
        }
    }

    private async void ExportTracks_Click(object sender, RoutedEventArgs e)
    {
        var tracks = TracksListView.SelectedItems.OfType<AudioTrackItem>().ToList();
        if (tracks.Count == 0)
            tracks = ViewModel.Tracks.ToList();
        if (tracks.Count == 0)
        {
            await AppServices.Current.Dialogs.ShowMessageAsync(XamlRoot, "No Tracks", "Add or rip audio tracks first.");
            return;
        }

        var folder = await FilePickerHelper.PickFolderAsync(PickerLocationId.MusicLibrary);
        if (string.IsNullOrWhiteSpace(folder)) return;

        int exported = 0;
        var errors = new List<string>();
        foreach (var item in tracks)
        {
            var src = item.Model.FileName;
            if (!File.Exists(src))
            {
                errors.Add($"{Path.GetFileName(src)}: file not found.");
                continue;
            }

            try
            {
                string ext = Path.GetExtension(src);
                string title = string.IsNullOrWhiteSpace(item.Title)
                    ? Path.GetFileNameWithoutExtension(src)
                    : item.Title;
                string baseName = CDBurner10.Core.Utilities.PathHelper.ReplaceInvalidFileNameChars($"{item.TrackNumber:D2} - {title}");
                string dest = Path.Combine(folder, baseName + ext);
                int suffix = 1;
                while (File.Exists(dest))
                {
                    dest = Path.Combine(folder, $"{baseName}_{suffix}{ext}");
                    suffix++;
                }

                await Task.Run(() => File.Copy(src, dest));
                exported++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(src)}: {ex.Message}");
            }
        }

        string summary = $"Exported {exported} track(s) to '{folder}'.";
        if (errors.Count > 0)
            summary += $"\n\n{errors.Count} failed:\n" + string.Join("\n", errors.Take(5));

        await AppServices.Current.Dialogs.ShowMessageAsync(XamlRoot, "Export Tracks", summary);
    }

    private async void BurnAudioCd_Click(object sender, RoutedEventArgs e)
    {
        await AppServices.Current.Dialogs.ShowBurnAudioDialogAsync(XamlRoot, ViewModel.Compilation, AppServices.Current.SelectedDrive);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Player.State == AudioPlaybackState.Stopped)
        {
            var target = TracksListView.SelectedItem as AudioTrackItem ?? ViewModel.Tracks.FirstOrDefault();
            if (target is not null)
                ViewModel.PlayTrack(target);
        }
        else
        {
            ViewModel.Player.TogglePlayPause();
        }
        UpdatePlayPauseIcon();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Player.Stop();
        ViewModel.UpdatePlayerPosition();
        UpdatePlayPauseIcon();
    }

    private void SeekSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.Player.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
    }
}
