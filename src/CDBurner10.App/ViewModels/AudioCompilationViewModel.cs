using System.Collections.ObjectModel;
using CDBurner10.App.Converters;
using CDBurner10.App.Services;
using CDBurner10.Core.Audio;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using CoreAudioTrack = CDBurner10.Core.Burning.AudioTrackItem;

namespace CDBurner10.App.ViewModels;

public sealed class AudioTrackItem
{
    public CoreAudioTrack Model { get; init; } = null!;
    public int TrackNumber { get; set; }
    public string Title => string.IsNullOrWhiteSpace(Model.CDTextTitle) ? Model.Title : Model.CDTextTitle;
    public string Artist => string.IsNullOrWhiteSpace(Model.CDTextArtist) ? Model.Artist : Model.CDTextArtist;
    public string Album => Model.Album;
    public string DurationDisplay => FormatHelper.FormatDuration(Model.Duration);
    public string BitrateDisplay => Model.BitrateKbps > 0 ? $"{Model.BitrateKbps} kbps" : "—";
    public string Format => string.IsNullOrWhiteSpace(Model.Format) ? Path.GetExtension(Model.FileName).TrimStart('.').ToUpperInvariant() : Model.Format;
    public string GapsDisplay => $"{Model.PreGap.TotalSeconds:0.#}s / {Model.PostGap.TotalSeconds:0.#}s";
    public string FilePath => Model.FileName;
}

public sealed partial class AudioCompilationViewModel : ObservableObject
{
    public AudioCompilation Compilation => AppServices.Current.ActiveAudioCompilation;
    public AudioPlayerService Player => AppServices.Current.AudioPlayer;

    public ObservableCollection<AudioTrackItem> Tracks { get; } = [];

    [ObservableProperty]
    private string _compilationSubtitle = "0 tracks • 00:00";

    [ObservableProperty]
    private Visibility _emptyTracksVisibility = Visibility.Visible;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private string _nowPlayingTitle = "No track playing";

    [ObservableProperty]
    private string _nowPlayingArtist = "Double-click a track to preview";

    [ObservableProperty]
    private string _elapsedDisplay = "00:00";

    [ObservableProperty]
    private string _remainingDisplay = "-00:00";

    [ObservableProperty]
    private double _playerDurationSeconds = 1;

    [ObservableProperty]
    private double _playerPositionSeconds;

    public event EventHandler<double>? TotalDurationChanged;

    public AudioCompilationViewModel()
    {
        RefreshTracks();
    }

    public void RefreshTracks()
    {
        Tracks.Clear();
        for (int i = 0; i < Compilation.Tracks.Count; i++)
        {
            Tracks.Add(new AudioTrackItem
            {
                Model = Compilation.Tracks[i],
                TrackNumber = i + 1
            });
        }

        EmptyTracksVisibility = Tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var total = Compilation.TotalDuration;
        CompilationSubtitle = $"{Tracks.Count} track(s) • {FormatHelper.FormatDuration(total)}";
        TotalDurationChanged?.Invoke(this, total.TotalSeconds);
    }

    public List<ItemAddError> AddPaths(IEnumerable<string> paths)
    {
        var errors = new List<ItemAddError>();
        bool autoCdText = AppServices.Current.Settings.Current.AudioAutoCDText;

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                                                  .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        if (BassAudioEngine.IsAudioFile(file))
                            TryAddAudioFile(file, autoCdText, errors);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(new ItemAddError(path, ex.Message, ex));
                }
            }
            else if (File.Exists(path))
            {
                if (PlaylistParser.IsSupportedPlaylist(path))
                {
                    errors.AddRange(Compilation.ImportPlaylist(path, autoCdText));
                }
                else
                {
                    TryAddAudioFile(path, autoCdText, errors);
                }
            }
        }

        RefreshTracks();
        return errors;
    }

    private void TryAddAudioFile(string filePath, bool autoCdText, List<ItemAddError> errors)
    {
        try
        {
            Compilation.Tracks.Add(CoreAudioTrack.FromFile(filePath, autoCdText));
        }
        catch (Exception ex)
        {
            errors.Add(new ItemAddError(filePath, ex.Message, ex));
        }
    }

    public void RemoveTracks(IEnumerable<AudioTrackItem> items)
    {
        foreach (var item in items.ToList())
            Compilation.Tracks.Remove(item.Model);
        RefreshTracks();
    }

    public void MoveTrackUp(int index)
    {
        if (index <= 0 || index >= Compilation.Tracks.Count) return;
        (Compilation.Tracks[index - 1], Compilation.Tracks[index]) = (Compilation.Tracks[index], Compilation.Tracks[index - 1]);
        RefreshTracks();
    }

    public void MoveTrackDown(int index)
    {
        if (index < 0 || index >= Compilation.Tracks.Count - 1) return;
        (Compilation.Tracks[index + 1], Compilation.Tracks[index]) = (Compilation.Tracks[index], Compilation.Tracks[index + 1]);
        RefreshTracks();
    }

    public void RandomizeOrder()
    {
        var rng = Random.Shared;
        for (int i = Compilation.Tracks.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (Compilation.Tracks[i], Compilation.Tracks[j]) = (Compilation.Tracks[j], Compilation.Tracks[i]);
        }
        RefreshTracks();
    }

    public void SyncOrderFromUi()
    {
        Compilation.Tracks.Clear();
        foreach (var item in Tracks)
            Compilation.Tracks.Add(item.Model);
        RefreshTracks();
    }

    public void ClearAll()
    {
        Compilation.Tracks.Clear();
        RefreshTracks();
    }

    public ItemAddError[] LoadAxp(string axpPath)
    {
        var errors = Compilation.Load(axpPath);
        RefreshTracks();
        return errors;
    }

    public void SaveAxp(string axpPath) => Compilation.Save(axpPath);

    public void PlayTrack(AudioTrackItem track)
    {
        if (Player.Play(track.FilePath))
        {
            NowPlayingTitle = string.IsNullOrWhiteSpace(track.Title) ? Path.GetFileNameWithoutExtension(track.FilePath) : track.Title;
            NowPlayingArtist = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown Artist" : track.Artist;
            UpdatePlayerPosition();
        }
    }

    public void UpdatePlayerPosition()
    {
        Player.PollPlaybackState();
        var dur = Player.Duration;
        var pos = Player.Position;
        PlayerDurationSeconds = Math.Max(1, dur.TotalSeconds);
        PlayerPositionSeconds = Math.Clamp(pos.TotalSeconds, 0, PlayerDurationSeconds);
        ElapsedDisplay = FormatHelper.FormatDuration(pos);
        var rem = dur > pos ? dur - pos : TimeSpan.Zero;
        RemainingDisplay = $"-{FormatHelper.FormatDuration(rem)}";
    }
}
