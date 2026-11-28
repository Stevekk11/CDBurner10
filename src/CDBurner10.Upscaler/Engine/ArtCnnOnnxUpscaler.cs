using System.Diagnostics;
using CDBurner10.Core.Utilities;
using CDBurner10.Upscaler.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CDBurner10.Upscaler.Engine;

/// <summary>
/// Runs ArtCNN Residual ONNX models (R16F96 / R8F64) on BT.709 Luma (Y) via DirectML GPU
/// (with CPU fallback) using overlap-padded tiled inference, paired with 2x bilinear chroma upscaling.
/// </summary>
internal static class ArtCnnOnnxUpscaler
{
    public static async Task RunAsync(
        VideoSourceInfo source,
        UpscalerPreset preset,
        int alignedSrcW,
        int alignedSrcH,
        int outWidth,
        int outHeight,
        Stream decoderStdout,
        Stream encoderStdin,
        UpscalerSettings settings,
        IProgress<UpscaleProgress> progress,
        Stopwatch elapsedWatch,
        CancellationToken ct)
    {
        string modelPath = UpscalerPresets.ResolveModelPath(
            preset.OnnxModelFileName ?? throw new InvalidOperationException("Preset has no ONNX model."));

        progress.Report(new UpscaleProgress(
            Percent: 0,
            CurrentFrame: 0,
            TotalFrames: source.EstimatedFrames,
            Fps: 0,
            Speed: 0,
            TimeRemaining: TimeSpan.Zero,
            Message: $"Loading ONNX model {preset.FilePrefix}...",
            LogLine: $"Loading {Path.GetFileName(modelPath)}..."));

        using var session = CreateSession(modelPath, settings.OnnxProvider, out string providerUsed);
        var inputMeta = session.InputMetadata.First();
        var outputMeta = session.OutputMetadata.First();
        string inputName = inputMeta.Key;
        string outputName = outputMeta.Key;
        bool useFp16 = inputMeta.Value.ElementDataType == TensorElementType.Float16;

        progress.Report(new UpscaleProgress(
            Percent: 0,
            CurrentFrame: 0,
            TotalFrames: source.EstimatedFrames,
            Fps: 0,
            Speed: 0,
            TimeRemaining: TimeSpan.Zero,
            Message: $"Upscaling with {preset.FilePrefix} ({providerUsed})...",
            LogLine:
            $"ONNX execution provider: {providerUsed} | Tensor precision: {(useFp16 ? "FP16" : "FP32")} | Input: {inputName} -> Output: {outputName}"));

        int ySize = alignedSrcW * alignedSrcH;
        int uvW = alignedSrcW / 2;
        int uvH = alignedSrcH / 2;
        int uvSize = uvW * uvH;
        int frameBytes = ySize + 2 * uvSize;

        int modelOutW = alignedSrcW * 2;
        int modelOutH = alignedSrcH * 2;
        int modelOutYSize = modelOutW * modelOutH;
        int modelOutUvW = alignedSrcW;
        int modelOutUvH = alignedSrcH;
        int modelOutUvSize = modelOutUvW * modelOutUvH;

        byte[] srcFrame = new byte[frameBytes];
        byte[] outFrame = new byte[modelOutYSize + 2 * modelOutUvSize];

        int tileSize = Math.Clamp(settings.OnnxTileSize, 128, 1024);
        const int pad = 16;

        long frameIndex = 0;
        while (!ct.IsCancellationRequested)
        {
            bool readOk = await ReadExactAsync(decoderStdout, srcFrame, frameBytes, ct);
            if (!readOk)
                break;

            ct.ThrowIfCancellationRequested();

            // 1. Run 2x ArtCNN Residual inference on Luma (Y) plane
            UpscaleLumaTiled(
                session,
                inputName,
                outputName,
                useFp16,
                srcFrame.AsSpan(0, ySize),
                alignedSrcW,
                alignedSrcH,
                outFrame.AsSpan(0, modelOutYSize),
                tileSize,
                pad,
                ct);

            // 2. Upscale U and V chroma planes 2x cleanly in parallel
            Parallel.Invoke(
                () => UpscalePlane2xBilinear(
                    srcFrame.AsSpan(ySize, uvSize), uvW, uvH,
                    outFrame.AsSpan(modelOutYSize, modelOutUvSize)),
                () => UpscalePlane2xBilinear(
                    srcFrame.AsSpan(ySize + uvSize, uvSize), uvW, uvH,
                    outFrame.AsSpan(modelOutYSize + modelOutUvSize, modelOutUvSize)));

            await encoderStdin.WriteAsync(outFrame, ct);
            frameIndex++;

            double currentFps = elapsedWatch.Elapsed.TotalSeconds > 0.25
                ? frameIndex / elapsedWatch.Elapsed.TotalSeconds
                : 0;
            double speed = source.Fps > 0 && currentFps > 0 ? currentFps / source.Fps : 0;
            long total = Math.Max(frameIndex, source.EstimatedFrames);
            int pct = total > 0 ? (int)Math.Clamp(frameIndex * 100.0 / total, 0, 99) : 0;
            TimeSpan eta = currentFps > 0 && total > frameIndex
                ? TimeSpan.FromSeconds((total - frameIndex) / currentFps)
                : TimeSpan.Zero;

            if (frameIndex == 1 || frameIndex % 2 == 0)
            {
                progress.Report(new UpscaleProgress(
                    Percent: pct,
                    CurrentFrame: frameIndex,
                    TotalFrames: total,
                    Fps: currentFps,
                    Speed: speed,
                    TimeRemaining: eta,
                    Message:
                    $"Upscaling via {preset.FilePrefix} ({providerUsed}, frame {frameIndex:N0} / {total:N0})..."));
            }
        }

        await encoderStdin.FlushAsync(ct);
        progress.Report(new UpscaleProgress(
            Percent: 99,
            CurrentFrame: frameIndex,
            TotalFrames: frameIndex,
            Fps: elapsedWatch.Elapsed.TotalSeconds > 0 ? frameIndex / elapsedWatch.Elapsed.TotalSeconds : 0,
            Speed: source.Fps > 0 && elapsedWatch.Elapsed.TotalSeconds > 0
                ? (frameIndex / elapsedWatch.Elapsed.TotalSeconds) / source.Fps
                : 0,
            TimeRemaining: TimeSpan.Zero,
            Message: "Finalizing encoded video stream...",
            LogLine: $"Processed {frameIndex:N0} frames through {preset.FilePrefix} ({providerUsed})."));
    }

