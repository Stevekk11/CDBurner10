using CDBurner10.Core.Audio;
using CDBurner10.Core.Native;

namespace CDBurner10.Core.Burning;

public enum DiscReadMode
{
    Linear,
    Navigated
}

public sealed record GrabTrackInfo(string FileName, bool IsAudio, int TrackSizeLB, int BadSectorCount = 0);

public sealed record DiscGrabResult(
    DiscReadMode ModeUsed,
    long BytesWritten,
    int BadSectorCount,
    IReadOnlyList<LbaRange> BadRanges,
    bool WasUnfinalizedRecovery = false,
    int ExtractedTitle = 0)
{
    public string FormatRangesSummary(int maxRanges = 10)
    {
        if (BadRanges.Count == 0)
            return "none";

        var shown = BadRanges.Take(Math.Max(1, maxRanges)).Select(r => r.ToString());
        string joined = string.Join(", ", shown);
        return BadRanges.Count > maxRanges
            ? $"{joined}, ... (+{BadRanges.Count - maxRanges} more range(s))"
            : joined;
    }
}

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
    /// When null, .iso targets always use Linear mode while non-.iso DVD-Video targets use Navigated.
    /// </summary>
    public DiscReadMode? ReadMode { get; init; }

    /// <summary>
    /// Target DVD title number for Navigated mode (0 = automatically select longest title).
    /// </summary>
    public int TargetTitle { get; init; } = 0;

    /// <summary>
    /// Allows automatic fallback to raw MPEG-2 sector recovery when an unfinalized DVD is detected.
    /// Disabled for Optical-to-Optical ISO copy and explicit .iso imaging unless ForceUnfinalizedRecovery is set.
    /// </summary>
    public bool AllowUnfinalizedRecovery { get; init; } = true;

    /// <summary>
    /// Forces raw MPEG-2 sector recovery via UnfinalizedDvdReader when the caller already confirmed
    /// the source disc is an unfinalized DVD.
    /// </summary>
    public bool ForceUnfinalizedRecovery { get; init; } = false;
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

        // Never write a single-title raw MPEG-2 stream into a .iso image file.
        if (!string.IsNullOrWhiteSpace(options.TargetPath) &&
            options.TargetPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            return DiscReadMode.Linear;
        }

        bool isDvdVideo = options.IsDvdVideo ||
                          (options.Drive is not null && DvdNavigationWalker.IsDvdVideoDrive(options.Drive));
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

    public Task<DiscGrabResult> GrabDiscAsync(
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
                if (!options.ForceUnfinalizedRecovery && readMode == DiscReadMode.Navigated)
                {
                    if (string.IsNullOrWhiteSpace(options.Drive.Letter))
                        throw new InvalidOperationException("The selected drive does not report a drive letter for libdvdnav.");

                    ScsiOpticalDevice.TrySetReadSpeed(options.Drive.Letter, options.ReadSpeedKBps);
                    progress?.Report(new BurnProgress(
                        BurnPhase.Grabbing,
                        0,
                        Message: "DVD-Video detected — walking disc navigation program with libdvdnav..."));

                    try
                    {
                        var walkResult = DvdNavigationWalker.WalkTitleToFile(
                            options.Drive.Letter,
                            options.TargetPath,
                            options.TargetTitle,
                            progress,
                            token);
                        return new DiscGrabResult(
                            DiscReadMode.Navigated,
                            walkResult.BytesWritten,
                            0,
                            Array.Empty<LbaRange>(),
                            WasUnfinalizedRecovery: false,
                            ExtractedTitle: walkResult.TitleNumber);
                    }
                    catch (OperationCanceledException)
                    {
                        try { File.Delete(options.TargetPath); } catch { }
                        progress?.Report(new BurnProgress(BurnPhase.Cancelled, 0, Message: "Grab cancelled."));
                        throw;
                    }
                }

                // Unfinalized DVD-R / DVD+R / DVD-RW (no UDF/VIDEO_TS.IFO, raw MPEG-2 sectors):
                bool canTryUnfinalized = options.ForceUnfinalizedRecovery ||
                                         (options.AllowUnfinalizedRecovery &&
                                          !options.ReadMode.HasValue &&
                                          !options.TargetPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase));

                if (canTryUnfinalized &&
                    !string.IsNullOrWhiteSpace(options.Drive.Letter) &&
                    !DvdNavigationWalker.IsDvdVideoDrive(options.Drive) &&
                    UnfinalizedDvdReader.TryScanDrive(options.Drive.Letter, out var unfinalizedScan) &&
                    unfinalizedScan is not null)
                {
                    ScsiOpticalDevice.TrySetReadSpeed(options.Drive.Letter, options.ReadSpeedKBps);
                    progress?.Report(new BurnProgress(
                        BurnPhase.Grabbing,
                        0,
                        Message: $"Unfinalized DVD detected — recovering raw MPEG-2 video from sector {unfinalizedScan.FirstMpegSector:N0}..."));

                    try
                    {
                        var unfinResult = UnfinalizedDvdReader.ReadToFile(
                            options.Drive.Letter,
                            options.TargetPath,
                            unfinalizedScan,
                            progress,
                            token);
                        return new DiscGrabResult(
                            DiscReadMode.Linear,
                            unfinResult.BytesWritten,
                            (int)Math.Min(int.MaxValue, unfinResult.SkippedUnreadableSectors),
                            Array.Empty<LbaRange>(),
                            WasUnfinalizedRecovery: true);
                    }
                    catch (OperationCanceledException)
                    {
                        try { File.Delete(options.TargetPath); } catch { }
                        progress?.Report(new BurnProgress(BurnPhase.Cancelled, 0, Message: "Grab cancelled."));
                        throw;
                    }
                }

                var discInfo = options.Drive.GetDiscInfo(refresh: true);
                if (options.DecryptCss && discInfo.IsCssProtected)
                {
                    ScsiOpticalDevice.TrySetReadSpeed(options.Drive.Letter, options.ReadSpeedKBps);
                    progress?.Report(new BurnProgress(
                        BurnPhase.Grabbing,
                        0,
                        Message: "CSS copy protection detected — decrypting with libdvdcss..."));

                    BadSectorTracker cssTracker;
                    try
                    {
                        long expectedBytes = discInfo.UsedSizeBytes > 0 ? discInfo.UsedSizeBytes : discInfo.TotalSizeBytes;
                        cssTracker = DvdCssReader.GrabToIso(
                            options.Drive,
                            options.TargetPath,
                            expectedBytes,
                            progress,
                            token,
                            retries: Math.Max(0, options.NumberOfReadRetries),
                            ignoreBadBlocks: options.IgnoreBadBlocks,
                            disableHardwareEc: options.DisableHardwareErrorCorrection);
                    }
                    catch (OperationCanceledException)
                    {
                        progress?.Report(new BurnProgress(BurnPhase.Cancelled, 0, Message: "Grab cancelled."));
                        throw;
                    }

                    long cssSize = File.Exists(options.TargetPath) ? new FileInfo(options.TargetPath).Length : 0;
                    string cssDoneMsg = cssTracker.BadSectorCount > 0
                        ? $"Disc image saved with {cssTracker.BadSectorCount} unreadable sector(s) zero-filled (CSS decrypted)."
                        : "Disc image saved successfully (CSS decrypted with libdvdcss).";
                    progress?.Report(new BurnProgress(
                        BurnPhase.Completed,
                        100,
                        Message: cssDoneMsg,
                        ReadErrors: cssTracker.BadSectorCount));
                    return new DiscGrabResult(
                        DiscReadMode.Linear,
                        cssSize,
                        cssTracker.BadSectorCount,
                        cssTracker.Ranges);
                }

                // Pure Win32/SPTI linear data sector grab
                if (string.IsNullOrWhiteSpace(options.Drive.Letter))
                    throw new InvalidOperationException("The selected drive does not report a drive letter.");

                long totalSectors = discInfo.UsedSizeBytes > 0
                    ? (discInfo.UsedSizeBytes + ScsiOpticalDevice.DataSectorSize - 1) / ScsiOpticalDevice.DataSectorSize
                    : 0;

                BadSectorTracker linearTracker;
                try
                {
                    linearTracker = ScsiOpticalDevice.GrabDataSectorsToFile(
                        options.Drive.Letter,
                        options.TargetPath,
                        startLba: 0,
                        sectorCount: totalSectors,
                        readSpeedKBps: options.ReadSpeedKBps,
                        retries: Math.Max(0, options.NumberOfReadRetries),
                        ignoreBadBlocks: options.IgnoreBadBlocks,
                        progress,
                        token,
                        disableHardwareEc: options.DisableHardwareErrorCorrection);
                }
                catch (OperationCanceledException)
                {
                    try { File.Delete(options.TargetPath); } catch { }
                    progress?.Report(new BurnProgress(BurnPhase.Cancelled, 0, Message: "Grab cancelled."));
                    throw;
                }

                long finalBytes = File.Exists(options.TargetPath) ? new FileInfo(options.TargetPath).Length : 0;
                string doneMsg = linearTracker.BadSectorCount > 0
                    ? $"Disc image saved with {linearTracker.BadSectorCount} unreadable sector(s) zero-filled."
                    : "Disc image saved successfully.";
                progress?.Report(new BurnProgress(
                    BurnPhase.Completed,
                    100,
                    Message: doneMsg,
                    ReadErrors: linearTracker.BadSectorCount));

                return new DiscGrabResult(
                    DiscReadMode.Linear,
                    finalBytes,
                    linearTracker.BadSectorCount,
                    linearTracker.Ranges);
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
                        var audioTracker = CdAudioDevice.RipTrackToWav(
                            options.Drive.Letter,
                            trackNum,
                            wavPath,
                            trackProg,
                            token,
                            options.ReadSpeedKBps,
                            Math.Max(0, options.NumberOfReadRetries),
                            padUnreadableWithSilence: options.IgnoreBadBlocks);
                        tracks[trackNum] = new GrabTrackInfo(wavPath, IsAudio: true, track.SizeBlocks, audioTracker.BadSectorCount);
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
                                Message: $"Grabbing data track {idx + 1}/{totalTracks}... {overall}%",
                                ReadErrors: bp.ReadErrors));
                        });
                        var dataTracker = ScsiOpticalDevice.GrabDataSectorsToFile(
                            options.Drive.Letter,
                            isoPath,
                            track.StartLba,
                            track.SizeBlocks,
                            options.ReadSpeedKBps,
                            Math.Max(0, options.NumberOfReadRetries),
                            options.IgnoreBadBlocks,
                            trackProg,
                            token,
                            disableHardwareEc: options.DisableHardwareErrorCorrection);
                        tracks[trackNum] = new GrabTrackInfo(isoPath, IsAudio: false, track.SizeBlocks, dataTracker.BadSectorCount);
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

    public Task<DiscGrabResult> GrabTrackAsync(
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

                BadSectorTracker tracker;
                if (isAudio)
                {
                    var trackProg = new Progress<double>(pct =>
                        progress?.Report(new BurnProgress(
                            BurnPhase.Grabbing,
                            (int)Math.Clamp(pct, 0, 100),
                            Message: $"Grabbing audio track {trackNumber}... {(int)pct}%")));
                    tracker = CdAudioDevice.RipTrackToWav(
                        options.Drive.Letter,
                        trackNumber,
                        options.TargetPath,
                        trackProg,
                        token,
                        options.ReadSpeedKBps,
                        Math.Max(0, options.NumberOfReadRetries),
                        padUnreadableWithSilence: options.IgnoreBadBlocks);
                }
                else
                {
                    var discInfo = options.Drive.GetDiscInfo(refresh: true);
                    var track = discInfo.Sessions.SelectMany(s => s.Tracks).FirstOrDefault(t => t.TrackNumber == trackNumber);
                    long startLba = track?.StartLba ?? 0;
                    long sectors = track?.SizeBlocks ?? 0;
                    tracker = ScsiOpticalDevice.GrabDataSectorsToFile(
                        options.Drive.Letter,
                        options.TargetPath,
                        startLba,
                        sectors,
                        options.ReadSpeedKBps,
                        Math.Max(0, options.NumberOfReadRetries),
                        options.IgnoreBadBlocks,
                        progress,
                        token,
                        disableHardwareEc: options.DisableHardwareErrorCorrection);
                }

                long written = File.Exists(options.TargetPath) ? new FileInfo(options.TargetPath).Length : 0;
                progress?.Report(new BurnProgress(
                    BurnPhase.Completed,
                    100,
                    Message: $"Track {trackNumber} grabbed.",
                    ReadErrors: tracker.BadSectorCount));

                return new DiscGrabResult(
                    DiscReadMode.Linear,
                    written,
                    tracker.BadSectorCount,
                    tracker.Ranges);
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
