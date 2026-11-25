using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CDBurner10.Core.Dvd;

/// <summary>
/// Probes source video files with FFprobe and calculates DVD-Video bitrates,
/// aspect ratios, and estimated disc usage.
/// </summary>
public static class DvdMediaProber
{
    /// <summary>Usable payload capacity of a single-layer DVD-5 (~4,350 MiB).</summary>
    public const long Dvd5UsableBytes = 4350L * 1024 * 1024;

    /// <summary>Usable payload capacity of a dual-layer DVD-9 (~7,900 MiB).</summary>
    public const long Dvd9UsableBytes = 7900L * 1024 * 1024;

    /// <summary>Minimum allowed MPEG-2 video bitrate in kbps.</summary>
    public const int MinVideoBitrateKbps = 1500;

    /// <summary>Maximum allowed MPEG-2 video bitrate in kbps.</summary>
    public const int MaxVideoBitrateKbps = 8500;

    /// <summary>Maximum combined video + audio bitrate in kbps for DVD-Video compliance.</summary>
    public const int MaxTotalBitrateKbps = 9400;

    /// <summary>Default per-stream AC-3 audio bitrate in kbps.</summary>
    public const int DefaultAudioBitrateKbps = 192;

    /// <summary>DVD-Video MPEG-PS / VOB / IFO muxing overhead multiplier (~5%).</summary>
    private const double MuxOverheadFactor = 1.05;

    /// <summary>
    /// Probes a video file using <c>ffprobe</c> to extract duration, dimensions,
    /// display aspect ratio, and audio/subtitle stream counts.
    /// </summary>
    /// <param name="filePath">Path to the source video file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="DvdMediaInfo"/> describing the source file.</returns>
    public static async Task<DvdMediaInfo> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Source video file path must not be empty.", nameof(filePath));

        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Source video file not found: '{fullPath}'.", fullPath);

