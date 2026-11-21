using System.Runtime.InteropServices;
using CDBurner10.Core.Audio;
using CDBurner10.Core.Settings;
using CDBurner10.Core.Utilities;
using RocketDivision.StarBurnX;

namespace CDBurner10.Core.Burning;

public enum AudioGapMode
{
    TaoPause,
    DaoGapless,
    CustomGaps,
    Crossfade
}

public sealed class AudioBurnOptions
{
    public required DriveModel Drive { get; init; }
    public int WriteSpeedKBps { get; init; } = 0;
    public AudioGapMode GapMode { get; init; } = AudioGapMode.TaoPause;
    public int PauseSeconds { get; init; } = 2;
    public double CrossfadeSeconds { get; init; } = 0;
    public bool EnableCdText { get; init; } = true;
    public ReplayGainMode ReplayGainMode { get; init; } = ReplayGainMode.None;
    public bool FinalizeDisc { get; init; } = true;
    public bool TestBurn { get; init; } = false;
    public bool EjectAfterBurn { get; init; } = true;
    public int Copies { get; init; } = 1;
}

public sealed class AudioBurner
{
    private AudioBurnerClass? _burner;
    private bool _isCancelled;

    public bool IsCancelled => _isCancelled;

    public Task BurnAsync(
        AudioCompilation compilation,
        AudioBurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            _isCancelled = false;
            using var reg = ct.Register(Cancel);

            if (compilation.Tracks.Count == 0)
                throw new InvalidOperationException("No audio tracks in compilation.");

            var tempFiles = new List<string>();
            try
            {
                BassAudioEngine.Initialize();

                // Prepare tracks (convert to 44.1kHz 16-bit stereo WAV if needed)
                var preparedPaths = PrepareTracks(compilation, options, tempFiles, progress, ct);

                int copies = Math.Max(1, options.Copies);
                for (int copy = 1; copy <= copies; copy++)
                {
                    ct.ThrowIfCancellationRequested();

                    var burner = new AudioBurnerClass
                    {
                        Drive = options.Drive.RawDrive,
                        Mode = options.GapMode == AudioGapMode.TaoPause
                            ? STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_TAO
                            : STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_DAO_96,
                        NextSessionAllowed = !options.FinalizeDisc
                    };
                    if (options.FinalizeDisc) burner.KeepSessionOpened = false;

                    if (options.GapMode == AudioGapMode.TaoPause)
                    {
                        burner.AudioPauseLength = Math.Max(0, options.PauseSeconds);
                    }
                    else
                    {
                        burner.AudioPauseLength = 0;
                    }

                    if (options.EnableCdText)
                    {
                        burner.Title = AudioTrackItem.StripToCdTextSize(compilation.Title);
                        burner.Artist = AudioTrackItem.StripToCdTextSize(compilation.Artist);
                    }

                    if (options.WriteSpeedKBps > 0)
                    {
                        try { burner.WriteSpeed = options.WriteSpeedKBps; } catch (COMException) { }
                    }

                    for (int i = 0; i < preparedPaths.Count; i++)
                    {
                        var addedTrack = burner.AddFile(preparedPaths[i]);
                        if (options.EnableCdText && addedTrack is not null)
                        {
                            var src = compilation.Tracks[i];
                            addedTrack.Title = AudioTrackItem.StripToCdTextSize(
                                string.IsNullOrWhiteSpace(src.CDTextTitle) ? src.Title : src.CDTextTitle);
                            addedTrack.Artist = AudioTrackItem.StripToCdTextSize(
                                string.IsNullOrWhiteSpace(src.CDTextArtist) ? src.Artist : src.CDTextArtist);
                        }
                    }

                    int trackCount = burner.Tracks.Count;
                    int tracksCompleted = 0;
                    int lastBuf = 0;
                    burner.OnBuffer += p => lastBuf = p;
                    burner.OnProgress += (percent, timeRemaining) =>
                    {
                        int overall = burner.Mode == STARBURN_WRITE_MODE.STARBURN_WRITE_MODE_TAO && trackCount > 0
                            ? (int)Math.Clamp((tracksCompleted * 100.0 + percent) / trackCount, 0, 100)
                            : percent;

                        if (percent == 100 && tracksCompleted < trackCount - 1)
                        {
                            tracksCompleted++;
                        }

                        progress?.Report(new BurnProgress(
                            BurnPhase.Burning,
                            overall,
                            lastBuf,
                            TimeSpan.FromSeconds(timeRemaining),
                            tracksCompleted + 1,
                            trackCount,
                            $"Burning audio track {tracksCompleted + 1}/{trackCount}... {overall}%"));
                    };
                    burner.OnCloseSessionBegin += () =>
                        progress?.Report(new BurnProgress(BurnPhase.ClosingSession, 99, lastBuf, Message: "Closing audio session..."));

                    _burner = burner;
                    burner.Burn(options.TestBurn);

                    if (options.EjectAfterBurn && !_isCancelled)
                    {
                        try { options.Drive.RawDrive.Eject(); } catch (COMException) { }
                    }
                }

                progress?.Report(new BurnProgress(
                    _isCancelled ? BurnPhase.Cancelled : BurnPhase.Completed,
                    100,
                    Message: _isCancelled ? "Audio burn cancelled." : "Audio CD burned successfully."));
            }
            finally
            {
                foreach (var tmp in tempFiles)
                {
                    PathHelper.TryDeleteFile(tmp);
                }
            }
        }, ct);
    }

    private static List<string> PrepareTracks(
        AudioCompilation compilation,
        AudioBurnOptions options,
        List<string> tempFiles,
        IProgress<BurnProgress>? progress,
        CancellationToken ct)
    {
        var result = new List<string>(compilation.Tracks.Count);
        bool useCrossfade = options.GapMode == AudioGapMode.Crossfade && options.CrossfadeSeconds > 0;
        bool useCustomGaps = options.GapMode == AudioGapMode.CustomGaps;

        using var converter = new CrossfadingAudioConverter
        {
            TargetSampleRate = 44100,
            ProcessReplayGain = options.ReplayGainMode,
            CrossfadeLength = useCrossfade ? TimeSpan.FromSeconds(options.CrossfadeSeconds) : TimeSpan.Zero,
            Items = compilation.Tracks.Select(t => t.FileName).ToArray()
        };

        for (int i = 0; i < compilation.Tracks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var track = compilation.Tracks[i];

            bool needsConversion =
                useCrossfade ||
                options.ReplayGainMode != ReplayGainMode.None ||
                (useCustomGaps && (track.PreGap > TimeSpan.Zero || track.PostGap > TimeSpan.Zero)) ||
                track.IsCda ||
                !Path.GetExtension(track.FileName).Equals(".wav", StringComparison.OrdinalIgnoreCase);

            if (!needsConversion)
            {
                result.Add(track.FileName);
                continue;
            }

            int trackIdx = i;
            var trackProg = new Progress<double>(p =>
            {
                int overall = (int)((trackIdx * 100.0 + p) / compilation.Tracks.Count);
                progress?.Report(new BurnProgress(
                    BurnPhase.Converting,
                    overall,
                    CurrentTrack: trackIdx + 1,
                    TotalTracks: compilation.Tracks.Count,
                    Message: $"Converting track {trackIdx + 1}/{compilation.Tracks.Count}: {track.Title}"));
            });

            string wavPath = useCrossfade
                ? converter.NextToWav(trackProg, ct)
                : converter.ToWav(
                    track.FileName,
                    useCustomGaps ? (float)track.PreGap.TotalSeconds : 0f,
                    useCustomGaps ? (float)track.PostGap.TotalSeconds : 0f,
                    trackProg,
                    ct);

            tempFiles.Add(wavPath);
            result.Add(wavPath);
        }

        return result;
    }

    public void Cancel()
    {
        _isCancelled = true;
        _ = Task.Run(() =>
        {
            try { _burner?.Cancel(); } catch { }
        });
    }
}
