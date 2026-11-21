using System.Runtime.InteropServices;
using RocketDivision.StarBurnX;

namespace CDBurner10.Core.Burning;

public sealed record GrabTrackInfo(string FileName, bool IsAudio, int TrackSizeLB);

public sealed class GrabOptions
{
    public required DriveModel Drive { get; init; }
    public required string TargetPath { get; init; }
    public int ReadSpeedKBps { get; init; } = 0;
    public bool IgnoreBadBlocks { get; init; } = false;
    public bool DisableHardwareErrorCorrection { get; init; } = false;
    public int NumberOfReadRetries { get; init; } = 3;
    public bool FastReadToc { get; init; } = false;
    public bool IsDvdVideo { get; init; } = false;
}

public sealed class DiscGrabber
{
    private GrabberClass? _grabber;
    private bool _isCancelled;

    public bool IsCancelled => _isCancelled;

    public Task GrabDiscAsync(
        GrabOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            _isCancelled = false;
            using var reg = ct.Register(Cancel);

            var dir = Path.GetDirectoryName(options.TargetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var grabber = CreateConfiguredGrabber(options);
            grabber.OnProgress += (pct, rem) =>
                progress?.Report(new BurnProgress(BurnPhase.Grabbing, pct, TimeRemaining: TimeSpan.FromSeconds(rem), Message: $"Reading disc... {pct}%"));

            _grabber = grabber;

            if (options.IsDvdVideo)
                grabber.GrabDVDVideoDisc(options.TargetPath);
            else
                grabber.GrabDisc(options.TargetPath);

            progress?.Report(new BurnProgress(
                _isCancelled ? BurnPhase.Cancelled : BurnPhase.Completed,
                100,
                Message: _isCancelled ? "Grab cancelled." : "Disc image saved successfully."));
        }, ct);
    }

    public Task<Dictionary<int, GrabTrackInfo>> GrabMultipleTracksAsync(
        GrabOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            _isCancelled = false;
            using var reg = ct.Register(Cancel);

            Directory.CreateDirectory(options.TargetPath);
            var grabber = CreateConfiguredGrabber(options);
            grabber.FastReadTOC = false;
            grabber.AudioCompression = STARBURN_AUDIO_COMPRESSION.STARBURN_AUDIO_UNCOMPRESSED;
            _grabber = grabber;

            var disc = options.Drive.RawDrive.DiscInfo;
            disc.Refresh();

            int totalTracks = 0;
            for (int s = 0; s < disc.Sessions.Count; s++)
                totalTracks += disc.Sessions[s].Tracks.Count;

            int completed = 0;
            grabber.OnProgress += (pct, rem) =>
            {
                int overall = totalTracks > 0
                    ? (int)Math.Clamp((completed * 100.0 + pct) / totalTracks, 0, 100)
                    : pct;
                progress?.Report(new BurnProgress(
                    BurnPhase.Grabbing,
                    overall,
                    TimeRemaining: TimeSpan.FromSeconds(rem),
                    CurrentTrack: completed + 1,
                    TotalTracks: totalTracks,
                    Message: $"Grabbing track {completed + 1}/{totalTracks}... {overall}%"));
            };

            var tracks = new Dictionary<int, GrabTrackInfo>();
            int trackNum = 1;
            for (int s = 0; s < disc.Sessions.Count; s++)
            {
                var session = disc.Sessions[s];
                for (int t = 0; t < session.Tracks.Count; t++)
                {
                    ct.ThrowIfCancellationRequested();
                    var track = session.Tracks[t];
                    if (track.Audio)
                    {
                        var wavPath = Path.Combine(options.TargetPath, $"Track{trackNum}.wav");
                        grabber.GrabTrackAsAudio(t + 1, wavPath);
                        tracks[trackNum] = new GrabTrackInfo(wavPath, IsAudio: true, track.Size);
                    }
                    else
                    {
                        var isoPath = Path.Combine(options.TargetPath, $"Track{trackNum}.iso");
                        grabber.GrabTrackAsData(t + 1, isoPath);
                        tracks[trackNum] = new GrabTrackInfo(isoPath, IsAudio: false, track.Size);
                    }
                    completed++;
                    trackNum++;
                }
            }

            progress?.Report(new BurnProgress(BurnPhase.Completed, 100, Message: "All tracks grabbed successfully."));
            return tracks;
        }, ct);
    }

    public Task GrabTrackAsync(
        GrabOptions options,
        int trackNumber,
        bool isAudio,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            _isCancelled = false;
            using var reg = ct.Register(Cancel);

            var dir = Path.GetDirectoryName(options.TargetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var grabber = CreateConfiguredGrabber(options);
            grabber.AudioCompression = STARBURN_AUDIO_COMPRESSION.STARBURN_AUDIO_UNCOMPRESSED;
            grabber.OnProgress += (pct, rem) =>
                progress?.Report(new BurnProgress(BurnPhase.Grabbing, pct, TimeRemaining: TimeSpan.FromSeconds(rem), Message: $"Grabbing track {trackNumber}... {pct}%"));
            _grabber = grabber;

            if (isAudio)
                grabber.GrabTrackAsAudio(trackNumber, options.TargetPath);
            else
                grabber.GrabTrackAsData(trackNumber, options.TargetPath);

            progress?.Report(new BurnProgress(BurnPhase.Completed, 100, Message: $"Track {trackNumber} grabbed."));
        }, ct);
    }

    public void Cancel()
    {
        _isCancelled = true;
        _ = Task.Run(() =>
        {
            try { _grabber?.Cancel(); } catch { }
        });
    }

    private static GrabberClass CreateConfiguredGrabber(GrabOptions options)
    {
        var grabber = new GrabberClass
        {
            Drive = options.Drive.RawDrive,
            IgnoreBadBlocks = options.IgnoreBadBlocks,
            DisableHardwareErrorCorrection = options.DisableHardwareErrorCorrection,
            NumberOfReadRetries = Math.Max(0, options.NumberOfReadRetries),
            FastReadTOC = options.FastReadToc
        };
        if (options.ReadSpeedKBps > 0)
        {
            try { grabber.ReadSpeed = options.ReadSpeedKBps; } catch (COMException) { }
        }
        return grabber;
    }
}
