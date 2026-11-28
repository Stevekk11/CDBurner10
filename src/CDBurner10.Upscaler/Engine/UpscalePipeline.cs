using System.Diagnostics;
using System.Globalization;
using System.Text;
using CDBurner10.Core.Dvd;
using CDBurner10.Core.Utilities;
using CDBurner10.Upscaler.Services;

namespace CDBurner10.Upscaler.Engine;

public sealed record UpscaleProgress(
    int Percent,
    long CurrentFrame,
    long TotalFrames,
    double Fps,
    double Speed,
    TimeSpan TimeRemaining,
    string Message,
    string? LogLine = null);

public static class UpscalePipeline
{
    private static readonly object EncoderLock = new();
    private static string? _hevcHwEncoder;
    private static bool _hevcProbed;

    private static readonly string[] HevcHardwareCandidates =
    [
        "hevc_nvenc",
        "hevc_qsv",
        "hevc_amf",
        "hevc_mf"
    ];

    public static string ResolveVideoEncoder(UpscalerSettings settings, out string friendlyLabel)
    {
        bool wantHevc = string.Equals(settings.VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase);
        bool allowHw = !string.Equals(settings.EncoderPreference, "Software", StringComparison.OrdinalIgnoreCase);

        if (wantHevc)
        {
            if (allowHw)
            {
                string? hw = DetectHevcHardwareEncoder();
                if (hw is not null)
                {
                    friendlyLabel = $"HEVC ({hw})";
                    return hw;
                }
            }

            friendlyLabel = "HEVC (libx265)";
            return "libx265";
        }

        string enc = DvdVideoConverter.ResolveVideoEncoder(
            allowHw ? DvdVideoEncoder.Auto : DvdVideoEncoder.Software);
        friendlyLabel = $"H.264 ({enc})";
        return enc;
    }

