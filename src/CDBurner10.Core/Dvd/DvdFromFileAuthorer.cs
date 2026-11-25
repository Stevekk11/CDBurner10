using CDBurner10.Core.Burning;
using CDBurner10.Core.Settings;
using CDBurner10.Core.Utilities;

namespace CDBurner10.Core.Dvd;

/// <summary>
/// End-to-end pipeline that converts one or more video files (MP4, MKV, etc.)
/// into a DVD-Video <c>VIDEO_TS</c> structure and either burns it to an optical disc
/// or saves it as a bootable/playable DVD-Video ISO (UDF 1.02 + ISO 9660 bridge).
/// </summary>
public sealed class DvdFromFileAuthorer
{
    private const long Dvd5MaxBytes = 4_700_000_000L;
    private const long Dvd9MaxBytes = 8_540_000_000L;

    private readonly object _cancelLock = new();
    private CancellationTokenSource? _internalCts;
    private DvdVideoBurner? _activeBurner;

    /// <summary>
    /// Encodes all sources in <paramref name="options"/>, authors <c>VIDEO_TS</c>,
    /// and burns the resulting DVD-Video disc to <see cref="DvdAuthoringOptions.Drive"/>.
    /// </summary>
    public Task BurnAsync(
        DvdAuthoringOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Drive is null)
            throw new InvalidOperationException("Target optical drive is required.");

