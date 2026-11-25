using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CDBurner10.Core.Burning;

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
    IReadOnlyList<DvdSubtitleTrack> SubtitleTracks)
{
    /// <summary>Resolved DVD title number (1..99) selected for probing and conversion.</summary>
    public int TitleNumber { get; init; } = 1;

    /// <summary>Total number of titles detected on the disc (0 if unknown).</summary>
    public int TotalTitles { get; init; }

    /// <summary>True when ffmpeg's dvdvideo demuxer needed -trim 0 (padding cells in PGC).</summary>
    public bool DisableTrim { get; init; }

    /// <summary>True when ffmpeg's dvdvideo demuxer could not open the disc directly and DvdNavigationWalker should walk it first.</summary>
    public bool UseNavigatedWalker { get; init; }

    /// <summary>True when the disc is an unfinalized DVD-R/RW with no UDF/VIDEO_TS.IFO and is recovered via raw MPEG-2 sectors.</summary>
    public bool IsUnfinalizedRaw { get; init; }

    /// <summary>True when the source is already a standalone MPEG-2 Program Stream (.mpg / .mpeg) file.</summary>
    public bool IsPreWalkedStream { get; init; }

    /// <summary>Scan metadata for unfinalized DVD raw sector recovery.</summary>
    public UnfinalizedDvdScanResult? UnfinalizedScan { get; init; }
}

public enum DvdVideoEncoder
{
    /// <summary>Use hardware GPU H.264 encoding (NVENC / QSV / AMF / MediaFoundation) when available, falling back to libx264.</summary>
    Auto,
    /// <summary>Always use CPU software encoding (libx264).</summary>
    Software
}

public enum DvdMp4AspectRatio
{
    /// <summary>Preserve the aspect ratio flagged by the source DVD/stream.</summary>
    Auto,
    /// <summary>Force 16:9 widescreen display aspect ratio (fixes misflagged 4:3 DVD-recorder broadcasts).</summary>
    Widescreen16x9,
    /// <summary>Force 4:3 standard display aspect ratio.</summary>
    Standard4x3
}

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

    /// <summary>Hardware GPU acceleration (Auto) or CPU software (Software).</summary>
    public DvdVideoEncoder Encoder { get; init; } = DvdVideoEncoder.Auto;

    /// <summary>Display aspect ratio override (Auto keeps source flags; 16:9 / 4:3 overrides misflagged DVD-recorder streams).</summary>
    public DvdMp4AspectRatio AspectRatio { get; init; } = DvdMp4AspectRatio.Auto;

    /// <summary>Optional optical drive read speed cap in KB/s (0 = maximum).</summary>
    public int ReadSpeedKBps { get; init; }

    /// <summary>Optional limit, mainly useful for previews and tests.</summary>
    public TimeSpan? MaxDuration { get; init; }
}