    private static string? DetectHevcHardwareEncoder()
    {
        lock (EncoderLock)
        {
            if (_hevcProbed) return _hevcHwEncoder;
            string? ffmpeg = DvdVideoConverter.FindFfmpeg();
            if (!string.IsNullOrWhiteSpace(ffmpeg))
            {
                foreach (var candidate in HevcHardwareCandidates)
                {
                    if (ProbeEncoder(ffmpeg, candidate))
                    {
                        _hevcHwEncoder = candidate;
                        break;
                    }
                }
            }

            _hevcProbed = true;
            return _hevcHwEncoder;
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

    public static async Task<string> UpscaleAsync(
        VideoSourceInfo source,
        UpscalerPreset preset,
        string outputFolder,
        string outputFormat,
        UpscalerSettings settings,
        IProgress<UpscaleProgress> progress,
        CancellationToken ct)
    {
        string ffmpeg = DvdVideoConverter.FindFfmpeg()
                        ?? throw new InvalidOperationException(DvdVideoConverter.InstallHint);

        Directory.CreateDirectory(outputFolder);
        string finalOutputPath =
            UpscalerPresets.BuildOutputFilePath(source.FilePath, preset, outputFolder, outputFormat);
        if (!settings.OverwriteExisting && File.Exists(finalOutputPath))
            throw new IOException(
                $"Output file already exists: '{finalOutputPath}'. Enable overwrite in Settings or remove the existing file.");

        int scale = settings.ScaleFactor is 4 ? 4 : 2;
        int outW = MakeEven(source.Width * scale);
        int outH = MakeEven(source.Height * scale);

        string formatExt = outputFormat.Trim().TrimStart('.').ToLowerInvariant() == "mkv" ? "mkv" : "mp4";
        string muxerFormat = formatExt == "mkv" ? "matroska" : "mp4";
        string tempOutputPath = Path.Combine(
            outputFolder,
            $".{Path.GetFileNameWithoutExtension(finalOutputPath)}.{Guid.NewGuid():N}.tmp.{formatExt}");

        string encoder = ResolveVideoEncoder(settings, out string encoderLabel);
        string fpsStr = source.Fps.ToString("0.######", CultureInfo.InvariantCulture);
        var elapsedWatch = Stopwatch.StartNew();

        progress.Report(new UpscaleProgress(
            Percent: 0,
            CurrentFrame: 0,
            TotalFrames: source.EstimatedFrames,
            Fps: 0,
            Speed: 0,
            TimeRemaining: TimeSpan.Zero,
            Message: $"Starting {preset.FilePrefix} upscale ({source.Width}×{source.Height} → {outW}×{outH})...",
            LogLine:
            $"Preset: {preset.DisplayName} | Backend: {preset.BackendLabel} | Encoder: {encoderLabel} | Target: {outW}×{outH} ({formatExt.ToUpperInvariant()})"));

        try
        {
            if (preset.Backend == UpscalerBackendKind.MpvGlslShader)
            {
                await RunMpvGlslPipelineAsync(
                    ffmpeg, source, preset, outW, outH, fpsStr,
                    encoder, muxerFormat, formatExt, tempOutputPath, settings, progress, elapsedWatch, ct);
            }
            else
            {
                await RunOnnxPipelineAsync(
                    ffmpeg, source, preset, outW, outH, fpsStr,
                    encoder, muxerFormat, formatExt, tempOutputPath, settings, progress, elapsedWatch, ct);
            }

            ct.ThrowIfCancellationRequested();

            if (!File.Exists(tempOutputPath) || new FileInfo(tempOutputPath).Length == 0)
                throw new InvalidOperationException("Upscaling finished but produced an empty output file.");

            File.Move(tempOutputPath, finalOutputPath, overwrite: true);
            return finalOutputPath;
        }
        catch
        {
            TryDelete(tempOutputPath);
            throw;
        }
    }

    private static async Task RunMpvGlslPipelineAsync(
        string ffmpeg,
        VideoSourceInfo source,
        UpscalerPreset preset,
        int outW,
        int outH,
        string fpsStr,
        string encoder,
        string muxerFormat,
        string formatExt,
        string tempOutputPath,
        UpscalerSettings settings,
        IProgress<UpscaleProgress> progress,
        Stopwatch elapsedWatch,
        CancellationToken ct)
    {
        var encPsi = BuildEncoderStartInfo(
            ffmpeg, "rgba", outW, outH, fpsStr, source,
            videoFilter: "format=yuv420p",
            encoder, muxerFormat, formatExt, tempOutputPath, settings);

        using var encProc = Process.Start(encPsi)
                            ?? throw new InvalidOperationException("Failed to start FFmpeg encoder.");
        using var encKillReg = ct.Register(() => TryKill(encProc));

        var stderrLog = new StringBuilder();
        var stderrTask = DrainStderrAsync(encProc.StandardError, stderrLog, ct);

        try
        {
            await MpvShaderUpscaler.RunAsync(
                source, preset, outW, outH,
                encProc.StandardInput.BaseStream,
                settings, progress, elapsedWatch, ct);
        }
        catch
        {
            TryKill(encProc);
            try
            {
                encProc.WaitForExit(2000);
            }
            catch
            {
            }

            throw;
        }
        finally
        {
            try
            {
                encProc.StandardInput.Close();
            }
            catch
            {
            }
        }

        await encProc.WaitForExitAsync(ct);
        await stderrTask;

        if (encProc.ExitCode != 0)
        {
            string tail = TailLines(stderrLog.ToString(), 8);
            throw new InvalidOperationException($"FFmpeg encoder exited with code {encProc.ExitCode}: {tail}");
        }
    }

    private static async Task RunOnnxPipelineAsync(
        string ffmpeg,
        VideoSourceInfo source,
        UpscalerPreset preset,
        int outW,
        int outH,
        string fpsStr,
        string encoder,
        string muxerFormat,
        string formatExt,
        string tempOutputPath,
        UpscalerSettings settings,
        IProgress<UpscaleProgress> progress,
        Stopwatch elapsedWatch,
        CancellationToken ct)
    {
        int alignedSrcW = MakeEven(source.Width);
        int alignedSrcH = MakeEven(source.Height);
        int modelOutW = alignedSrcW * 2;
        int modelOutH = alignedSrcH * 2;

        var decPsi = new ProcessStartInfo
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
                     "-i", source.FilePath,
                     "-vf", $"scale={alignedSrcW}:{alignedSrcH}:flags=bicubic,format=yuv420p",
                     "-f", "rawvideo",
                     "-pix_fmt", "yuv420p",
                     "pipe:1"
                 })
        {
            decPsi.ArgumentList.Add(arg);
        }

        string? encVf = (modelOutW != outW || modelOutH != outH)
            ? $"scale={outW}:{outH}:flags=lanczos"
            : null;

        var encPsi = BuildEncoderStartInfo(
            ffmpeg, "yuv420p", modelOutW, modelOutH, fpsStr, source,
            videoFilter: encVf,
            encoder, muxerFormat, formatExt, tempOutputPath, settings);

        using var decProc = Process.Start(decPsi)
                            ?? throw new InvalidOperationException("Failed to start FFmpeg decoder.");
        using var encProc = Process.Start(encPsi)
                            ?? throw new InvalidOperationException("Failed to start FFmpeg encoder.");

        using var decKillReg = ct.Register(() => TryKill(decProc));
        using var encKillReg = ct.Register(() => TryKill(encProc));

        var decErr = new StringBuilder();
        var encErr = new StringBuilder();
        var decErrTask = DrainStderrAsync(decProc.StandardError, decErr, ct);
        var encErrTask = DrainStderrAsync(encProc.StandardError, encErr, ct);

        try
        {
            await ArtCnnOnnxUpscaler.RunAsync(
                source, preset, alignedSrcW, alignedSrcH, outW, outH,
                decProc.StandardOutput.BaseStream,
                encProc.StandardInput.BaseStream,
                settings, progress, elapsedWatch, ct);
        }
        catch
        {
            TryKill(decProc);
            TryKill(encProc);
            try
            {
                decProc.WaitForExit(2000);
            }
            catch
            {
            }

            try
            {
                encProc.WaitForExit(2000);
            }
            catch
            {
            }

            throw;
        }
        finally
        {
            try
            {
                encProc.StandardInput.Close();
            }
            catch
            {
            }
        }

        await Task.WhenAll(decProc.WaitForExitAsync(ct), encProc.WaitForExitAsync(ct), decErrTask, encErrTask);

        if (decProc.ExitCode != 0)
            throw new InvalidOperationException(
                $"FFmpeg decoder failed (code {decProc.ExitCode}): {TailLines(decErr.ToString(), 6)}");

        if (encProc.ExitCode != 0)
            throw new InvalidOperationException(
                $"FFmpeg encoder failed (code {encProc.ExitCode}): {TailLines(encErr.ToString(), 8)}");
    }

