using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CDBurner10.Upscaler.Cli;

internal sealed record ProbeInfo(
    int Width,
    int Height,
    double Fps,
    TimeSpan Duration,
    long EstimatedFrames,
    bool HasAudio,
    string? AudioCodec,
    string? SampleAspectRatio);

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 2 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        string inputPath = args[0];
        string outputDir = args[1];
        string modelName = GetArg(args, "--model") ?? "ArtCNN_R16F96";
        string format = (GetArg(args, "--format") ?? "mp4").TrimStart('.').ToLowerInvariant();
        if (format != "mkv") format = "mp4";
        int crf = int.TryParse(GetArg(args, "--crf"), out int c) ? Math.Clamp(c, 10, 32) : 18;
        string preset = GetArg(args, "--preset") ?? "fast";
        int tileSize = int.TryParse(GetArg(args, "--tile"), out int t) ? Math.Clamp(t, 128, 2048) : 1024;
        bool forceCpu = args.Contains("--cpu", StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input file not found: {inputPath}");
            return 2;
        }

        string modelPath = ResolveModelPath(modelName);
        if (!File.Exists(modelPath))
        {
            Console.Error.WriteLine($"ONNX model not found: {modelPath}");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            var probe = await ProbeAsync(inputPath, cts.Token);
            var (baseW, baseH) = ComputeSquarePixelBase(probe);
            int outW = baseW * 2;
            int outH = baseH * 2;

            Directory.CreateDirectory(outputDir);
            string stem = Path.GetFileNameWithoutExtension(inputPath);
            string modelPrefix = Path.GetFileNameWithoutExtension(modelPath);
            string outputPath = Path.Combine(outputDir, $"{modelPrefix}_{stem}.{format}");
            string tempPath = Path.Combine(outputDir, $".{modelPrefix}_{stem}.{Guid.NewGuid():N}.tmp.{format}");

            string videoEncoder = DetectBestH264Encoder();
            Console.WriteLine($"Input:   {inputPath} ({probe.Width}x{probe.Height}, SAR {probe.SampleAspectRatio ?? "1:1"}, {probe.Fps:0.###} fps)");
            Console.WriteLine($"Target:  {outW}x{outH} (square pixels, 2x from {baseW}x{baseH}) -> {outputPath}");
            Console.WriteLine($"Model:   {Path.GetFileName(modelPath)} | Tile: {tileSize} | Encoder: {videoEncoder} (CRF {crf}, {preset})");

            using var session = CreateSession(modelPath, forceCpu, out string providerUsed);
            var inputMeta = session.InputMetadata.First();
            var outputMeta = session.OutputMetadata.First();
            string inputName = inputMeta.Key;
            string outputName = outputMeta.Key;
            bool useFp16 = inputMeta.Value.ElementDataType == TensorElementType.Float16;
            Console.WriteLine($"ONNX EP: {providerUsed} | Precision: {(useFp16 ? "FP16" : "FP32")}");

            string fpsStr = probe.Fps.ToString("0.######", CultureInfo.InvariantCulture);

            // Decoder: deinterlace (auto/bwdif) + convert anamorphic SAR to square pixels at baseW x baseH + yuv420p
            var decPsi = CreateProcessInfo("ffmpeg");
            AddArgs(decPsi,
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-i", inputPath,
                "-vf", $"bwdif=mode=send_frame:parity=auto:deint=interlaced,scale={baseW}:{baseH}:flags=bicubic,setsar=1,format=yuv420p",
                "-f", "rawvideo",
                "-pix_fmt", "yuv420p",
                "pipe:1");

            // Encoder: raw yuv420p -> H.264 (NVENC on T4/L4, libx264 on A100/CPU) + BT.709 tags + audio copy/AAC
            var encPsi = CreateProcessInfo("ffmpeg");
            AddArgs(encPsi,
                "-y", "-hide_banner", "-loglevel", "error",
                "-f", "rawvideo",
                "-pixel_format", "yuv420p",
                "-video_size", $"{outW}x{outH}",
                "-framerate", fpsStr,
                "-i", "pipe:0",
                "-i", inputPath,
                "-map", "0:v:0");

            if (probe.HasAudio)
                AddArgs(encPsi, "-map", "1:a?");
            if (format == "mkv")
                AddArgs(encPsi, "-map", "1:s?", "-c:s", "copy");

            AddArgs(encPsi, "-vf", "setsar=1", "-c:v", videoEncoder);
            if (videoEncoder == "h264_nvenc")
            {
                AddArgs(encPsi, "-rc", "vbr", "-cq", crf.ToString(CultureInfo.InvariantCulture), "-preset", "p5");
            }
            else
            {
                AddArgs(encPsi, "-crf", crf.ToString(CultureInfo.InvariantCulture), "-preset", preset);
            }

            AddArgs(encPsi,
                "-pix_fmt", "yuv420p",
                "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv");

            if (probe.HasAudio)
            {
                bool copyAudio = format == "mkv" || IsMp4AudioCompatible(probe.AudioCodec);
                if (copyAudio)
                    AddArgs(encPsi, "-c:a", "copy");
                else
                    AddArgs(encPsi, "-c:a", "aac", "-b:a", "192k");
            }

            if (format == "mp4")
                AddArgs(encPsi, "-movflags", "+faststart");

            AddArgs(encPsi, "-shortest", "-f", format == "mkv" ? "matroska" : "mp4", tempPath);

            using var decProc = Process.Start(decPsi) ?? throw new InvalidOperationException("Failed to start ffmpeg decoder.");
            using var encProc = Process.Start(encPsi) ?? throw new InvalidOperationException("Failed to start ffmpeg encoder.");
            using var killReg = cts.Token.Register(() => { TryKill(decProc); TryKill(encProc); });

            var decErr = new StringBuilder();
            var encErr = new StringBuilder();
            var decErrTask = DrainAsync(decProc.StandardError, decErr, cts.Token);
            var encErrTask = DrainAsync(encProc.StandardError, encErr, cts.Token);

            try
            {
                int ySize = baseW * baseH;
                int uvW = baseW / 2;
                int uvH = baseH / 2;
                int uvSize = uvW * uvH;
                int inFrameBytes = ySize + 2 * uvSize;

                int outYSize = outW * outH;
                int outUvSize = baseW * baseH;
                byte[] srcFrame = new byte[inFrameBytes];
                byte[] outFrame = new byte[outYSize + 2 * outUvSize];

                var sw = Stopwatch.StartNew();
                long frameIndex = 0;
                long lastPrintMs = -1000;

                while (!cts.Token.IsCancellationRequested)
                {
                    bool readOk = await ReadExactAsync(decProc.StandardOutput.BaseStream, srcFrame, inFrameBytes, cts.Token);
                    if (!readOk) break;

                    UpscaleLumaTiled(
                        session, inputName, outputName, useFp16,
                        srcFrame.AsSpan(0, ySize), baseW, baseH,
                        outFrame.AsSpan(0, outYSize),
                        tileSize, pad: 16, cts.Token);

                    Parallel.Invoke(
                        () => UpscalePlane2xBilinear(srcFrame.AsSpan(ySize, uvSize), uvW, uvH, outFrame.AsSpan(outYSize, outUvSize)),
                        () => UpscalePlane2xBilinear(srcFrame.AsSpan(ySize + uvSize, uvSize), uvW, uvH, outFrame.AsSpan(outYSize + outUvSize, outUvSize)));

                    await encProc.StandardInput.BaseStream.WriteAsync(outFrame, cts.Token);
                    frameIndex++;

                    long nowMs = sw.ElapsedMilliseconds;
                    if (frameIndex == 1 || nowMs - lastPrintMs >= 1000)
                    {
                        lastPrintMs = nowMs;
                        double fps = sw.Elapsed.TotalSeconds > 0.25 ? frameIndex / sw.Elapsed.TotalSeconds : 0;
                        double speed = probe.Fps > 0 && fps > 0 ? fps / probe.Fps : 0;
                        long total = Math.Max(frameIndex, probe.EstimatedFrames);
                        int pct = total > 0 ? (int)Math.Clamp(frameIndex * 100.0 / total, 0, 99) : 0;
                        string eta = fps > 0 && total > frameIndex
                            ? TimeSpan.FromSeconds((total - frameIndex) / fps).ToString(@"hh\:mm\:ss")
                            : "--:--:--";
                        Console.WriteLine($"[{pct,3}%] Frame {frameIndex:N0}/{total:N0} | {fps:F1} fps ({speed:F2}x) | ETA {eta}");
                    }
                }

                await encProc.StandardInput.BaseStream.FlushAsync(cts.Token);
                encProc.StandardInput.Close();

                await Task.WhenAll(decProc.WaitForExitAsync(cts.Token), encProc.WaitForExitAsync(cts.Token), decErrTask, encErrTask);

                if (decProc.ExitCode != 0)
                    throw new InvalidOperationException($"Decoder failed ({decProc.ExitCode}): {decErr}");
                if (encProc.ExitCode != 0)
                    throw new InvalidOperationException($"Encoder failed ({encProc.ExitCode}): {encErr}");

                File.Move(tempPath, outputPath, overwrite: true);
                Console.WriteLine($"DONE in {sw.Elapsed:hh\\:mm\\:ss} ({frameIndex:N0} frames) -> {outputPath}");
                return 0;
            }
            catch
            {
                TryKill(decProc);
                TryKill(encProc);
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            CDBurner10.Upscaler.Cli — Cross-platform .NET 10 CUDA/CPU ArtCNN Video Upscaler

            Usage:
              CDBurner10.Upscaler.Cli <input-video> <output-folder> [options]

            Options:
              --model <name|path>   ArtCNN_R16F96 (default) or ArtCNN_R8F64 or path to .onnx
              --format <mp4|mkv>    Output container (default: mp4)
              --crf <10..32>        Quality factor (default: 18)
              --preset <name>       libx264 preset when no NVENC (default: fast)
              --tile <128..2048>    ONNX tile size in pixels (default: 1024; fits whole DVD frame on A100)
              --cpu                 Force CPU execution provider instead of CUDA
            """);
    }

    private static string? GetArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static string ResolveModelPath(string modelNameOrPath)
    {
        if (File.Exists(modelNameOrPath))
            return Path.GetFullPath(modelNameOrPath);

        string fileName = modelNameOrPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
            ? modelNameOrPath
            : $"{modelNameOrPath}.onnx";

        string nextToExe = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", fileName);
        if (File.Exists(nextToExe))
            return nextToExe;

        return Path.Combine(Directory.GetCurrentDirectory(), "src", "CDBurner10.Upscaler", "Assets", "Models", fileName);
    }

    private static InferenceSession CreateSession(string modelPath, bool forceCpu, out string providerUsed)
    {
        if (!forceCpu)
        {
            try
            {
                var cudaOpts = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };
                cudaOpts.AppendExecutionProvider_CUDA(0);
                var sess = new InferenceSession(modelPath, cudaOpts);
                providerUsed = "CUDA GPU";
                return sess;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"CUDA unavailable ({ex.Message}), falling back to CPU.");
            }
        }

        var cpuOpts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        providerUsed = "CPU";
        return new InferenceSession(modelPath, cpuOpts);
    }

    private static string DetectBestH264Encoder()
    {
        try
        {
            var psi = CreateProcessInfo("ffmpeg");
            AddArgs(psi,
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.1:r=25",
                "-frames:v", "2", "-pix_fmt", "yuv420p",
                "-c:v", "h264_nvenc", "-f", "null", "-");
            using var p = Process.Start(psi);
            if (p is not null && p.WaitForExit(4000) && p.ExitCode == 0)
                return "h264_nvenc";
        }
        catch { }

        return "libx264";
    }

    private static async Task<ProbeInfo> ProbeAsync(string filePath, CancellationToken ct)
    {
        var psi = CreateProcessInfo("ffprobe");
        AddArgs(psi, "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", filePath);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffprobe.");
        string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException($"ffprobe failed on '{filePath}'.");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        double fmtDur = 0;
        if (root.TryGetProperty("format", out var fmt) &&
            fmt.TryGetProperty("duration", out var durProp) &&
            double.TryParse(durProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            fmtDur = d;

        int w = 0, h = 0;
        double fps = 0, streamDur = 0;
        long nbFrames = 0;
        string? sar = null;
        bool hasAudio = false;
        string? audioCodec = null;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                string? type = s.TryGetProperty("codec_type", out var ctEl) ? ctEl.GetString() : null;
                if (type == "video" && w == 0)
                {
                    if (s.TryGetProperty("width", out var wEl) && wEl.TryGetInt32(out int wi)) w = wi;
                    if (s.TryGetProperty("height", out var hEl) && hEl.TryGetInt32(out int hi)) h = hi;
                    if (s.TryGetProperty("avg_frame_rate", out var afr)) fps = ParseFraction(afr.GetString());
                    if (fps <= 0 && s.TryGetProperty("r_frame_rate", out var rfr)) fps = ParseFraction(rfr.GetString());
                    if (s.TryGetProperty("nb_frames", out var nfEl) && long.TryParse(nfEl.GetString(), out long nf)) nbFrames = nf;
                    if (s.TryGetProperty("duration", out var sdEl) && double.TryParse(sdEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sd)) streamDur = sd;
                    if (s.TryGetProperty("sample_aspect_ratio", out var sarEl))
                    {
                        string? raw = sarEl.GetString();
                        if (!string.IsNullOrWhiteSpace(raw) && !raw.Equals("N/A", StringComparison.OrdinalIgnoreCase) && raw != "0:1")
                            sar = raw;
                    }
                }
                else if (type == "audio" && !hasAudio)
                {
                    hasAudio = true;
                    if (s.TryGetProperty("codec_name", out var ac)) audioCodec = ac.GetString();
                }
            }
        }

        if (w <= 0 || h <= 0) throw new InvalidOperationException("No valid video stream found.");
        if (fps <= 0) fps = 25.0;
        double totalSec = fmtDur > 0 ? fmtDur : streamDur;
        var duration = totalSec > 0 ? TimeSpan.FromSeconds(totalSec) : TimeSpan.Zero;
        if (nbFrames <= 0 && duration > TimeSpan.Zero)
            nbFrames = Math.Max(1, (long)Math.Round(duration.TotalSeconds * fps));

        return new ProbeInfo(w, h, fps, duration, nbFrames, hasAudio, audioCodec, sar);
    }

    private static (int BaseW, int BaseH) ComputeSquarePixelBase(ProbeInfo p)
    {
        int dispW = p.Width;
        int dispH = p.Height;
        if (!string.IsNullOrWhiteSpace(p.SampleAspectRatio))
        {
            var parts = p.SampleAspectRatio.Split([':', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int num) && int.TryParse(parts[1], out int den) &&
                num > 0 && den > 0 && num != den)
            {
                if (num > den) dispW = (int)Math.Round(p.Width * (double)num / den);
                else dispH = (int)Math.Round(p.Height * (double)den / num);
            }
        }
        return (dispW % 2 == 0 ? dispW : dispW + 1, dispH % 2 == 0 ? dispH : dispH + 1);
    }

    private static void UpscaleLumaTiled(
        InferenceSession session, string inputName, string outputName, bool useFp16,
        ReadOnlySpan<byte> srcY, int srcW, int srcH, Span<byte> dstY,
        int tileSize, int pad, CancellationToken ct)
    {
        int dstW = srcW * 2;
        for (int ty = 0; ty < srcH; ty += tileSize)
        {
            int validH = Math.Min(tileSize, srcH - ty);
            int inY0 = Math.Max(0, ty - pad);
            int inY1 = Math.Min(srcH, ty + validH + pad);
            int inH = inY1 - inY0;

            for (int tx = 0; tx < srcW; tx += tileSize)
            {
                ct.ThrowIfCancellationRequested();
                int validW = Math.Min(tileSize, srcW - tx);
                int inX0 = Math.Max(0, tx - pad);
                int inX1 = Math.Min(srcW, tx + validW + pad);
                int inW = inX1 - inX0;

                int tilePixels = inW * inH;
                int cropX = (tx - inX0) * 2;
                int cropY = (ty - inY0) * 2;

                if (useFp16)
                {
                    var inputArr = new Float16[tilePixels];
                    for (int y = 0; y < inH; y++)
                    {
                        int sRow = (inY0 + y) * srcW + inX0;
                        int dRow = y * inW;
                        for (int x = 0; x < inW; x++)
                            inputArr[dRow + x] = (Float16)(srcY[sRow + x] / 255f);
                    }

                    var tensor = new DenseTensor<Float16>(inputArr, [1, 1, inH, inW]);
                    using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
                    var outTensor = results.First(r => r.Name == outputName).AsTensor<Float16>();

                    for (int y = 0; y < validH * 2; y++)
                    {
                        int dstRow = (ty * 2 + y) * dstW + tx * 2;
                        for (int x = 0; x < validW * 2; x++)
                        {
                            float v = (float)outTensor[0, 0, cropY + y, cropX + x];
                            dstY[dstRow + x] = (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
                        }
                    }
                }
                else
                {
                    var inputArr = new float[tilePixels];
                    for (int y = 0; y < inH; y++)
                    {
                        int sRow = (inY0 + y) * srcW + inX0;
                        int dRow = y * inW;
                        for (int x = 0; x < inW; x++)
                            inputArr[dRow + x] = srcY[sRow + x] / 255f;
                    }

                    var tensor = new DenseTensor<float>(inputArr, [1, 1, inH, inW]);
                    using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
                    var outTensor = results.First(r => r.Name == outputName).AsTensor<float>();

                    for (int y = 0; y < validH * 2; y++)
                    {
                        int dstRow = (ty * 2 + y) * dstW + tx * 2;
                        for (int x = 0; x < validW * 2; x++)
                        {
                            float v = outTensor[0, 0, cropY + y, cropX + x];
                            dstY[dstRow + x] = (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
                        }
                    }
                }
            }
        }
    }

    private static void UpscalePlane2xBilinear(ReadOnlySpan<byte> src, int srcW, int srcH, Span<byte> dst)
    {
        int dstW = srcW * 2;
        int dstH = srcH * 2;
        for (int dy = 0; dy < dstH; dy++)
        {
            float sy = (dy + 0.5f) * 0.5f - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(sy), 0, srcH - 1);
            int y1 = Math.Min(y0 + 1, srcH - 1);
            float fy = Math.Clamp(sy - y0, 0f, 1f);
            int row0 = y0 * srcW, row1 = y1 * srcW, dRow = dy * dstW;

            for (int dx = 0; dx < dstW; dx++)
            {
                float sx = (dx + 0.5f) * 0.5f - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(sx), 0, srcW - 1);
                int x1 = Math.Min(x0 + 1, srcW - 1);
                float fx = Math.Clamp(sx - x0, 0f, 1f);

                float top = src[row0 + x0] + (src[row0 + x1] - src[row0 + x0]) * fx;
                float bot = src[row1 + x0] + (src[row1 + x1] - src[row1 + x0]) * fx;
                dst[dRow + dx] = (byte)Math.Clamp((int)MathF.Round(top + (bot - top) * fy), 0, 255);
            }
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (n == 0) return false;
            offset += n;
        }
        return true;
    }

    private static double ParseFraction(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var p = raw.Split('/');
        if (p.Length == 2 &&
            double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num) &&
            double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den) && den > 0)
            return num / den;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }

    private static bool IsMp4AudioCompatible(string? codec) =>
        codec?.ToLowerInvariant() is "aac" or "mp3" or "ac3" or "eac3" or "alac" or "opus" or "mp2";

    private static ProcessStartInfo CreateProcessInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    private static void AddArgs(ProcessStartInfo psi, params string[] args)
    {
        foreach (var a in args) psi.ArgumentList.Add(a);
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder sb, CancellationToken ct)
    {
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (sb.Length < 16384) sb.AppendLine(line);
            }
        }
        catch { }
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}
