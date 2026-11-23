using CDBurner10.Core.Audio;
using CDBurner10.Core.Native;

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
    private CancellationTokenSource? _activeCts;
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
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeCts = linkedCts;
            var token = linkedCts.Token;

            try
            {
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
                            token);
                    }
                    catch (OperationCanceledException)
                    {
                        try { File.Delete(options.TargetPath); } catch { }
                        progress?.Report(new BurnProgress(BurnPhase.Cancelled, 0, Message: "Grab cancelled."));
                        return;
                    }

                    return;
                }

                // Unfinalized DVD-R / DVD+R / DVD-RW (no UDF/VIDEO_TS.IFO, raw MPEG-2 sectors):
                if (!options.ReadMode.HasValue &&
                    !string.IsNullOrWhiteSpace(options.Drive.Letter) &&
                    !DvdNavigationWalker.IsDvdVideoDrive(options.Drive) &&
                    UnfinalizedDvdReader.TryScanDrive(options.Drive.Letter, out var unfinalizedScan) &&
                    unfinalizedScan is not null)
                {
                    progress?.Report(new BurnProgress(
                        BurnPhase.Grabbing,
                        0,
                        Message: $"Unfinalized DVD detected — recovering raw MPEG-2 video from sector {unfinalizedScan.FirstMpegSector:N0}..."));

                    try
                    {
                        UnfinalizedDvdReader.ReadToFile(
                            options.Drive.Letter,
                            options.TargetPath,
                            unfinalizedScan,
                            progress,
                            token);
                    }
                    catch (OperationCanceledException)
                    {
                        try { File.Delete(options.TargetPath); } catch { }
                        progress?.Report(new BurnProgress(BurnPhase.Cancelled, 0, Message: "Grab cancelled."));
                        return;
                    }

                    return;
                }

                var discInfo = options.Drive.GetDiscInfo(refresh: true);
                if (options.DecryptCss && discInfo.IsCssProtected)
                {
                    progress?.Report(new BurnProgress(
                        BurnPhase.Grabbing,
                        0,
                        Message: "CSS copy protection detected — decrypting with libdvdcss..."));

                    try
                    {
                        long expectedBytes = discInfo.UsedSizeBytes > 0 ? discInfo.UsedSizeBytes : discInfo.TotalSizeBytes;
                        DvdCssReader.GrabToIso(options.Drive, options.TargetPath, expectedBytes, progress, token);
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

                // Pure Win32/SPTI linear data sector grab
                if (string.IsNullOrWhiteSpace(options.Drive.Letter))
                    throw new InvalidOperationException("The selected drive does not report a drive letter.");

                long totalSectors = discInfo.UsedSizeBytes > 0
                    ? (discInfo.UsedSizeBytes + ScsiOpticalDevice.DataSectorSize - 1) / ScsiOpticalDevice.DataSectorSize
                    : 0;

                try
                {
                    ScsiOpticalDevice.GrabDataSectorsToFile(
                        options.Drive.Letter,
                        options.TargetPath,
                        startLba: 0,
                        sectorCount: totalSectors,
                        readSpeedKBps: options.ReadSpeedKBps,
                        retries: Math.Max(1, options.NumberOfReadRetries),
                        ignoreBadBlocks: options.IgnoreBadBlocks,
                        progress,
                        token);
                }
                catch (OperationCanceledException)
                {
                    try { File.Delete(options.TargetPath); } catch { }
                    progress?.Report(new BurnProgress(BurnPhase.Cancelled, 0, Message: "Grab cancelled."));
                    return;
                }

                progress?.Report(new BurnProgress(
                    _isCancelled ? BurnPhase.Cancelled : BurnPhase.Completed,
                    100,
                    Message: _isCancelled ? "Grab cancelled." : "Disc image saved successfully."));
            }
            finally
            {
                _activeCts = null;
            }
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
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeCts = linkedCts;
            var token = linkedCts.Token;

            try
            {
                if (string.IsNullOrWhiteSpace(options.Drive.Letter))
                    throw new InvalidOperationException("The selected drive does not report a drive letter.");

                Directory.CreateDirectory(options.TargetPath);
                var discInfo = options.Drive.GetDiscInfo(refresh: true);
                var allTracks = discInfo.Sessions.SelectMany(s => s.Tracks).ToList();
                int totalTracks = allTracks.Count;
                var tracks = new Dictionary<int, GrabTrackInfo>();

                for (int i = 0; i < totalTracks; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var track = allTracks[i];
                    int trackNum = track.TrackNumber;
                    int idx = i;

                    if (track.IsAudio)
                    {
                        var wavPath = Path.Combine(options.TargetPath, $"Track{trackNum}.wav");
                        var trackProg = new Progress<double>(pct =>
                        {
                            int overall = (int)Math.Clamp((idx * 100.0 + pct) / Math.Max(1, totalTracks), 0, 100);
                            progress?.Report(new BurnProgress(
                                BurnPhase.Grabbing,
                                overall,
                                CurrentTrack: idx + 1,
                                TotalTracks: totalTracks,
                                Message: $"Grabbing audio track {idx + 1}/{totalTracks}... {overall}%"));
                        });
                        CdAudioDevice.RipTrackToWav(options.Drive.Letter, trackNum, wavPath, trackProg, token);
                        tracks[trackNum] = new GrabTrackInfo(wavPath, IsAudio: true, track.SizeBlocks);
                    }
                    else
                    {
                        var isoPath = Path.Combine(options.TargetPath, $"Track{trackNum}.iso");
                        var trackProg = new Progress<BurnProgress>(bp =>
                        {
                            int overall = (int)Math.Clamp((idx * 100.0 + bp.Percent) / Math.Max(1, totalTracks), 0, 100);
                            progress?.Report(new BurnProgress(
                                BurnPhase.Grabbing,
                                overall,
                                CurrentTrack: idx + 1,
                                TotalTracks: totalTracks,
                                Message: $"Grabbing data track {idx + 1}/{totalTracks}... {overall}%"));
                        });
                        ScsiOpticalDevice.GrabDataSectorsToFile(
                            options.Drive.Letter,
                            isoPath,
                            track.StartLba,
                            track.SizeBlocks,
                            options.ReadSpeedKBps,
                            Math.Max(1, options.NumberOfReadRetries),
                            options.IgnoreBadBlocks,
                            trackProg,
                            token);
                        tracks[trackNum] = new GrabTrackInfo(isoPath, IsAudio: false, track.SizeBlocks);
                    }
                }

                progress?.Report(new BurnProgress(BurnPhase.Completed, 100, Message: "All tracks grabbed successfully."));
                return tracks;
            }
            finally
            {
                _activeCts = null;
            }
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
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeCts = linkedCts;
            var token = linkedCts.Token;

            try
            {
                if (string.IsNullOrWhiteSpace(options.Drive.Letter))
                    throw new InvalidOperationException("The selected drive does not report a drive letter.");

                var dir = Path.GetDirectoryName(options.TargetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (isAudio)
                {
                    var trackProg = new Progress<double>(pct =>
                        progress?.Report(new BurnProgress(BurnPhase.Grabbing, (int)Math.Clamp(pct, 0, 100), Message: $"Grabbing audio track {trackNumber}... {(int)pct}%")));
                    CdAudioDevice.RipTrackToWav(options.Drive.Letter, trackNumber, options.TargetPath, trackProg, token);
                }
                else
                {
                    var discInfo = options.Drive.GetDiscInfo(refresh: true);
                    var track = discInfo.Sessions.SelectMany(s => s.Tracks).FirstOrDefault(t => t.TrackNumber == trackNumber);
                    long startLba = track?.StartLba ?? 0;
                    long sectors = track?.SizeBlocks ?? 0;
                    ScsiOpticalDevice.GrabDataSectorsToFile(
                        options.Drive.Letter,
                        options.TargetPath,
                        startLba,
                        sectors,
                        options.ReadSpeedKBps,
                        Math.Max(1, options.NumberOfReadRetries),
                        options.IgnoreBadBlocks,
                        progress,
                        token);
                }

                progress?.Report(new BurnProgress(BurnPhase.Completed, 100, Message: $"Track {trackNumber} grabbed."));
            }
            finally
            {
                _activeCts = null;
            }
        }, ct);
    }

    public void Cancel()
    {
        _isCancelled = true;
        try { _activeCts?.Cancel(); } catch { }
    }
}
