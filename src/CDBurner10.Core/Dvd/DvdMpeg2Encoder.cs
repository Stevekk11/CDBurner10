using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CDBurner10.Core.Dvd;

/// <summary>
/// Builds FFmpeg command-line arguments and encodes source videos to DVD-Video compliant
/// MPEG-2 Program Streams (.mpg) with AC-3 audio and optional subtitles.
/// </summary>
public static class DvdMpeg2Encoder
{
    private const int MaxDvdAudioTracks = 8;
    private const int MaxStderrTailLines = 40;

    /// <summary>
    /// Builds the FFmpeg argument vector to transcode a source video into a DVD-Video
    /// compliant MPEG-2 Program Stream.
    /// </summary>
    /// <param name="source">Source video file and optional external .srt path.</param>
    /// <param name="info">Probed metadata for the source video.</param>
    /// <param name="options">Authoring options controlling standard, audio, and subtitles.</param>
    /// <param name="videoBitrateKbps">Target MPEG-2 video bitrate in kbps.</param>
    /// <param name="resolvedAspect">Resolved display aspect ratio (16:9 or 4:3).</param>
    /// <param name="outputMpgPath">Destination path for the encoded .mpg file.</param>
    /// <returns>Exact argument array suitable for <see cref="ProcessStartInfo.ArgumentList"/>.</returns>
    public static string[] BuildFfmpegArguments(
        DvdSourceVideo source,
        DvdMediaInfo info,
        DvdAuthoringOptions options,
        int videoBitrateKbps,
        DvdAspectRatio resolvedAspect,
        string outputMpgPath)
    {
        return BuildFfmpegArgumentsCore(
            source,
            info,
            options,
            videoBitrateKbps,
            resolvedAspect,
            outputMpgPath,
            includeEmbeddedSubtitles: true);
    }

    /// <summary>
    /// Encodes a single source video title to a DVD-Video compliant MPEG-2 Program Stream.
    /// </summary>
    /// <param name="source">Source video file and optional external .srt path.</param>
    /// <param name="info">Probed metadata for the source video.</param>
    /// <param name="options">Authoring options.</param>
    /// <param name="videoBitrateKbps">Target MPEG-2 video bitrate in kbps.</param>
    /// <param name="resolvedAspect">Resolved display aspect ratio (16:9 or 4:3).</param>
    /// <param name="outputMpgPath">Destination path for the encoded .mpg file.</param>
    /// <param name="progress">Optional progress reporter receiving normalized values in [0.0, 1.0].</param>
    /// <param name="ct">Cancellation token; kills the FFmpeg process tree when cancelled.</param>
    public static async Task EncodeTitleAsync(
        DvdSourceVideo source,
        DvdMediaInfo info,
        DvdAuthoringOptions options,
        int videoBitrateKbps,
        DvdAspectRatio resolvedAspect,
        string outputMpgPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(source.FilePath))
            throw new ArgumentException("Source video file path must not be empty.", nameof(source));
        if (string.IsNullOrWhiteSpace(outputMpgPath))
            throw new ArgumentException("Output MPEG file path must not be empty.", nameof(outputMpgPath));

        string ffmpeg = DvdVideoConverter.FindFfmpeg()
            ?? throw new InvalidOperationException(DvdVideoConverter.InstallHint);