    private static InferenceSession CreateSession(string modelPath, string providerPref, out string providerUsed)
    {
        bool tryDml = !string.Equals(providerPref, "CPU", StringComparison.OrdinalIgnoreCase);

        if (tryDml)
        {
            try
            {
                var dmlOpts = new SessionOptions
                {
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };
                dmlOpts.AppendExecutionProvider_DML(0);
                var sess = new InferenceSession(modelPath, dmlOpts);
                providerUsed = "DirectML GPU";
                return sess;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("ArtCNN-ONNX",
                    $"DirectML initialization failed, falling back to CPU: {ex.Message}");
            }
        }

        var cpuOpts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        providerUsed = "CPU";
        return new InferenceSession(modelPath, cpuOpts);
    }

    private static void UpscaleLumaTiled(
        InferenceSession session,
        string inputName,
        string outputName,
        bool useFp16,
        ReadOnlySpan<byte> srcY,
        int srcW,
        int srcH,
        Span<byte> dstY,
        int tileSize,
        int pad,
        CancellationToken ct)
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
                int outTileW = inW * 2;

                if (useFp16)
                {
                    var inputArr = new Float16[tilePixels];
                    for (int y = 0; y < inH; y++)
                    {
                        int sRow = (inY0 + y) * srcW + inX0;
                        int dRow = y * inW;
                        for (int x = 0; x < inW; x++)
                        {
                            inputArr[dRow + x] = (Float16)(srcY[sRow + x] / 255f);
                        }
                    }

                    var tensor = new DenseTensor<Float16>(inputArr, new[] { 1, 1, inH, inW });
                    using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
                    var outTensor = results.First(r => r.Name == outputName).AsTensor<Float16>();

                    for (int y = 0; y < validH * 2; y++)
                    {
                        int dstRow = (ty * 2 + y) * dstW + tx * 2;
                        for (int x = 0; x < validW * 2; x++)
                        {
                            float v = (float)outTensor[0, 0, cropY + y, cropX + x];
                            int b = (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
                            dstY[dstRow + x] = (byte)b;
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
                        {
                            inputArr[dRow + x] = srcY[sRow + x] / 255f;
                        }
                    }

                    var tensor = new DenseTensor<float>(inputArr, new[] { 1, 1, inH, inW });
                    using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
                    var outTensor = results.First(r => r.Name == outputName).AsTensor<float>();

                    for (int y = 0; y < validH * 2; y++)
                    {
                        int dstRow = (ty * 2 + y) * dstW + tx * 2;
                        for (int x = 0; x < validW * 2; x++)
                        {
                            float v = outTensor[0, 0, cropY + y, cropX + x];
                            int b = (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
                            dstY[dstRow + x] = (byte)b;
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
            float fy = (dy + 0.5f) * 0.5f - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(fy), 0, srcH - 1);
            int y1 = Math.Clamp(y0 + 1, 0, srcH - 1);
            float wy = fy - y0;
            if (wy < 0) wy = 0;
            float wy0 = 1f - wy;

            int row0 = y0 * srcW;
            int row1 = y1 * srcW;
            int outRow = dy * dstW;

            for (int dx = 0; dx < dstW; dx++)
            {
                float fx = (dx + 0.5f) * 0.5f - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(fx), 0, srcW - 1);
                int x1 = Math.Clamp(x0 + 1, 0, srcW - 1);
                float wx = fx - x0;
                if (wx < 0) wx = 0;
                float wx0 = 1f - wx;

                float v00 = src[row0 + x0];
                float v10 = src[row0 + x1];
                float v01 = src[row1 + x0];
                float v11 = src[row1 + x1];

                float val = (v00 * wx0 + v10 * wx) * wy0 + (v01 * wx0 + v11 * wx) * wy;
                dst[outRow + dx] = (byte)Math.Clamp((int)(val + 0.5f), 0, 255);
            }
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (n == 0)
                return offset == 0 ? false : throw new EndOfStreamException("Unexpected end of decoded video stream.");
            offset += n;
        }

        return true;
    }
}