using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CDBurner10.Core.Dvd;

public sealed record DvdAudioTrack(
    int StreamIndex,
    int Ordinal,
    string Codec,
    int Channels,
    string? Language,
    string? Title)
{
    public string DisplayName =>
        $"{DvdVideoConverter.GetLanguageName(Language)} • {DvdVideoConverter.GetChannelName(Channels)} {Codec.ToUpperInvariant()}";
}

public sealed record DvdSubtitleTrack(
    int StreamIndex,
    int Ordinal,
    string Codec,
    string? Language)
{
    public string DisplayName =>
        $"{DvdVideoConverter.GetLanguageName(Language)} (DVD subtitles)";
}

public sealed record DvdVideoInfo(
    TimeSpan Duration,
    int Width,
    int Height,
    IReadOnlyList<DvdAudioTrack> AudioTracks,
    IReadOnlyList<DvdSubtitleTrack> SubtitleTracks);

public sealed record DvdMp4Options
{
    /// <summary>DVD title number (0 = auto-select longest title, 1..99 = specific title).</summary>
    public int TitleNumber { get; init; }

    /// <summary>Zero-based audio track ordinal (0 = first track).</summary>
    public int AudioOrdinal { get; init; }

    /// <summary>Zero-based subtitle track ordinal to burn in, or null for none.</summary>
    public int? SubtitleOrdinal { get; init; }

    public int Crf { get; init; } = 21;
    public string Preset { get; init; } = "medium";
    public bool StereoDownmix { get; init; } = true;

    /// <summary>Deinterlace frames flagged as interlaced (bwdif=0:-1:1); progressive DVDs pass through untouched.</summary>
    public bool Deinterlace { get; init; } = true;

    /// <summary>Optional limit, mainly useful for previews and tests.</summary>
    public TimeSpan? MaxDuration { get; init; }
}

public sealed record DvdMp4Progress(
    int Percent,
    double Speed,
    double Fps,
    TimeSpan TimeRemaining,
    string Message);

/// <summary>
/// Converts the main movie of a DVD-Video ISO or VIDEO_TS folder to H.264/AAC
/// MP4 using ffmpeg. The dvdvideo demuxer selects the longest title, so menus,
/// warnings, trailers and other short titles are skipped automatically.
/// </summary>
public static class DvdVideoConverter
{
    private static readonly object LocatorLock = new();
    private static string? _ffmpegPath;
    private static string? _ffprobePath;

    public const string InstallHint =
        "ffmpeg was not found. Install it with 'winget install Gyan.FFmpeg' and restart CDBurner10, " +
        "or place ffmpeg.exe and ffprobe.exe next to CDBurner10.exe.";

    public static bool IsAvailable => FindFfmpeg() is not null && FindFfprobe() is not null;

    public static string? FfmpegPath => FindFfmpeg();
    public static string? FfprobePath => FindFfprobe();

    public static void ResetToolCache()
    {
        lock (LocatorLock)
        {
            _ffmpegPath = null;
            _ffprobePath = null;
        }
    }

    public static string? FindFfmpeg()
    {
        lock (LocatorLock)
        {
            if (!string.IsNullOrWhiteSpace(_ffmpegPath) && File.Exists(_ffmpegPath))
                return _ffmpegPath;

            _ffmpegPath = FindTool("ffmpeg.exe");
            return _ffmpegPath;
        }
    }

    public static string? FindFfprobe()
    {
        lock (LocatorLock)
        {
            if (!string.IsNullOrWhiteSpace(_ffprobePath) && File.Exists(_ffprobePath))
                return _ffprobePath;

            var ffmpeg = FindFfmpeg();
            if (!string.IsNullOrWhiteSpace(ffmpeg))
            {
                var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
                if (File.Exists(sibling))
                {
                    _ffprobePath = sibling;
                    return sibling;
                }
            }

            _ffprobePath = FindTool("ffprobe.exe");
            return _ffprobePath;
        }
    }