        return ExecutePipelineAsync(options, outputIsoPath: null, progress, ct);
    }

    /// <summary>
    /// Encodes all sources in <paramref name="options"/>, authors <c>VIDEO_TS</c>,
    /// and saves the resulting DVD-Video UDF 1.02 + ISO 9660 image to <paramref name="outputIsoPath"/>.
    /// </summary>
    public Task SaveIsoAsync(
        DvdAuthoringOptions options,
        string outputIsoPath,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(outputIsoPath))
            throw new ArgumentException("Output ISO path is required.", nameof(outputIsoPath));

        return ExecutePipelineAsync(options, outputIsoPath, progress, ct);
    }

    /// <summary>
    /// Cancels any active encoding, authoring, or burning operation.
    /// </summary>
    public void Cancel()
    {
        CancellationTokenSource? cts;
        DvdVideoBurner? burner;
        lock (_cancelLock)
        {
            cts = _internalCts;
            burner = _activeBurner;
        }

        try { cts?.Cancel(); } catch { }
        try { burner?.Cancel(); } catch { }
    }

    private async Task ExecutePipelineAsync(
        DvdAuthoringOptions options,
        string? outputIsoPath,
        IProgress<BurnProgress>? progress,
        CancellationToken ct)
    {
        // 1. Validate ffmpeg / ffprobe availability.
        if (!DvdVideoConverter.IsAvailable)
            throw new InvalidOperationException(DvdVideoConverter.InstallHint);

        if (options.Sources is null || options.Sources.Count == 0)
            throw new InvalidOperationException("Add at least one video file to create a Video DVD.");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_cancelLock)
        {
            _internalCts = linkedCts;
            _activeBurner = null;
        }

        CancellationToken token = linkedCts.Token;
        string workDir = Path.Combine(
            SettingsService.Default.GetTempDirectory(),
            "DvdAuthor",
            Guid.NewGuid().ToString("N"));

        try
        {
            // 2. Probe all source videos.
            progress?.Report(new BurnProgress(
                BurnPhase.Preparing,
                0,
                TotalTracks: options.Sources.Count,
                Message: "Analyzing source video files..."));

            var probedInfos = new List<DvdMediaInfo>(options.Sources.Count);
            TimeSpan totalDuration = TimeSpan.Zero;
            int maxAudioStreams = 1;

            for (int i = 0; i < options.Sources.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var src = options.Sources[i];
                if (!File.Exists(src.FilePath))
                    throw new FileNotFoundException($"Source video file not found: '{src.FilePath}'.", src.FilePath);

                var info = await DvdMediaProber.ProbeAsync(src.FilePath, token).ConfigureAwait(false);
                probedInfos.Add(info);
                totalDuration += info.Duration > TimeSpan.Zero ? info.Duration : TimeSpan.FromSeconds(1);

                int titleAudioCount = options.AudioSelection == DvdAudioSelection.AllTracks
                    ? Math.Clamp(info.AudioStreamCount, 1, 8)
                    : 1;
                if (titleAudioCount > maxAudioStreams)
                    maxAudioStreams = titleAudioCount;
            }

            if (totalDuration <= TimeSpan.Zero)
                throw new InvalidOperationException("Could not determine the duration of the selected video files.");

            // 3. Compute optimal MPEG-2 video bitrate and verify disc capacity.
            int videoBitrateKbps = DvdMediaProber.CalculateVideoBitrateKbps(
                totalDuration,
                options.TargetDisc,
                options.Quality,
                maxAudioStreams);

            long estimatedBytes = DvdMediaProber.EstimateDiscBytes(
                totalDuration,
                videoBitrateKbps,
                audioBitrateKbps: 192 * maxAudioStreams);

            long discCapacityBytes = options.TargetDisc == DvdTargetDisc.Dvd9_8_5GB
                ? Dvd9MaxBytes
                : Dvd5MaxBytes;

            if (estimatedBytes > discCapacityBytes)
            {
                throw new InvalidOperationException(
                    $"Estimated DVD size ({FileSizeFormatter.Format(estimatedBytes)}) exceeds target disc capacity ({FileSizeFormatter.Format(discCapacityBytes)}). " +
                    "Use a lower quality preset, switch to DVD DL 8.5 GB, or remove some videos.");
            }

            // 4. Create temporary workspace.
            string encodedDir = Path.Combine(workDir, "encoded");
            string authoredRoot = Path.Combine(workDir, "disc");
            Directory.CreateDirectory(encodedDir);
            Directory.CreateDirectory(authoredRoot);

            // 5. Encode each source to DVD-compliant MPEG-2 PS (progress 0..65%).
            var authoredInputs = new List<AuthoredTitleInput>(options.Sources.Count);
            double totalSeconds = Math.Max(1.0, totalDuration.TotalSeconds);
            double completedSeconds = 0.0;

            for (int i = 0; i < options.Sources.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var src = options.Sources[i];
                var info = probedInfos[i];
                double titleSeconds = Math.Max(1.0, info.Duration.TotalSeconds);
                string titleName = Path.GetFileNameWithoutExtension(src.FilePath);
                string mpgPath = Path.Combine(encodedDir, $"title_{i + 1:D2}.mpg");

                DvdAspectRatio resolvedAspect = DvdMediaProber.ResolveAspectRatio(
                    options.AspectRatio,
                    info.DisplayAspectRatio);

                int titleAudioCount = options.AudioSelection == DvdAudioSelection.AllTracks
                    ? Math.Clamp(info.AudioStreamCount, 1, 8)
                    : Math.Min(1, Math.Max(0, info.AudioStreamCount));
                if (titleAudioCount == 0) titleAudioCount = 1;

                int trackNumber = i + 1;
                double baseSeconds = completedSeconds;

                var encodeProgress = new Progress<double>(frac =>
                {
                    double clamped = Math.Clamp(frac, 0.0, 1.0);
                    double overallFrac = (baseSeconds + clamped * titleSeconds) / totalSeconds;
                    int pct = (int)Math.Clamp(Math.Round(overallFrac * 65.0), 0, 65);
                    progress?.Report(new BurnProgress(
                        BurnPhase.Converting,
                        pct,
                        CurrentTrack: trackNumber,
                        TotalTracks: options.Sources.Count,
                        Message: $"Encoding title {trackNumber}/{options.Sources.Count}: {titleName} ({(int)Math.Round(clamped * 100)}%)"));
                });

                progress?.Report(new BurnProgress(
                    BurnPhase.Converting,
                    (int)Math.Clamp(Math.Round((completedSeconds / totalSeconds) * 65.0), 0, 65),
                    CurrentTrack: trackNumber,
                    TotalTracks: options.Sources.Count,
                    Message: $"Encoding title {trackNumber}/{options.Sources.Count}: {titleName}..."));

                await DvdMpeg2Encoder.EncodeTitleAsync(
                    src,
                    info,
                    options,
                    videoBitrateKbps,
                    resolvedAspect,
                    mpgPath,
                    encodeProgress,
                    token).ConfigureAwait(false);

                authoredInputs.Add(new AuthoredTitleInput(
                    mpgPath,
                    titleName,
                    info.Duration,
                    options.Standard,
                    resolvedAspect,
                    titleAudioCount));

                completedSeconds += titleSeconds;
            }

            // 6. Author VIDEO_TS (progress 65..80%).
            token.ThrowIfCancellationRequested();
            progress?.Report(new BurnProgress(
                BurnPhase.Preparing,
                65,
                Message: "Authoring DVD-Video structure (VIDEO_TS)..."));

            var authorProgress = new Progress<double>(frac =>
            {
                int pct = 65 + (int)Math.Clamp(Math.Round(frac * 15.0), 0, 15);
                progress?.Report(new BurnProgress(
                    BurnPhase.Preparing,
                    pct,
                    Message: $"Authoring VIDEO_TS... {(int)Math.Round(Math.Clamp(frac, 0, 1) * 100)}%"));
            });

            string videoTsDir = await VideoTsAuthorer.AuthorAsync(
                authoredInputs,
                authoredRoot,
                options.VolumeName,
                options.CreateMenu,
                authorProgress,
                token).ConfigureAwait(false);

            // 7. Burn to disc or save ISO via DvdVideoBurner (progress 80..100%).
            token.ThrowIfCancellationRequested();
            var burner = new DvdVideoBurner();
            lock (_cancelLock)
            {
                _activeBurner = burner;
            }

            var burnOptions = new DvdVideoBurnOptions
            {
                Drive = options.Drive,
                VideoTsDirectory = videoTsDir,
                AudioTsDirectory = Path.Combine(authoredRoot, "AUDIO_TS"),
                VolumeName = string.IsNullOrWhiteSpace(options.VolumeName) ? "MY_VIDEO" : options.VolumeName,
                WriteSpeedKBps = options.WriteSpeedKBps,
                VerifyData = options.VerifyAfterBurn,
                EjectAfterBurn = options.EjectAfterBurn
            };

            var burnProgress = new Progress<BurnProgress>(p =>
            {
                int mappedPct = p.Phase == BurnPhase.Completed
                    ? 100
                    : 80 + (int)Math.Clamp(Math.Round(p.Percent * 0.20), 0, 20);
                progress?.Report(p with { Percent = mappedPct });
            });

            if (outputIsoPath is not null)
            {
                await burner.SaveIsoAsync(burnOptions, outputIsoPath, burnProgress, token).ConfigureAwait(false);
            }
            else
            {
                await burner.BurnAsync(burnOptions, burnProgress, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new BurnProgress(
                BurnPhase.Cancelled,
                0,
                Message: "Video DVD authoring cancelled."));
            throw;
        }
        finally
        {
            lock (_cancelLock)
            {
                _activeBurner = null;
                if (ReferenceEquals(_internalCts, linkedCts))
                    _internalCts = null;
            }

            // 8. Always clean up temporary work directory.
            TryDeleteDirectory(workDir);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of temp authoring files.
        }
    }
}