        string? outDir = Path.GetDirectoryName(outputMpgPath);
        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir);

        bool allowEmbeddedSubFallback = options.SubtitleMode == DvdSubtitleMode.Embedded && info.SubtitleStreamCount > 0;
        string[] args = BuildFfmpegArgumentsCore(
            source,
            info,
            options,
            videoBitrateKbps,
            resolvedAspect,
            outputMpgPath,
            includeEmbeddedSubtitles: true);

        var result = await RunFfmpegAsync(ffmpeg, args, info.Duration, progress, ct);
        if (result.ExitCode == 0)
        {
            progress?.Report(1.0);
            return;
        }

        // Text-based embedded subtitles (e.g. SubRip/ASS/mov_text) cannot be transcoded directly
        // to bitmap dvdsub by FFmpeg; fall back cleanly to encoding without embedded subtitles.
        if (allowEmbeddedSubFallback && IsSubtitleEncodingFailure(result.StderrTail))
        {
            TryDelete(outputMpgPath);
            string[] fallbackArgs = BuildFfmpegArgumentsCore(
                source,
                info,
                options,
                videoBitrateKbps,
                resolvedAspect,
                outputMpgPath,
                includeEmbeddedSubtitles: false);

            result = await RunFfmpegAsync(ffmpeg, fallbackArgs, info.Duration, progress, ct);
            if (result.ExitCode == 0)
            {
                progress?.Report(1.0);
                return;
            }
        }

        TryDelete(outputMpgPath);
        string errorDetail = string.IsNullOrWhiteSpace(result.StderrTail)
            ? $"ffmpeg exited with code {result.ExitCode}."
            : result.StderrTail;
        throw new InvalidOperationException(
            $"MPEG-2 encoding failed for '{Path.GetFileName(source.FilePath)}':{Environment.NewLine}{errorDetail}");
    }

    /// <summary>
    /// Escapes a Windows or POSIX file path for FFmpeg's libass <c>subtitles</c> video filter.
    /// </summary>
    internal static string EscapeSubtitleFilterPath(string path)
    {
        var sb = new StringBuilder(path.Length + 8);
        foreach (char c in path)
        {
            switch (c)
            {
                case '\\':
                    sb.Append('/');
                    break;
                case ':':
                    sb.Append(@"\:");
                    break;
                case '\'':
                    sb.Append(@"\'");
                    break;
                case '[':
                    sb.Append(@"\[");
                    break;
                case ']':
                    sb.Append(@"\]");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string[] BuildFfmpegArgumentsCore(
        DvdSourceVideo source,
        DvdMediaInfo info,
        DvdAuthoringOptions options,
        int videoBitrateKbps,
        DvdAspectRatio resolvedAspect,
        string outputMpgPath,
        bool includeEmbeddedSubtitles)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(options);

        DvdAspectRatio effectiveAspect = DvdMediaProber.ResolveAspectRatio(resolvedAspect, info.DisplayAspectRatio);
        bool isPal = options.Standard == DvdVideoStandard.Pal;
        bool is16x9 = effectiveAspect == DvdAspectRatio.Widescreen16x9;

        int width = 720;
        int height = isPal ? 576 : 480;
        string frameRate = isPal ? "25" : "30000/1001";
        string gopSize = isPal ? "15" : "18";
        string aspect = is16x9 ? "16:9" : "4:3";
        string sar = (isPal, is16x9) switch
        {
            (true, true) => "64/45",
            (true, false) => "16/15",
            (false, true) => "32/27",
            (false, false) => "8/9"
        };

        int displayWidth = (isPal, is16x9) switch
        {
            (true, true) => 1024,
            (true, false) => 768,
            (false, true) => 854,
            (false, false) => 640
        };

        int clampedBitrate = Math.Clamp(
            videoBitrateKbps,
            DvdMediaProber.MinVideoBitrateKbps,
            DvdMediaProber.MaxVideoBitrateKbps);

        // Normalize source SAR to square pixels, fit & pad inside the display aspect frame
        // (1024x576 / 768x576 for PAL, 854x480 / 640x480 for NTSC), then scale to 720xH anamorphic DVD.
        string videoFilter =
            "scale=trunc(iw*sar/2)*2:ih,setsar=1," +
            $"scale={displayWidth}:{height}:force_original_aspect_ratio=decrease," +
            $"pad={displayWidth}:{height}:(ow-iw)/2:(oh-ih)/2," +
            $"scale={width}:{height}," +
            $"setsar={sar}";

        if (options.SubtitleMode == DvdSubtitleMode.ExternalSrt &&
            !string.IsNullOrWhiteSpace(source.ExternalSrtPath) &&
            File.Exists(source.ExternalSrtPath))
        {
            string escapedSrt = EscapeSubtitleFilterPath(source.ExternalSrtPath);
            videoFilter += $",subtitles='{escapedSrt}'";
        }

        var args = new List<string>(48)
        {
            "-y",
            "-hide_banner",
            "-nostdin",
            "-i", source.FilePath,
            "-map", "0:v:0"
        };

        if (options.AudioSelection == DvdAudioSelection.AllTracks)
        {
            if (info.AudioStreamCount > MaxDvdAudioTracks)
            {
                for (int i = 0; i < MaxDvdAudioTracks; i++)
                {
                    args.Add("-map");
                    args.Add($"0:a:{i}?");
                }
            }
            else
            {
                args.Add("-map");
                args.Add("0:a?");
            }
        }
        else
        {
            args.Add("-map");
            args.Add("0:a:0?");
        }

        bool mapEmbeddedSubs = includeEmbeddedSubtitles &&
                               options.SubtitleMode == DvdSubtitleMode.Embedded &&
                               info.SubtitleStreamCount > 0;
        if (mapEmbeddedSubs)
        {
            args.Add("-map");
            args.Add("0:s:0?");
        }

        args.AddRange(new[]
        {
            "-f", "dvd",
            "-c:v", "mpeg2video",
            "-b:v", $"{clampedBitrate.ToString(CultureInfo.InvariantCulture)}k",
            "-maxrate", "9000k",
            "-minrate", "0",
            "-bufsize", "1835008",
            "-muxrate", "10080000",
            "-packetsize", "2048",
            "-pix_fmt", "yuv420p",
            "-r", frameRate,
            "-g", gopSize,
            "-vf", videoFilter,
            "-aspect", aspect,
            "-c:a", "ac3",
            "-ar", "48000",
            "-b:a", "192k"
        });

        if (mapEmbeddedSubs)
        {
            args.Add("-c:s");
            args.Add("dvdsub");
        }

        args.AddRange(new[]
        {
            "-progress", "pipe:1",
            "-nostats",
            outputMpgPath
        });

        return args.ToArray();
    }

    private static async Task<(int ExitCode, string StderrTail)> RunFfmpegAsync(
        string ffmpegPath,
        string[] args,
        TimeSpan duration,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not start ffmpeg: {ex.Message}", ex);
        }

        using CancellationTokenRegistration registration = ct.Register(() => TryKill(process));

        var stderrLines = new ConcurrentQueue<string>();
        double totalUs = Math.Max(1.0, duration.TotalMilliseconds * 1000.0);

        Task stderrReader = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    stderrLines.Enqueue(line);
                    while (stderrLines.Count > MaxStderrTailLines && stderrLines.TryDequeue(out _)) { }
                }
            }
            catch
            {
                // Stream closes when process exits or is killed.
            }
        });

        Task stdoutReader = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    string key = line[..eq];
                    string value = line[(eq + 1)..];

                    if (key is "out_time_us" or "out_time_ms" &&
                        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long micros) &&
                        micros >= 0 &&
                        duration > TimeSpan.Zero)
                    {
                        double fraction = Math.Clamp(micros / totalUs, 0.0, 1.0);
                        progress?.Report(fraction);
                    }
                    else if (key == "progress" && value.Equals("end", StringComparison.OrdinalIgnoreCase))
                    {
                        progress?.Report(1.0);
                    }
                }
            }
            catch
            {
                // Stream closes when process exits or is killed.
            }
        });

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await Task.WhenAll(stderrReader, stdoutReader);
            throw;
        }

        await Task.WhenAll(stderrReader, stdoutReader);

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        string stderrTail = string.Join(Environment.NewLine, stderrLines);
        return (process.ExitCode, stderrTail);
    }

    private static bool IsSubtitleEncodingFailure(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return false;

        return stderr.Contains("dvdsub", StringComparison.OrdinalIgnoreCase) ||
               stderr.Contains("Subtitle encoding", StringComparison.OrdinalIgnoreCase) ||
               stderr.Contains("currently only possible from text to text or bitmap to bitmap", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process already exited.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore cleanup failure.
        }
    }
}