    private static string? FindTool(string fileName)
    {
        foreach (var candidate in GetToolCandidates(fileName))
        {
            try
            {
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static IEnumerable<string> GetToolCandidates(string fileName)
    {
        yield return Path.Combine(AppContext.BaseDirectory, fileName);
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg", fileName);
        yield return Path.Combine(AppContext.BaseDirectory, "tools", fileName);

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                yield return Path.Combine(directory.Trim().Trim('"'), fileName);
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string wingetPackages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        foreach (var candidate in FindInDirectories(wingetPackages, "Gyan.FFmpeg*", fileName))
            yield return candidate;

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", fileName);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", fileName);
    }

    private static IEnumerable<string> FindInDirectories(string root, string directoryPattern, string fileName)
    {
        if (!Directory.Exists(root)) yield break;

        string[] packages;
        try
        {
            packages = Directory.GetDirectories(root, directoryPattern, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        foreach (var package in packages)
        {
            List<string> matches;
            try
            {
                matches = Directory.EnumerateFiles(package, fileName, SearchOption.AllDirectories).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var match in matches)
                yield return match;
        }
    }

    /// <summary>
    /// VIDEO_TS folders are passed as their parent disc root, while ISO files
    /// are passed directly to ffmpeg's dvdvideo demuxer.
    /// </summary>
    public static string NormalizeSource(string path)
    {
        string full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) return full;

        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        if (name.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(trimmed) ?? trimmed;

        if (Directory.Exists(Path.Combine(full, "VIDEO_TS")))
            return full;

        return full;
    }

    public static void ValidateSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("Select a DVD-Video ISO file or a VIDEO_TS folder.");

        string normalized = NormalizeSource(source);
        if (File.Exists(normalized)) return;

        if (Directory.Exists(normalized))
        {
            string videoTs = Directory.Exists(Path.Combine(normalized, "VIDEO_TS"))
                ? Path.Combine(normalized, "VIDEO_TS")
                : normalized;
            if (File.Exists(Path.Combine(videoTs, "VIDEO_TS.IFO")))
                return;
            throw new InvalidOperationException("The selected folder does not contain a VIDEO_TS.IFO file.");
        }

        throw new InvalidOperationException($"The source '{source}' was not found.");
    }

    public static Task<DvdVideoInfo> ProbeAsync(string source, CancellationToken ct = default) =>
        ProbeAsync(source, titleNumber: 0, ct);

    public static async Task<DvdVideoInfo> ProbeAsync(string source, int titleNumber, CancellationToken ct = default)
    {
        ValidateSource(source);
        string ffprobe = FindFfprobe() ?? throw new InvalidOperationException(InstallHint);

        var args = new List<string>
        {
            "-v", "error",
            "-f", "dvdvideo"
        };
        if (titleNumber > 0)
        {
            args.Add("-title");
            args.Add(Math.Clamp(titleNumber, 1, 99).ToString(CultureInfo.InvariantCulture));
        }

        args.AddRange(new[]
        {
            "-show_entries", "format=duration:stream=index,codec_type,codec_name,channels,width,height,duration:stream_tags=language,title,DURATION",
            "-of", "json",
            NormalizeSource(source)
        });

        var result = await RunCaptureAsync(ffprobe, args, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.StdErr)
                    ? "ffprobe could not read the DVD-Video source."
                    : result.StdErr.Trim());

        return ParseProbeJson(result.StdOut);
    }

    internal static DvdVideoInfo ParseProbeJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double durationSeconds = 0;
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durationElement) &&
            double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            durationSeconds = parsed;
        }