        string ffprobe = DvdVideoConverter.FindFfprobe()
            ?? throw new InvalidOperationException(DvdVideoConverter.InstallHint);

        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-print_format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add(fullPath);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not start ffprobe: {ex.Message}", ex);
        }

        using CancellationTokenRegistration registration = ct.Register(() => TryKill(process));
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;

        try
        {
            stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(stderr)
                    ? $"ffprobe exited with code {process.ExitCode}."
                    : stderr.Trim();
                throw new InvalidOperationException($"Could not probe '{Path.GetFileName(fullPath)}': {detail}");
            }

            if (string.IsNullOrWhiteSpace(stdout))
                throw new InvalidOperationException($"ffprobe returned empty output for '{Path.GetFileName(fullPath)}'.");

            return ParseProbeJson(fullPath, stdout);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainStreamsQuietlyAsync(stdoutTask, stderrTask);
            throw;
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    /// <summary>
    /// Calculates the target MPEG-2 video bitrate in kbps for the given total duration,
    /// target disc capacity, quality preset, and audio stream count.
    /// </summary>
    /// <param name="totalDuration">Combined duration of all titles to place on the disc.</param>
    /// <param name="targetDisc">Target DVD disc capacity.</param>
    /// <param name="quality">Requested quality preset.</param>
    /// <param name="audioStreams">Number of encoded audio streams per title (default 1).</param>
    /// <returns>Target video bitrate in kbps, clamped to [1500, 8500] and keeping total video+audio &lt;= 9400 kbps.</returns>
    public static int CalculateVideoBitrateKbps(
        TimeSpan totalDuration,
        DvdTargetDisc targetDisc,
        DvdQualityPreset quality,
        int audioStreams = 1)
    {
        int clampedAudioStreams = Math.Clamp(audioStreams, 0, 8);
        int audioBitrateKbps = clampedAudioStreams * DefaultAudioBitrateKbps;
        int maxVideoForCompliance = Math.Min(MaxVideoBitrateKbps, Math.Max(MinVideoBitrateKbps, MaxTotalBitrateKbps - audioBitrateKbps));

        int rawTargetKbps = quality switch
        {
            DvdQualityPreset.High => 8000,
            DvdQualityPreset.Medium => 5500,
            DvdQualityPreset.Low => 3500,
            DvdQualityPreset.BestFit => CalculateBestFitBitrateKbps(totalDuration, targetDisc, audioBitrateKbps, maxVideoForCompliance),
            _ => 5500
        };

        return Math.Clamp(rawTargetKbps, MinVideoBitrateKbps, maxVideoForCompliance);
    }

    /// <summary>
    /// Resolves the output DVD display aspect ratio, automatically choosing 16:9 when
    /// the probed display aspect ratio is at least 1.55 and 4:3 otherwise.
    /// </summary>
    /// <param name="requested">User-selected aspect ratio mode.</param>
    /// <param name="probedDar">Probed source display aspect ratio (width / height).</param>
    /// <returns>Either <see cref="DvdAspectRatio.Widescreen16x9"/> or <see cref="DvdAspectRatio.Standard4x3"/>.</returns>
    public static DvdAspectRatio ResolveAspectRatio(DvdAspectRatio requested, double probedDar)
    {
        if (requested != DvdAspectRatio.Auto)
            return requested;

        return probedDar >= 1.55
            ? DvdAspectRatio.Widescreen16x9
            : DvdAspectRatio.Standard4x3;
    }

    /// <summary>
    /// Estimates the total DVD disc size in bytes for the specified duration and bitrates,
    /// including ~5% MPEG-PS / DVD-Video muxing overhead.
    /// </summary>
    /// <param name="totalDuration">Combined duration of all titles.</param>
    /// <param name="videoBitrateKbps">Target MPEG-2 video bitrate in kbps.</param>
    /// <param name="audioBitrateKbps">Combined audio bitrate in kbps (default 192).</param>
    /// <returns>Estimated authored disc size in bytes.</returns>
    public static long EstimateDiscBytes(
        TimeSpan totalDuration,
        int videoBitrateKbps,
        int audioBitrateKbps = DefaultAudioBitrateKbps)
    {
        if (totalDuration <= TimeSpan.Zero)
            return 0;

        double totalKbps = Math.Max(0, videoBitrateKbps) + Math.Max(0, audioBitrateKbps);
        double rawBytes = (totalKbps * 1000.0 / 8.0) * totalDuration.TotalSeconds;
        return (long)Math.Ceiling(rawBytes * MuxOverheadFactor);
    }

    /// <summary>
    /// Parses the JSON output of <c>ffprobe -show_format -show_streams</c> into a <see cref="DvdMediaInfo"/>.
    /// </summary>
    internal static DvdMediaInfo ParseProbeJson(string filePath, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double durationSeconds = 0;
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durEl) &&
            TryParseDouble(durEl, out double parsedFormatDuration) &&
            parsedFormatDuration > 0)
        {
            durationSeconds = parsedFormatDuration;
        }

        int width = 0;
        int height = 0;
        double dar = 0;
        double fallbackStreamDuration = 0;
        int audioCount = 0;
        int subtitleCount = 0;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                string codecType = stream.TryGetProperty("codec_type", out var ctEl)
                    ? ctEl.GetString() ?? string.Empty
                    : string.Empty;

                if (fallbackStreamDuration <= 0)
                {
                    if (stream.TryGetProperty("duration", out var streamDurEl) &&
                        TryParseDouble(streamDurEl, out double parsedStreamDur) &&
                        parsedStreamDur > 0)
                    {
                        fallbackStreamDuration = parsedStreamDur;
                    }
                    else if (TryGetTag(stream, "DURATION") is { } tagDur &&
                             TimeSpan.TryParse(tagDur, CultureInfo.InvariantCulture, out var parsedTs) &&
                             parsedTs.TotalSeconds > 0)
                    {
                        fallbackStreamDuration = parsedTs.TotalSeconds;
                    }
                }

                if (codecType == "video" && width == 0)
                {
                    if (stream.TryGetProperty("width", out var wEl) && wEl.TryGetInt32(out int w))
                        width = w;
                    if (stream.TryGetProperty("height", out var hEl) && hEl.TryGetInt32(out int h))
                        height = h;

                    dar = ResolveStreamDar(stream, width, height);
                }
                else if (codecType == "audio")
                {
                    audioCount++;
                }
                else if (codecType == "subtitle")
                {
                    subtitleCount++;
                }
            }
        }

        if (durationSeconds <= 0 && fallbackStreamDuration > 0)
            durationSeconds = fallbackStreamDuration;

        if (dar <= 0)
            dar = height > 0 ? (double)width / height : 16.0 / 9.0;

        TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, durationSeconds));
        string summary = FormatSummary(width, height, duration, audioCount, subtitleCount);

        return new DvdMediaInfo(
            filePath,
            duration,
            width,
            height,
            dar,
            audioCount,
            subtitleCount,
            summary);
    }

    private static int CalculateBestFitBitrateKbps(
        TimeSpan totalDuration,
        DvdTargetDisc targetDisc,
        int audioBitrateKbps,
        int maxVideoForCompliance)
    {
        if (totalDuration <= TimeSpan.Zero)
            return maxVideoForCompliance;

        double usableBytes = targetDisc == DvdTargetDisc.Dvd9_8_5GB
            ? Dvd9UsableBytes
            : Dvd5UsableBytes;

        double payloadBytes = usableBytes / MuxOverheadFactor;
        double totalKbps = (payloadBytes * 8.0) / totalDuration.TotalSeconds / 1000.0;
        return (int)Math.Floor(totalKbps - audioBitrateKbps);
    }

    private static double ResolveStreamDar(JsonElement stream, int width, int height)
    {
        if (stream.TryGetProperty("display_aspect_ratio", out var darEl) &&
            TryParseRatio(darEl.GetString(), out double parsedDar) &&
            parsedDar > 0.1)
        {
            return parsedDar;
        }

        if (width > 0 && height > 0)
        {
            if (stream.TryGetProperty("sample_aspect_ratio", out var sarEl) &&
                TryParseRatio(sarEl.GetString(), out double parsedSar) &&
                parsedSar > 0.1)
            {
                return (width * parsedSar) / height;
            }

            return (double)width / height;
        }

        return 16.0 / 9.0;
    }

    private static bool TryParseRatio(string? text, out double ratio)
    {
        ratio = 0;
        if (string.IsNullOrWhiteSpace(text) ||
            text.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int colon = text.IndexOf(':');
        if (colon > 0 && colon < text.Length - 1)
        {
            if (double.TryParse(text[..colon], NumberStyles.Float, CultureInfo.InvariantCulture, out double num) &&
                double.TryParse(text[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double den) &&
                num > 0 && den > 0)
            {
                ratio = num / den;
                return true;
            }
            return false;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double direct) && direct > 0)
        {
            ratio = direct;
            return true;
        }

        return false;
    }

    private static bool TryParseDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
            return true;

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string? TryGetTag(JsonElement stream, string name)
    {
        if (!stream.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
            return null;

        return tags.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static string FormatSummary(int width, int height, TimeSpan duration, int audioCount, int subtitleCount)
    {
        var parts = new List<string>(4);
        if (width > 0 && height > 0)
            parts.Add($"{width}×{height}");

        string durText = duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{duration.Minutes:D2}:{duration.Seconds:D2}";
        parts.Add(durText);

        parts.Add(audioCount == 1 ? "1 audio" : $"{audioCount} audio");

        if (subtitleCount > 0)
            parts.Add(subtitleCount == 1 ? "1 sub" : $"{subtitleCount} subs");

        return string.Join(" • ", parts);
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

    private static async Task DrainStreamsQuietlyAsync(params Task<string>?[] tasks)
    {
        try
        {
            var pending = tasks.Where(t => t is not null).ToArray();
            if (pending.Length > 0)
                await Task.WhenAll(pending!);
        }
        catch
        {
            // Streams close when the process is killed.
        }
    }
}