    private static ProcessStartInfo BuildEncoderStartInfo(
        string ffmpeg,
        string inputPixelFormat,
        int inputWidth,
        int inputHeight,
        string fpsStr,
        VideoSourceInfo source,
        string? videoFilter,
        string encoder,
        string muxerFormat,
        string formatExt,
        string outputPath,
        UpscalerSettings settings)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        void Add(params string[] args)
        {
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        Add("-y", "-hide_banner", "-loglevel", "error");
        Add("-f", "rawvideo",
            "-pixel_format", inputPixelFormat,
            "-video_size", $"{inputWidth}x{inputHeight}",
            "-framerate", fpsStr,
            "-i", "pipe:0");
        Add("-i", source.FilePath);
        Add("-map", "0:v:0");

        if (source.HasAudio)
        {
            Add("-map", "1:a?");
        }

        if (formatExt == "mkv")
        {
            Add("-map", "1:s?", "-c:s", "copy");
        }

        if (!string.IsNullOrWhiteSpace(videoFilter))
            Add("-vf", videoFilter);

        Add("-c:v", encoder);
        AddEncoderQualityArgs(psi, encoder, settings);
        Add("-pix_fmt", "yuv420p");

        if (source.HasAudio)
        {
            bool canCopyAudio = !string.Equals(settings.AudioMode, "Aac", StringComparison.OrdinalIgnoreCase) &&
                                (formatExt == "mkv" || IsMp4AudioCompatible(source.AudioCodec));
            if (canCopyAudio)
                Add("-c:a", "copy");
            else
                Add("-c:a", "aac", "-b:a", "192k");
        }

        if (formatExt == "mp4")
            Add("-movflags", "+faststart");

        Add("-shortest");
        Add("-f", muxerFormat, outputPath);
        return psi;
    }

    private static void AddEncoderQualityArgs(ProcessStartInfo psi, string encoder, UpscalerSettings settings)
    {
        int crf = Math.Clamp(settings.Crf, 10, 32);
        string preset = string.IsNullOrWhiteSpace(settings.EncoderPreset) ? "medium" : settings.EncoderPreset;

        if (encoder is "libx264" or "libx265")
        {
            psi.ArgumentList.Add("-crf");
            psi.ArgumentList.Add(crf.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-preset");
            psi.ArgumentList.Add(preset);
        }
        else if (encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-rc");
            psi.ArgumentList.Add("vbr");
            psi.ArgumentList.Add("-cq");
            psi.ArgumentList.Add(crf.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-preset");
            psi.ArgumentList.Add("p5");
        }
        else if (encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-global_quality");
            psi.ArgumentList.Add(crf.ToString(CultureInfo.InvariantCulture));
        }
        else if (encoder.EndsWith("_amf", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-rc");
            psi.ArgumentList.Add("cqp");
            psi.ArgumentList.Add("-qp_i");
            psi.ArgumentList.Add(crf.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-qp_p");
            psi.ArgumentList.Add(crf.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            // MediaFoundation or generic fallback
            psi.ArgumentList.Add("-q:v");
            psi.ArgumentList.Add("80");
        }
    }

    private static bool IsMp4AudioCompatible(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return false;
        return codec.ToLowerInvariant() is "aac" or "mp3" or "ac3" or "eac3" or "alac" or "opus" or "mp2";
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value + 1;

    private static async Task DrainStderrAsync(StreamReader reader, StringBuilder sb, CancellationToken ct)
    {
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    DiagnosticLogger.Verbose("ffmpeg", line);
                    lock (sb)
                    {
                        if (sb.Length < 16384)
                            sb.AppendLine(line);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private static string TailLines(string text, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text)) return "No error output.";
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines.TakeLast(maxLines));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}