        int width = 0, height = 0;
        double fallbackStreamDuration = 0;
        var audio = new List<DvdAudioTrack>();
        var subtitles = new List<DvdSubtitleTrack>();

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                string type = stream.TryGetProperty("codec_type", out var codecType) ? codecType.GetString() ?? "" : "";
                string codec = stream.TryGetProperty("codec_name", out var codecName) ? codecName.GetString() ?? "" : "";
                int index = stream.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) ? i : 0;
                string? language = GetTag(stream, "language");
                string? title = GetTag(stream, "title");

                if (fallbackStreamDuration <= 0 &&
                    stream.TryGetProperty("duration", out var streamDur) &&
                    double.TryParse(streamDur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedStreamDur) &&
                    parsedStreamDur > 0)
                {
                    fallbackStreamDuration = parsedStreamDur;
                }
                else if (fallbackStreamDuration <= 0 &&
                         GetTag(stream, "DURATION") is { } tagDur &&
                         TimeSpan.TryParse(tagDur, CultureInfo.InvariantCulture, out var parsedTs) &&
                         parsedTs.TotalSeconds > 0)
                {
                    fallbackStreamDuration = parsedTs.TotalSeconds;
                }

                if (type == "video" && width == 0)
                {
                    if (stream.TryGetProperty("width", out var w) && w.TryGetInt32(out var ww)) width = ww;
                    if (stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var hh)) height = hh;
                }
                else if (type == "audio")
                {
                    int channels = stream.TryGetProperty("channels", out var c) && c.TryGetInt32(out var cc) ? cc : 0;
                    audio.Add(new DvdAudioTrack(index, audio.Count, codec, channels, language, title));
                }
                else if (type == "subtitle")
                {
                    subtitles.Add(new DvdSubtitleTrack(index, subtitles.Count, codec, language));
                }
            }
        }

        if (durationSeconds <= 0 && fallbackStreamDuration > 0)
            durationSeconds = fallbackStreamDuration;

        return new DvdVideoInfo(
            TimeSpan.FromSeconds(durationSeconds),
            width,
            height,
            audio,
            subtitles);
    }

    private static string? GetTag(JsonElement stream, string name)
    {
        if (!stream.TryGetProperty("tags", out var tags)) return null;
        return tags.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    internal static List<string> BuildFfmpegArguments(
        string normalizedSource,
        string destination,
        DvdVideoInfo info,
        DvdMp4Options options)
    {
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "warning", "-nostdin",
            "-f", "dvdvideo"
        };

        if (options.TitleNumber > 0)
        {
            args.Add("-title");
            args.Add(Math.Clamp(options.TitleNumber, 1, 99).ToString(CultureInfo.InvariantCulture));
        }

        args.AddRange(new[] { "-i", normalizedSource });

        bool burnSubtitles = options.SubtitleOrdinal is int subIdx &&
                             subIdx >= 0 && subIdx < info.SubtitleTracks.Count;

        if (burnSubtitles)
        {
            int subtitleOrdinal = options.SubtitleOrdinal!.Value;
            string filter = options.Deinterlace
                ? $"[0:v]bwdif=0:-1:1[v0];[v0][0:s:{subtitleOrdinal}]overlay=eof_action=pass[v]"
                : $"[0:v][0:s:{subtitleOrdinal}]overlay=eof_action=pass[v]";
            args.AddRange(new[]
            {
                "-filter_complex", filter,
                "-map", "[v]"
            });
        }
        else
        {
            args.AddRange(new[] { "-map", "0:v:0" });
            if (options.Deinterlace)
            {
                args.AddRange(new[] { "-vf", "bwdif=0:-1:1" });
            }
        }

        DvdAudioTrack? selectedAudio = null;
        if (info.AudioTracks.Count > 0)
        {
            int audioOrdinal = Math.Clamp(options.AudioOrdinal, 0, info.AudioTracks.Count - 1);
            selectedAudio = info.AudioTracks[audioOrdinal];
            args.AddRange(new[] { "-map", $"0:a:{audioOrdinal}" });
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(new[]
        {
            "-c:v", "libx264",
            "-preset", options.Preset,
            "-crf", options.Crf.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p"
        });

        if (selectedAudio is not null)
        {
            bool keepSurround = !options.StereoDownmix && selectedAudio.Channels > 2;
            string audioBitrate = keepSurround ? "384k" : "192k";
            args.AddRange(new[]
            {
                "-c:a", "aac",
                "-b:a", audioBitrate
            });
            if (options.StereoDownmix)
                args.AddRange(new[] { "-ac", "2" });
        }

        args.AddRange(new[] { "-movflags", "+faststart", "-progress", "pipe:1", "-nostats" });

        if (options.MaxDuration is TimeSpan max && max > TimeSpan.Zero)
            args.AddRange(new[] { "-t", max.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) });

        args.Add(destination);
        return args;
    }

    public static async Task ConvertAsync(
        string source,
        string destination,
        DvdVideoInfo info,
        DvdMp4Options options,
        IProgress<DvdMp4Progress>? progress = null,
        CancellationToken ct = default)
    {
        ValidateSource(source);
        string ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(InstallHint);

        if (string.IsNullOrWhiteSpace(destination))
            throw new InvalidOperationException("Select an output MP4 file.");

        string normalizedSource = NormalizeSource(source);
        if (string.Equals(
                Path.GetFullPath(normalizedSource),
                Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The output MP4 must be different from the source DVD.");
        }

        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var args = BuildFfmpegArguments(normalizedSource, destination, info, options);

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        var errors = new ConcurrentQueue<string>();
        double effectiveDurationMs = info.Duration.TotalMilliseconds;
        if (options.MaxDuration is TimeSpan maxDur && maxDur > TimeSpan.Zero)
        {
            effectiveDurationMs = effectiveDurationMs > 0
                ? Math.Min(effectiveDurationMs, maxDur.TotalMilliseconds)
                : maxDur.TotalMilliseconds;
        }
        var totalMs = Math.Max(1, effectiveDurationMs);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not start ffmpeg: {ex.Message}", ex);
        }

        using CancellationTokenRegistration registration = ct.Register(() => TryKill(process));

        var errorReader = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    errors.Enqueue(line);
                    while (errors.Count > 40 && errors.TryDequeue(out _)) { }
                }
            }
            catch
            {
                // The stream closes when ffmpeg is cancelled or exits.
            }
        });

        var progressReader = Task.Run(async () =>
        {
            try
            {
                double speed = 0, fps = 0;
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    string key = line[..separator];
                    string value = line[(separator + 1)..];

                    if (key is "speed" && value.EndsWith('x') &&
                        double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSpeed))
                    {
                        speed = parsedSpeed;
                    }
                    else if (key is "fps" &&
                             double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFps))
                    {
                        fps = parsedFps;
                    }
                    else if (key is "out_time_us" or "out_time_ms" &&
                             long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
                    {
                        double percent = Math.Clamp(micros / (totalMs * 1000.0) * 100.0, 0, 100);
                        double remainingSeconds = speed > 0
                            ? Math.Max(0, (totalMs - micros / 1000.0) / 1000.0 / speed)
                            : 0;
                        progress?.Report(new DvdMp4Progress(
                            (int)percent,
                            speed,
                            fps,
                            TimeSpan.FromSeconds(remainingSeconds),
                            $"Converting to MP4... {(int)percent}%"));
                    }
                }
            }
            catch
            {
                // The stream closes when ffmpeg is cancelled or exits.
            }
        });

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await Task.WhenAll(errorReader, progressReader);
            TryDelete(destination);
            throw;
        }

        await Task.WhenAll(errorReader, progressReader);

        if (ct.IsCancellationRequested)
        {
            TryDelete(destination);
            throw new OperationCanceledException(ct);
        }

        if (process.ExitCode != 0)
        {
            TryDelete(destination);
            string message = errors.IsEmpty
                ? $"ffmpeg exited with code {process.ExitCode}."
                : string.Join(Environment.NewLine, errors);
            throw new InvalidOperationException($"MP4 conversion failed:{Environment.NewLine}{message}");
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCaptureAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not start {Path.GetFileName(fileName)}: {ex.Message}", ex);
        }

        using CancellationTokenRegistration registration = ct.Register(() => TryKill(process));
        Task<string>? stdout = null;
        Task<string>? stderr = null;

        try
        {
            stdout = process.StandardOutput.ReadToEndAsync(ct);
            stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try
            {
                var pending = new[] { stdout, stderr }.Where(t => t is not null).ToArray();
                if (pending.Length > 0) await Task.WhenAll(pending!);
            }
            catch
            {
                // Streams close when the process is killed.
            }
            throw;
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process already exited.
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public static string GetLanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "Unknown language";

        return code.Trim().ToLowerInvariant() switch
        {
            "eng" or "en" => "English",
            "cze" or "ces" or "cs" => "Czech",
            "slo" or "slk" or "sk" => "Slovak",
            "ger" or "deu" or "de" => "German",
            "fre" or "fra" or "fr" => "French",
            "spa" or "es" => "Spanish",
            "ita" or "it" => "Italian",
            "pol" or "pl" => "Polish",
            "hun" or "hu" => "Hungarian",
            "rus" or "ru" => "Russian",
            "ukr" or "uk" => "Ukrainian",
            "jpn" or "ja" => "Japanese",
            "chi" or "zho" or "zh" => "Chinese",
            "kor" or "ko" => "Korean",
            "por" or "pt" => "Portuguese",
            "dut" or "nld" or "nl" => "Dutch",
            "swe" or "sv" => "Swedish",
            "nor" or "no" => "Norwegian",
            "dan" or "da" => "Danish",
            "fin" or "fi" => "Finnish",
            "gre" or "ell" or "el" => "Greek",
            "tur" or "tr" => "Turkish",
            "ara" or "ar" => "Arabic",
            "heb" or "he" => "Hebrew",
            "hin" or "hi" => "Hindi",
            _ => code.Trim().ToUpperInvariant()
        };
    }

    public static string GetChannelName(int channels) => channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        6 => "5.1",
        8 => "7.1",
        > 0 => $"{channels} ch",
        _ => "Audio"
    };
}
