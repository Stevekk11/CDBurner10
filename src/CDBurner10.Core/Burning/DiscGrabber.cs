using System.Runtime.InteropServices;
using RocketDivision.StarBurnX;

namespace CDBurner10.Core.Burning;

public enum DiscReadMode
{
    Linear,
    Navigated
}

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

    /// <summary>
    /// When the source is a CSS-protected DVD in Linear mode, read it through
    /// libdvdcss and write a decrypted ISO instead of copying scrambled sectors.
    /// </summary>
    public bool DecryptCss { get; init; } = true;

    /// <summary>
    /// Selects Linear sector reading vs. libdvdnav Navigated playback-path walking.
    /// When null, DVD-Video discs (detected via VIDEO_TS presence) default to Navigated.
    /// </summary>
    public DiscReadMode? ReadMode { get; init; }

    /// <summary>
    /// Target DVD title number for Navigated mode (0 = automatically select longest title).
    /// </summary>
    public int TargetTitle { get; init; } = 0;
}

public sealed class DiscGrabber
{
    private GrabberClass? _grabber;
    private bool _isCancelled;

    public bool IsCancelled => _isCancelled;

    public static DiscReadMode ResolveReadMode(GrabOptions options)
    {
        if (options.ReadMode.HasValue)
            return options.ReadMode.Value;

        bool isDvdVideo = options.IsDvdVideo || DvdNavigationWalker.IsDvdVideoDrive(options.Drive);
        return isDvdVideo && DvdNavigationWalker.IsAvailable
            ? DiscReadMode.Navigated
            : DiscReadMode.Linear;
    }

    public Task<DvdWalkResult> GrabNavigatedStreamAsync(
        DriveModel drive,
        Stream output,
        int targetTitle = 0,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            _isCancelled = false;
            if (string.IsNullOrWhiteSpace(drive.Letter))
                throw new InvalidOperationException("The selected drive does not report a drive letter.");

            return DvdNavigationWalker.WalkTitleToStream(
                drive.Letter,
                output,
                targetTitle,
                progress,
                ct);
        }, ct);
    }

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

            var readMode = ResolveReadMode(options);
            if (readMode == DiscReadMode.Navigated)
            {
                if (string.IsNullOrWhiteSpace(options.Drive.Letter))
                    throw new InvalidOperationException("The selected drive does not report a drive letter for libdvdnav.");

                progress?.Report(new BurnProgress(
                    BurnPhase.Grabbing,
                    0,
                    Message: "DVD-Video detected — walking disc navigation program with libdvdnav..."));

                try
                {
                    DvdNavigationWalker.WalkTitleToFile(
                        options.Drive.Letter,
                        options.TargetPath,
                        options.TargetTitle,
                        progress,
                        ct);
                }
                catch (OperationCanceledException)
                {
                    try { File.Delete(options.TargetPath); } catch { }
                    progress?.Report(new BurnProgress(BurnPhase.Cancelled, 0, Message: "Grab cancelled."));
                    return;
                }

                return;
            }

            var disc = options.DecryptCss
                ? GetDiscProtection(options.Drive)
                : (Protection: STARBURN_DISC_PROTECTION_SYSTEM.STARBURN_DVD_PROTECTION_NONE, UsedBytes: 0L);

            if (disc.Protection == STARBURN_DISC_PROTECTION_SYSTEM.STARBURN_DVD_PROTECTION_CSS)
            {
                progress?.Report(new BurnProgress(
                    BurnPhase.Grabbing,
                    0,
                    Message: "CSS copy protection detected - decrypting with libdvdcss..."));

                try
                {
                    DvdCssReader.GrabToIso(options.Drive, options.TargetPath, disc.UsedBytes, progress, ct);
                }
                catch (OperationCanceledException)
                {
                    progress?.Report(new BurnProgress(BurnPhase.Cancelled, 0, Message: "Grab cancelled."));
                    return;
                }

                progress?.Report(new BurnProgress(
                    BurnPhase.Completed,
                    100,
                    Message: "Disc image saved successfully (CSS decrypted with libdvdcss)."));
                return;
            }

            var grabber = CreateConfiguredGrabber(options);
            grabber.CDRawReadMode = STARBURN_DISC_READ_MODE_RAW.STARBURN_READ_MODE_COOKED;
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

    private static (STARBURN_DISC_PROTECTION_SYSTEM Protection, long UsedBytes) GetDiscProtection(DriveModel drive)
    {
        try
        {
            var disc = drive.RawDrive.DiscInfo;
            disc.Refresh();
            long usedBytes = (long)disc.UsedSize * 1024L;
            if (usedBytes <= 0) usedBytes = (long)disc.TotalSize * 1024L;
            return (disc.ProtectionSystem, usedBytes);
        }
        catch (COMException)
        {
            return (STARBURN_DISC_PROTECTION_SYSTEM.STARBURN_DVD_PROTECTION_NONE, 0);
        }
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
