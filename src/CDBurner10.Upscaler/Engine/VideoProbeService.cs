using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CDBurner10.Core.Dvd;

namespace CDBurner10.Upscaler.Engine;

public sealed record VideoSourceInfo(
    string FilePath,
    int Width,
    int Height,
    double Fps,
    TimeSpan Duration,
    long EstimatedFrames,
    string VideoCodec,
    string PixelFormat,
    bool HasAudio,
    string? AudioCodec,
    int AudioChannels,
    long FileSizeBytes,
    string? SampleAspectRatio = null)
{
    public string ResolutionLabel => $"{Width}×{Height}";
    public string FpsLabel => Fps > 0 ? $"{Fps:0.###} fps" : "Unknown fps";
}

public static class VideoProbeService
{
    public static async Task<VideoSourceInfo> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("Video file not found.", filePath);

        string ffprobe = DvdVideoConverter.FindFfprobe()
                         ?? throw new InvalidOperationException(
                             "ffprobe.exe was not found. Install FFmpeg (winget install Gyan.FFmpeg).");

        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[]
                 {
                     "-v", "quiet",
                     "-print_format", "json",
                     "-show_format",
                     "-show_streams",
                     filePath
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffprobe.");
        using var reg = ct.Register(() =>
        {
            try
            {
                if (!proc.HasExited) proc.Kill(true);
            }
            catch
            {
            }
        });

        string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        string stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException($"ffprobe failed on '{Path.GetFileName(filePath)}': {stderr}".Trim());

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        double formatDuration = 0;
        if (root.TryGetProperty("format", out var formatEl) &&
            formatEl.TryGetProperty("duration", out var durProp) &&
            double.TryParse(durProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fd))
        {
            formatDuration = fd;
        }

        int width = 0, height = 0;
        double fps = 0;
        long nbFrames = 0;
        double streamDuration = 0;
        string videoCodec = "unknown";
        string pixFmt = "yuv420p";
        string? sar = null;
        bool hasAudio = false;
        string? audioCodec = null;
        int audioChannels = 0;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                string? codecType = s.TryGetProperty("codec_type", out var ctEl) ? ctEl.GetString() : null;
                if (codecType == "video" && width == 0)
                {
                    if (s.TryGetProperty("width", out var wEl) && wEl.TryGetInt32(out int w)) width = w;
                    if (s.TryGetProperty("height", out var hEl) && hEl.TryGetInt32(out int h)) height = h;
                    if (s.TryGetProperty("codec_name", out var cnEl)) videoCodec = cnEl.GetString() ?? "unknown";
                    if (s.TryGetProperty("pix_fmt", out var pfEl)) pixFmt = pfEl.GetString() ?? "yuv420p";
                    if (s.TryGetProperty("sample_aspect_ratio", out var sarEl))
                    {
                        string? rawSar = sarEl.GetString();
                        if (!string.IsNullOrWhiteSpace(rawSar) &&
                            !rawSar.Equals("N/A", StringComparison.OrdinalIgnoreCase) &&
                            !rawSar.Equals("0:1", StringComparison.OrdinalIgnoreCase))
                        {
                            sar = rawSar;
                        }
                    }

                    if (s.TryGetProperty("avg_frame_rate", out var afrEl))
                        fps = ParseFraction(afrEl.GetString());
                    if (fps <= 0 && s.TryGetProperty("r_frame_rate", out var rfrEl))
                        fps = ParseFraction(rfrEl.GetString());

                    if (s.TryGetProperty("nb_frames", out var nfEl) &&
                        long.TryParse(nfEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out long nf))
                        nbFrames = nf;

                    if (s.TryGetProperty("duration", out var sdEl) &&
                        double.TryParse(sdEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture,
                            out double sd))
                        streamDuration = sd;
                }
                else if (codecType == "audio" && !hasAudio)
                {
                    hasAudio = true;
                    if (s.TryGetProperty("codec_name", out var acEl)) audioCodec = acEl.GetString();
                    if (s.TryGetProperty("channels", out var chEl) && chEl.TryGetInt32(out int ch)) audioChannels = ch;
                }
            }
        }

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("No valid video stream found in this file.");

        double totalSec = formatDuration > 0 ? formatDuration : streamDuration;
        var duration = totalSec > 0 ? TimeSpan.FromSeconds(totalSec) : TimeSpan.Zero;
        if (fps <= 0) fps = 24.0;
        if (nbFrames <= 0 && duration > TimeSpan.Zero)
            nbFrames = Math.Max(1, (long)Math.Round(duration.TotalSeconds * fps));

        long fileSize = new FileInfo(filePath).Length;

        return new VideoSourceInfo(
            FilePath: filePath,
            Width: width,
            Height: height,
            Fps: fps,
            Duration: duration,
            EstimatedFrames: nbFrames,
            VideoCodec: videoCodec,
            PixelFormat: pixFmt,
            HasAudio: hasAudio,
            AudioCodec: audioCodec,
            AudioChannels: audioChannels,
            FileSizeBytes: fileSize,
            SampleAspectRatio: sar);
    }

    private static double ParseFraction(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var parts = raw.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den) &&
            den > 0)
        {
            double val = num / den;
            return val is > 0.1 and <= 240 ? val : 0;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double direct))
            return direct is > 0.1 and <= 240 ? direct : 0;
        return 0;
    }
}