public sealed record DvdMp4Progress(
    int Percent,
    double Speed,
    double Fps,
    TimeSpan TimeRemaining,
    string Message,
    string? LogLine = null);

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
    private static bool _hwEncoderProbed;
    private static string? _hwEncoder;

    private static readonly string[] HardwareEncoderCandidates =
    [
        "h264_nvenc",
        "h264_qsv",
        "h264_amf",
        "h264_mf"
    ];

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
            _hwEncoderProbed = false;
            _hwEncoder = null;
        }
    }

    /// <summary>
    /// Detects the best working GPU H.264 encoder in ffmpeg (NVENC → QSV → AMF → MediaFoundation).
    /// Cached after the first probe. Returns null if only CPU libx264 is available.
    /// </summary>
    public static string? DetectHardwareEncoder()
    {
        lock (LocatorLock)
        {
            if (_hwEncoderProbed) return _hwEncoder;

            string? ffmpeg = FindFfmpeg();
            if (string.IsNullOrWhiteSpace(ffmpeg))
            {
                _hwEncoderProbed = true;
                _hwEncoder = null;
                return null;
            }

            foreach (var candidate in HardwareEncoderCandidates)
            {
                if (ProbeEncoder(ffmpeg, candidate))
                {
                    _hwEncoder = candidate;
                    break;
                }
            }

            _hwEncoderProbed = true;
            return _hwEncoder;
        }
    }

    private static bool ProbeEncoder(string ffmpeg, string encoder)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "color=c=black:s=128x128:d=0.1:r=25",
                "-frames:v", "2",
                "-pix_fmt", "yuv420p",
                "-c:v", encoder,
                "-f", "null", "-"
            })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null) return false;
            if (!process.WaitForExit(4000))
            {
                TryKill(process);
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static string ResolveVideoEncoder(DvdVideoEncoder preference) =>
        preference == DvdVideoEncoder.Auto
            ? DetectHardwareEncoder() ?? "libx264"
            : "libx264";

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
    /// Normalizes a DVD source: optical drive letters ("E", "E:", "E:\", "\\.\E:") become "E:"
    /// (without trailing backslash, which libdvdnav requires on Windows),
    /// VIDEO_TS folders become their parent disc root, and ISO files are returned as full paths.
    /// </summary>
    public static string NormalizeSource(string path)
    {
        string trimmedInput = path.Trim();
        if (TryExtractDriveLetter(trimmedInput, out char driveLetter))
            return $"{char.ToUpperInvariant(driveLetter)}:";

        string full = Path.GetFullPath(trimmedInput);
        if (!Directory.Exists(full)) return full;

        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        if (name.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(trimmed) ?? trimmed;

        if (Directory.Exists(Path.Combine(full, "VIDEO_TS")))
            return full;

        return full;
    }

    public static string GetDriveSourcePath(string driveLetter)
    {
        if (TryExtractDriveLetter(driveLetter, out char letter))
            return $"{char.ToUpperInvariant(letter)}:";
        throw new ArgumentException($"Invalid optical drive letter '{driveLetter}'.", nameof(driveLetter));
    }

    public static bool TryExtractDriveLetter(string? input, out char letter)
    {
        letter = '\0';
        if (string.IsNullOrWhiteSpace(input)) return false;

        string s = input.Trim();
        if (s.StartsWith(@"\\.\", StringComparison.Ordinal))
            s = s[4..];

        if (s.Length == 1 && char.IsAsciiLetter(s[0]))
        {
            letter = char.ToUpperInvariant(s[0]);
            return true;
        }
        if (s.Length == 2 && char.IsAsciiLetter(s[0]) && s[1] == ':')
        {
            letter = char.ToUpperInvariant(s[0]);
            return true;
        }
        if (s.Length == 3 && char.IsAsciiLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/'))
        {
            letter = char.ToUpperInvariant(s[0]);
            return true;
        }
        return false;
    }

    public static string GetSuggestedDriveOutputName(string driveLetter)
    {
        try
        {
            if (TryExtractDriveLetter(driveLetter, out char letter))
            {
                var di = new DriveInfo($"{letter}:\\");
                if (di.IsReady && !string.IsNullOrWhiteSpace(di.VolumeLabel))
                {
                    var invalid = Path.GetInvalidFileNameChars();
                    string clean = new string(di.VolumeLabel.Trim().Where(c => !invalid.Contains(c)).ToArray()).Trim();
                    if (!string.IsNullOrWhiteSpace(clean))
                        return clean;
                }
            }
        }
        catch
        {
            // Fall back to default name.
        }
        return "DVDMovie";
    }

    public static void ValidateSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("Select an optical drive, a DVD-Video ISO file, or a VIDEO_TS folder.");

        string normalized = NormalizeSource(source);
        if (TryExtractDriveLetter(normalized, out char letter))
        {
            string driveRoot = $"{letter}:\\";
            if (File.Exists(Path.Combine(driveRoot, "VIDEO_TS", "VIDEO_TS.IFO")))
                return;
            if (UnfinalizedDvdReader.TryScanDrive(letter.ToString(), out _))
                return;
            throw new InvalidOperationException($"Drive {driveRoot} does not contain a DVD-Video VIDEO_TS.IFO file or readable raw MPEG-2 video.");
        }

        if (File.Exists(normalized)) return;

        if (Directory.Exists(normalized))
        {
            string videoTs = Directory.Exists(Path.Combine(normalized, "VIDEO_TS"))
                ? Path.Combine(normalized, "VIDEO_TS")
                : normalized;
            if (File.Exists(Path.Combine(videoTs, "VIDEO_TS.IFO")))
                return;
            throw new InvalidOperationException("The selected drive or folder does not contain a DVD-Video VIDEO_TS.IFO file.");
        }

        throw new InvalidOperationException($"The source '{source}' was not found.");
    }

    public static Task<DvdVideoInfo> ProbeAsync(string source, CancellationToken ct = default) =>
        ProbeAsync(source, titleNumber: 0, ct);

    public static bool IsMpegStreamFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string ext = Path.GetExtension(path.Trim());
        return ext.Equals(".mpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".mpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".m2p", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<DvdVideoInfo> ProbeAsync(string source, int titleNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("Select an optical drive, a DVD-Video ISO/MPG file, or a VIDEO_TS folder.");

        string ffprobe = FindFfprobe() ?? throw new InvalidOperationException(InstallHint);
        string normalized = NormalizeSource(source);

        // If this is an optical drive without a finalized VIDEO_TS.IFO filesystem, check for
        // raw MPEG-2 Program Stream sectors on an unfinalized DVD-R / DVD+R / DVD-RW.
        if (TryExtractDriveLetter(normalized, out char driveLetter) &&
            !File.Exists(Path.Combine($"{driveLetter}:\\", "VIDEO_TS", "VIDEO_TS.IFO")))
        {
            UnfinalizedDvdScanResult? scan = null;
            bool foundRaw = await Task.Run(() => UnfinalizedDvdReader.TryScanDrive(driveLetter.ToString(), out scan), ct);
            if (!foundRaw || scan is null)
                throw new InvalidOperationException(
                    $"Drive {driveLetter}:\\ does not contain a DVD-Video VIDEO_TS.IFO file or readable raw MPEG-2 video.");

            return await ProbeUnfinalizedDriveAsync(ffprobe, driveLetter.ToString(), scan, ct);
        }

        ValidateSource(source);

        // Standalone MPEG-2 Program Stream (.mpg / .mpeg) extracted via Main Movie or Unfinalized DVD recovery:
        if (!TryExtractDriveLetter(normalized, out _) && IsMpegStreamFile(normalized) && File.Exists(normalized))
        {
            var mpegArgs = new[]
            {
                "-v", "error",
                "-show_entries", "format=duration:stream=index,codec_type,codec_name,channels,width,height,duration:stream_tags=language,title,DURATION",
                "-of", "json",
                normalized
            };
            var mpegRes = await RunCaptureAsync(ffprobe, mpegArgs, ct);
            if (mpegRes.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(mpegRes.StdErr)
                        ? "ffprobe could not read the MPEG-2 video stream."
                        : mpegRes.StdErr.Trim());
            }

            return ParseProbeJson(mpegRes.StdOut) with
            {
                TitleNumber = 1,
                TotalTitles = 1,
                IsPreWalkedStream = true
            };
        }

        // ffmpeg's dvdvideo demuxer defaults -title 0 to Title 1 (which is often a 0-second
        // dummy/padding title on commercial DVDs). Enumerate titles via libdvdnav first so
        // auto-selection reliably picks the true longest title.
        int resolvedTitle = titleNumber;
        IReadOnlyList<DvdTitleInfo>? navTitles = null;
        if (DvdNavigationWalker.IsAvailable)
        {
            try
            {
                navTitles = await Task.Run(() => DvdNavigationWalker.EnumerateTitles(normalized), ct);
                if (navTitles.Count > 0 && resolvedTitle <= 0)
                {
                    resolvedTitle = DvdNavigationWalker.SelectMainTitle(navTitles).TitleNumber;
                }
            }
            catch
            {
                // Continue with ffprobe if libdvdnav enumeration is unavailable for this source.
            }
        }

        int totalTitles = navTitles?.Count ?? 0;
        var result = await RunFfprobeDvdAsync(ffprobe, normalized, resolvedTitle, disableTrim: false, ct);
        if (result.ExitCode == 0)
        {
            return ParseProbeJson(result.StdOut) with
            {
                TitleNumber = resolvedTitle > 0 ? resolvedTitle : 1,
                TotalTitles = totalTitles
            };
        }

        // Retry with -trim 0 when ffmpeg reports padding cells ("looks empty" / "-trim").
        if (NeedsTrimDisabled(result.StdErr))
        {
            var retry = await RunFfprobeDvdAsync(ffprobe, normalized, resolvedTitle, disableTrim: true, ct);
            if (retry.ExitCode == 0)
            {
                return ParseProbeJson(retry.StdOut) with
                {
                    TitleNumber = resolvedTitle > 0 ? resolvedTitle : 1,
                    TotalTitles = totalTitles,
                    DisableTrim = true
                };
            }
            result = retry;
        }

        // Fallback: use our native libdvdnav walker when ffmpeg's dvdvideo demuxer cannot read the disc.
        if (navTitles is { Count: > 0 })
        {
            var selected = resolvedTitle > 0
                ? navTitles.FirstOrDefault(t => t.TitleNumber == resolvedTitle) ?? DvdNavigationWalker.SelectMainTitle(navTitles)
                : DvdNavigationWalker.SelectMainTitle(navTitles);
            return new DvdVideoInfo(
                selected.Duration,
                720,
                576,
                [new DvdAudioTrack(0, 0, "ac3", 2, null, "Default audio")],
                [])
            {
                TitleNumber = selected.TitleNumber,
                TotalTitles = totalTitles,
                UseNavigatedWalker = true
            };
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(result.StdErr)
                ? "ffprobe could not read the DVD-Video source."
                : result.StdErr.Trim());
    }

    private static async Task<DvdVideoInfo> ProbeUnfinalizedDriveAsync(
        string ffprobe,
        string driveLetter,
        UnfinalizedDvdScanResult scan,
        CancellationToken ct)
    {
        string tempSample = Path.Combine(Path.GetTempPath(), $"cdburner10_unfinalized_probe_{Guid.NewGuid():N}.mpg");
        try
        {
            long sampleBytes = await Task.Run(() =>
                UnfinalizedDvdReader.ReadSampleToFile(driveLetter, tempSample, scan.FirstMpegSector, 1024 * 1024, ct), ct);
            if (sampleBytes <= 0)
                throw new InvalidOperationException($"Could not read MPEG-2 sample sectors from drive {driveLetter}:\\.");

            var args = new[]
            {
                "-v", "error",
                "-show_entries", "format=duration,bit_rate:stream=index,codec_type,codec_name,channels,width,height,duration:stream_tags=language,title,DURATION",
                "-of", "json",
                tempSample
            };
            var result = await RunCaptureAsync(ffprobe, args, ct);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.StdErr)
                        ? "ffprobe could not inspect the raw MPEG-2 stream on the unfinalized DVD."
                        : result.StdErr.Trim());

            var parsed = ParseProbeJson(result.StdOut);
            TimeSpan estimatedDuration = parsed.Duration;

            // Estimate full disc duration from sample bitrate and reported used bytes.
            using var doc = JsonDocument.Parse(result.StdOut);
            if (scan.EstimatedTotalBytes > 0 &&
                doc.RootElement.TryGetProperty("format", out var fmt) &&
                fmt.TryGetProperty("bit_rate", out var brEl) &&
                double.TryParse(brEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double bitRate) &&
                bitRate > 100_000)
            {
                double estSec = scan.EstimatedTotalBytes * 8.0 / bitRate;
                if (estSec > estimatedDuration.TotalSeconds)
                    estimatedDuration = TimeSpan.FromSeconds(estSec);
            }

            return parsed with
            {
                Duration = estimatedDuration,
                TitleNumber = 1,
                TotalTitles = 1,
                IsUnfinalizedRaw = true,
                UnfinalizedScan = scan
            };
        }
        finally
        {
            TryDelete(tempSample);
        }
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunFfprobeDvdAsync(
        string ffprobe,
        string normalizedSource,
        int titleNumber,
        bool disableTrim,
        CancellationToken ct)
    {
        var args = new List<string>
        {
            "-v", "error",
            "-f", "dvdvideo"
        };
        if (disableTrim)
        {
            args.Add("-trim");
            args.Add("0");
        }
        if (titleNumber > 0)
        {
            args.Add("-title");
            args.Add(Math.Clamp(titleNumber, 1, 99).ToString(CultureInfo.InvariantCulture));
        }

        args.AddRange(new[]
        {
            "-show_entries", "format=duration:stream=index,codec_type,codec_name,channels,width,height,duration:stream_tags=language,title,DURATION",
            "-of", "json",
            normalizedSource
        });

        return RunCaptureAsync(ffprobe, args, ct);
    }

    private static bool NeedsTrimDisabled(string? stderr) =>
        !string.IsNullOrWhiteSpace(stderr) &&
        (stderr.Contains("-trim", StringComparison.OrdinalIgnoreCase) ||
         stderr.Contains("looks empty", StringComparison.OrdinalIgnoreCase) ||
         stderr.Contains("padding cells", StringComparison.OrdinalIgnoreCase));

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
        DvdMp4Options options,
        string? videoEncoderOverride = null,
        bool isPreWalkedMpegStream = false,
        bool isPipedMpegStream = false)
    {
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "warning"
        };

        if (isPipedMpegStream)
        {
            args.AddRange(new[] { "-fflags", "+genpts+igndts", "-err_detect", "ignore_err", "-f", "mpeg", "-i", "pipe:0" });
        }
        else
        {
            args.Add("-nostdin");
            if (!isPreWalkedMpegStream)
            {
                args.AddRange(new[] { "-f", "dvdvideo" });
                if (info.DisableTrim)
                    args.AddRange(new[] { "-trim", "0" });
                int effectiveTitle = options.TitleNumber > 0
                    ? options.TitleNumber
                    : (info.TitleNumber > 1 ? info.TitleNumber : 0);
                if (effectiveTitle > 0)
                {
                    args.Add("-title");
                    args.Add(Math.Clamp(effectiveTitle, 1, 99).ToString(CultureInfo.InvariantCulture));
                }
            }

            args.AddRange(new[] { "-i", normalizedSource });
        }

        bool burnSubtitles = options.SubtitleOrdinal is int subIdx &&
                             subIdx >= 0 && subIdx < info.SubtitleTracks.Count;

        string? darFilter = options.AspectRatio switch
        {
            DvdMp4AspectRatio.Widescreen16x9 => "setdar=16/9",
            DvdMp4AspectRatio.Standard4x3 => "setdar=4/3",
            _ => null
        };
        string? aspectArg = options.AspectRatio switch
        {
            DvdMp4AspectRatio.Widescreen16x9 => "16:9",
            DvdMp4AspectRatio.Standard4x3 => "4:3",
            _ => null
        };

        if (burnSubtitles)
        {
            int subtitleOrdinal = options.SubtitleOrdinal!.Value;
            string postOverlay = darFilter is not null ? $",{darFilter}[v]" : "[v]";
            string filter = options.Deinterlace
                ? $"[0:v]bwdif=0:-1:1[v0];[v0][0:s:{subtitleOrdinal}]overlay=eof_action=pass{postOverlay}"
                : $"[0:v][0:s:{subtitleOrdinal}]overlay=eof_action=pass{postOverlay}";
            args.AddRange(new[]
            {
                "-filter_complex", filter,
                "-map", "[v]"
            });
        }
        else
        {
            args.AddRange(new[] { "-map", "0:v:0" });
            var vfParts = new List<string>(2);
            if (options.Deinterlace) vfParts.Add("bwdif=0:-1:1");
            if (darFilter is not null) vfParts.Add(darFilter);
            if (vfParts.Count > 0)
            {
                args.AddRange(new[] { "-vf", string.Join(",", vfParts) });
            }
        }

        if (aspectArg is not null)
        {
            args.AddRange(new[] { "-aspect", aspectArg });
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

        string encoder = !string.IsNullOrWhiteSpace(videoEncoderOverride)
            ? videoEncoderOverride
            : ResolveVideoEncoder(options.Encoder);

        AppendVideoEncoderArgs(args, encoder, options);

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

    private static void AppendVideoEncoderArgs(List<string> args, string encoder, DvdMp4Options options)
    {
        string crf = Math.Clamp(options.Crf, 0, 51).ToString(CultureInfo.InvariantCulture);
        switch (encoder)
        {
            case "h264_nvenc":
                args.AddRange(new[]
                {
                    "-c:v", "h264_nvenc",
                    "-preset", "p5",
                    "-rc", "vbr",
                    "-cq", crf,
                    "-b:v", "0",
                    "-pix_fmt", "yuv420p"
                });
                break;

            case "h264_qsv":
                args.AddRange(new[]
                {
                    "-c:v", "h264_qsv",
                    "-global_quality", crf,
                    "-pix_fmt", "yuv420p"
                });
                break;

            case "h264_amf":
                args.AddRange(new[]
                {
                    "-c:v", "h264_amf",
                    "-rc", "cqp",
                    "-qp_i", crf,
                    "-qp_p", crf,
                    "-pix_fmt", "yuv420p"
                });
                break;

            case "h264_mf":
            {
                int quality = options.Crf <= 19 ? 85 : options.Crf <= 22 ? 75 : 65;
                args.AddRange(new[]
                {
                    "-c:v", "h264_mf",
                    "-rate_control", "quality",
                    "-quality", quality.ToString(CultureInfo.InvariantCulture),
                    "-pix_fmt", "yuv420p"
                });
                break;
            }

            default:
                args.AddRange(new[]
                {
                    "-c:v", "libx264",
                    "-preset", options.Preset,
                    "-crf", crf,
                    "-pix_fmt", "yuv420p"
                });
                break;
        }
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
        if (TryExtractDriveLetter(normalizedSource, out char sourceDriveLetter))
        {
            CDBurner10.Core.Native.ScsiOpticalDevice.TrySetReadSpeed(sourceDriveLetter.ToString(), options.ReadSpeedKBps);
        }

        if (string.Equals(
                Path.GetFullPath(normalizedSource),
                Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The output MP4 must be different from the source DVD.");
        }

        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        string? tempWalkedMpg = null;
        string inputForFfmpeg = normalizedSource;
        bool isPreWalked = info.IsPreWalkedStream || (!TryExtractDriveLetter(normalizedSource, out _) && IsMpegStreamFile(normalizedSource));
        char rawDriveLetter = '\0';
        bool isPipedUnfinalized = info.IsUnfinalizedRaw && TryExtractDriveLetter(normalizedSource, out rawDriveLetter);

        try
        {
            if (!isPipedUnfinalized && !isPreWalked && info.UseNavigatedWalker && DvdNavigationWalker.IsAvailable)
            {
                tempWalkedMpg = Path.Combine(Path.GetTempPath(), $"cdburner10_dvdnav_{Guid.NewGuid():N}.mpg");
                var walkProgress = new Progress<BurnProgress>(bp =>
                {
                    int mapped = Math.Clamp(bp.Percent / 2, 0, 50);
                    progress?.Report(new DvdMp4Progress(
                        mapped,
                        0,
                        0,
                        bp.TimeRemaining,
                        bp.Message ?? $"Reading DVD via libdvdnav... {bp.Percent}%"));
                });

                int walkTitle = options.TitleNumber > 0 ? options.TitleNumber : info.TitleNumber;
                await Task.Run(() =>
                    DvdNavigationWalker.WalkTitleToFile(normalizedSource, tempWalkedMpg, walkTitle, walkProgress, ct), ct);

                inputForFfmpeg = tempWalkedMpg;
                isPreWalked = true;
            }

            var args = BuildFfmpegArguments(
                inputForFfmpeg,
                destination,
                info,
                options,
                isPreWalkedMpegStream: isPreWalked,
                isPipedMpegStream: isPipedUnfinalized);

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = isPipedUnfinalized,
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

            double latestSpeed = 0, latestFps = 0;

            Task pipeFeeder = Task.CompletedTask;
            if (isPipedUnfinalized)
            {
                string dl = rawDriveLetter.ToString();
                var scan = info.UnfinalizedScan;
                pipeFeeder = Task.Run(() =>
                {
                    var rawProgress = new Progress<BurnProgress>(bp =>
                    {
                        progress?.Report(new DvdMp4Progress(
                            Math.Clamp(bp.Percent, 0, 99),
                            Volatile.Read(ref latestSpeed),
                            Volatile.Read(ref latestFps),
                            bp.TimeRemaining,
                            bp.Message ?? $"Recovering & converting unfinalized DVD... {bp.Percent}%"));
                    });

                    try
                    {
                        UnfinalizedDvdReader.ReadToStream(dl, process.StandardInput.BaseStream, scan, rawProgress, ct);
                    }
                    catch (IOException)
                    {
                        // Pipe closed early (e.g. MaxDuration reached or ffmpeg exited).
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancelled by user.
                    }
                    finally
                    {
                        try { process.StandardInput.Close(); } catch { }
                    }
                });
            }

            var errorReader = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync() is { } line)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        errors.Enqueue(line);
                        while (errors.Count > 40 && errors.TryDequeue(out _)) { }
                        progress?.Report(new DvdMp4Progress(-1, 0, 0, TimeSpan.Zero, string.Empty, line));
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
                            Volatile.Write(ref latestSpeed, parsedSpeed);
                        }
                        else if (key is "fps" &&
                                 double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFps))
                        {
                            fps = parsedFps;
                            Volatile.Write(ref latestFps, parsedFps);
                        }
                        else if (!isPipedUnfinalized &&
                                 key is "out_time_us" or "out_time_ms" &&
                                 long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
                        {
                            double rawPercent = Math.Clamp(micros / (totalMs * 1000.0) * 100.0, 0, 100);
                            int displayPercent = isPreWalked
                                ? (int)Math.Clamp(50 + rawPercent * 0.5, 50, 100)
                                : (int)rawPercent;
                            double remainingSeconds = speed > 0
                                ? Math.Max(0, (totalMs - micros / 1000.0) / 1000.0 / speed)
                                : 0;
                            progress?.Report(new DvdMp4Progress(
                                displayPercent,
                                speed,
                                fps,
                                TimeSpan.FromSeconds(remainingSeconds),
                                $"Converting to MP4... {displayPercent}%"));
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
                await Task.WhenAll(pipeFeeder, errorReader, progressReader);
                TryDelete(destination);
                throw;
            }

            await Task.WhenAll(pipeFeeder, errorReader, progressReader);

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
        finally
        {
            if (!string.IsNullOrEmpty(tempWalkedMpg))
                TryDelete(tempWalkedMpg);
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
