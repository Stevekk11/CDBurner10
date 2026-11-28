using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using CDBurner10.Core.Utilities;
using CDBurner10.Upscaler.Services;

namespace CDBurner10.Upscaler.Engine;

internal static class MpvShaderUpscaler
{
    public static Task RunAsync(
        VideoSourceInfo source,
        UpscalerPreset preset,
        int targetWidth,
        int targetHeight,
        Stream ffmpegInputStream,
        UpscalerSettings settings,
        IProgress<UpscaleProgress> progress,
        Stopwatch elapsedWatch,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                RunCore(source, preset, targetWidth, targetHeight, ffmpegInputStream, settings, progress, elapsedWatch,
                    ct);
                tcs.TrySetResult();
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            Name = "CDBurner10.Upscaler.MpvGlRender",
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    private static unsafe void RunCore(
        VideoSourceInfo source,
        UpscalerPreset preset,
        int targetWidth,
        int targetHeight,
        Stream ffmpegInputStream,
        UpscalerSettings settings,
        IProgress<UpscaleProgress> progress,
        Stopwatch elapsedWatch,
        CancellationToken ct)
    {
        LibMpvNative.EnsureInitialized();

        IReadOnlyList<string> shaderNames = preset.GetShadersForTier(settings.Anime4KTier);
        var shaderPaths = shaderNames.Select(UpscalerPresets.ResolveShaderPath).ToList();

        progress.Report(new UpscaleProgress(
            Percent: 0,
            CurrentFrame: 0,
            TotalFrames: source.EstimatedFrames,
            Fps: 0,
            Speed: 0,
            TimeRemaining: TimeSpan.Zero,
            Message: $"Initializing OpenGL & {preset.FilePrefix} shaders...",
            LogLine: $"Loading {shaderPaths.Count} GLSL shader(s): {string.Join(", ", shaderNames)}"));

        using var gl = new OffscreenGlContext(targetWidth, targetHeight);
        gl.MakeCurrent();
        progress.Report(new UpscaleProgress(0, 0, source.EstimatedFrames, 0, 0, TimeSpan.Zero,
            "Initializing libmpv...",
            $"OpenGL context: {gl.GlRendererInfo} | Async PBO: {(gl.SupportsAsyncPbo ? $"Enabled ({OffscreenGlContext.PboRingCapacity}x ring)" : "Fallback sync")}"));

        nint mpv = LibMpvNative.mpv_create();
        if (mpv == 0)
            throw new InvalidOperationException(
                "mpv_create() returned NULL. Ensure libmpv-2.dll is valid and LC_NUMERIC is C.");

        nint renderCtx = 0;
        Exception? controlException = null;

        using var updateSignal = new AutoResetEvent(false);
        using var stopControl = new ManualResetEventSlim(false);

        int eofReached = 0;
        int fileLoaded = 0;
        int renderFinished = 0;
        long framesRendered = 0;
        double lastRenderedPts = -1.0;
        long waitMpvTicks = 0;
        long loadWaitTicks = 0;
        long renderTicks = 0;
        long pboReadTicks = 0;
        long channelWaitTicks = 0;
        var phaseSw = Stopwatch.StartNew();

        LibMpvNative.RenderUpdateCallbackFn updateCb = _ => updateSignal.Set();
        nint updateCbPtr = Marshal.GetFunctionPointerForDelegate(updateCb);

        int frameBytes = gl.FrameByteSize;
        var frameChannel = Channel.CreateBounded<(byte[] Buffer, int Length)>(new BoundedChannelOptions(4)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        Task writerTask = PipeWriterLoopAsync(frameChannel.Reader, ffmpegInputStream, ct);

        void EnqueueFrame(byte[] buf, int len)
        {
            if (frameChannel.Writer.TryWrite((buf, len)))
                return;

            long t0 = Stopwatch.GetTimestamp();
            try
            {
                frameChannel.Writer.WriteAsync((buf, len), ct).AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buf);
                throw;
            }
            finally
            {
                channelWaitTicks += Stopwatch.GetTimestamp() - t0;
            }
        }

        try
        {
            ConfigureMpvOptions(mpv, settings);
            LibMpvNative.Check(LibMpvNative.mpv_initialize(mpv), "mpv_initialize failed");
            _ = LibMpvNative.mpv_request_log_messages(mpv, "warn");

            // Load shaders individually via change-list so Windows drive colons (e.g. D:\...) never split paths
            foreach (var sp in shaderPaths)
            {
                int rc = LibMpvNative.Command(mpv, "change-list", "glsl-shaders", "append", sp);
                LibMpvNative.Check(rc, $"Failed to append GLSL shader '{Path.GetFileName(sp)}'");
            }

            renderCtx = CreateMpvRenderContext(mpv, gl, settings);
            LibMpvNative.mpv_render_context_set_update_callback(renderCtx, updateCbPtr, 0);

            // Start separate mpv control/event thread to abide strictly by libmpv render API threading rules
            var controlThread = new Thread(() =>
            {
                try
                {
                    ControlLoop(
                        mpv,
                        source.FilePath,
                        progress,
                        source.EstimatedFrames,
                        updateSignal,
                        stopControl,
                        () => Volatile.Read(ref fileLoaded) != 0,
                        () => Volatile.Read(ref eofReached) != 0,
                        val => Volatile.Write(ref fileLoaded, val ? 1 : 0),
                        val => Volatile.Write(ref eofReached, val ? 1 : 0),
                        ct);
                }
                catch (Exception ex)
                {
                    controlException = ex;
                    Volatile.Write(ref renderFinished, 1);
                    updateSignal.Set();
                }
            })
            {
                Name = "CDBurner10.Upscaler.MpvControl",
                IsBackground = true
            };
            controlThread.Start();

            var fbo = new LibMpvNative.MpvOpenglFbo
            {
                Fbo = gl.FboId,
                W = targetWidth,
                H = targetHeight,
                InternalFormat = 0
            };
            int flipY = 0;
            int blockForTargetTime = 0;

            LibMpvNative.MpvRenderParam* renderParams = stackalloc LibMpvNative.MpvRenderParam[4];
            renderParams[0] = new LibMpvNative.MpvRenderParam
            {
                Type = LibMpvNative.MPV_RENDER_PARAM_OPENGL_FBO,
                Data = (nint)(&fbo)
            };
            renderParams[1] = new LibMpvNative.MpvRenderParam
            {
                Type = LibMpvNative.MPV_RENDER_PARAM_FLIP_Y,
                Data = (nint)(&flipY)
            };
            renderParams[2] = new LibMpvNative.MpvRenderParam
            {
                Type = LibMpvNative.MPV_RENDER_PARAM_BLOCK_FOR_TARGET_TIME,
                Data = (nint)(&blockForTargetTime)
            };
            renderParams[3] = new LibMpvNative.MpvRenderParam
            {
                Type = LibMpvNative.MPV_RENDER_PARAM_INVALID,
                Data = 0
            };

            int eofQuietChecks = 0;
            while (Volatile.Read(ref renderFinished) == 0 && !ct.IsCancellationRequested)
            {
                long tw0 = Stopwatch.GetTimestamp();
                updateSignal.WaitOne(15);
                long twDelta = Stopwatch.GetTimestamp() - tw0;
                if (framesRendered == 0) loadWaitTicks += twDelta;
                else waitMpvTicks += twDelta;

                if (ct.IsCancellationRequested || Volatile.Read(ref renderFinished) != 0)
                    break;

                ulong flags = LibMpvNative.mpv_render_context_update(renderCtx);
                if ((flags & LibMpvNative.MPV_RENDER_UPDATE_FRAME) != 0 &&
                    Volatile.Read(ref fileLoaded) != 0)
                {
                    eofQuietChecks = 0;
                    bool atEof = Volatile.Read(ref eofReached) != 0;
                    double pts = LibMpvNative.GetDoubleProperty(mpv, "time-pos");
                    _ = atEof;

                    gl.MakeCurrent();
                    long tr0 = Stopwatch.GetTimestamp();
                    int rc = LibMpvNative.mpv_render_context_render(renderCtx, renderParams);
                    renderTicks += Stopwatch.GetTimestamp() - tr0;
                    if (rc < 0)
                        throw new InvalidOperationException(
                            $"mpv_render_context_render failed: {LibMpvNative.GetErrorMessage(rc)}");

                    LibMpvNative.mpv_render_context_report_swap(renderCtx);

                    if (gl.SupportsAsyncPbo)
                    {
                        gl.EnqueueAsyncReadBgra();

                        if (gl.PendingPboCount >= OffscreenGlContext.PboRingCapacity)
                        {
                            byte[] buf = ArrayPool<byte>.Shared.Rent(frameBytes);
                            long td0 = Stopwatch.GetTimestamp();
                            bool dequeued = gl.TryDequeueCompletedBgra(buf);
                            pboReadTicks += Stopwatch.GetTimestamp() - td0;
                            if (dequeued)
                                EnqueueFrame(buf, frameBytes);
                            else
                                ArrayPool<byte>.Shared.Return(buf);
                        }
                    }
                    else
                    {
                        byte[] buf = ArrayPool<byte>.Shared.Rent(frameBytes);
                        try
                        {
                            gl.ReadFboBgraSync(buf);
                        }
                        catch
                        {
                            ArrayPool<byte>.Shared.Return(buf);
                            throw;
                        }

                        EnqueueFrame(buf, frameBytes);
                    }

                    framesRendered++;
                    lastRenderedPts = pts;

                    double currentFps = elapsedWatch.Elapsed.TotalSeconds > 0.25
                        ? framesRendered / elapsedWatch.Elapsed.TotalSeconds
                        : 0;
                    double speed = source.Fps > 0 && currentFps > 0 ? currentFps / source.Fps : 0;
                    long total = Math.Max(framesRendered, source.EstimatedFrames);
                    int pct = total > 0 ? (int)Math.Clamp(framesRendered * 100.0 / total, 0, 99) : 0;
                    TimeSpan eta = currentFps > 0 && total > framesRendered
                        ? TimeSpan.FromSeconds((total - framesRendered) / currentFps)
                        : TimeSpan.Zero;

                    if (framesRendered == 1 || framesRendered % 5 == 0)
                    {
                        progress.Report(new UpscaleProgress(
                            Percent: pct,
                            CurrentFrame: framesRendered,
                            TotalFrames: total,
                            Fps: currentFps,
                            Speed: speed,
                            TimeRemaining: eta,
                            Message:
                            $"Upscaling via libmpv GPU shaders (frame {framesRendered:N0} / {total:N0})..."));
                    }
                }
                else if (Volatile.Read(ref eofReached) != 0)
                {
                    eofQuietChecks++;
                    if (eofQuietChecks >= 2)
                        break;
                }
            }

            stopControl.Set();
            LibMpvNative.mpv_wakeup(mpv);
            controlThread.Join(5000);

            ct.ThrowIfCancellationRequested();
            if (controlException is not null)
                throw controlException;

            // Drain remaining frames queued in PBO ring before completing the FFmpeg writer channel
            gl.MakeCurrent();
            while (gl.PendingPboCount > 0)
            {
                byte[] buf = ArrayPool<byte>.Shared.Rent(frameBytes);
                long td0 = Stopwatch.GetTimestamp();
                bool dequeued = gl.TryDequeueCompletedBgra(buf);
                pboReadTicks += Stopwatch.GetTimestamp() - td0;
                if (dequeued)
                {
                    EnqueueFrame(buf, frameBytes);
                }
                else
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    break;
                }
            }

            frameChannel.Writer.TryComplete();
            long tc0 = Stopwatch.GetTimestamp();
            writerTask.GetAwaiter().GetResult();
            channelWaitTicks += Stopwatch.GetTimestamp() - tc0;

            ffmpegInputStream.Flush();
            double toMs = 1000.0 / Stopwatch.Frequency;
            DiagnosticLogger.Verbose("MpvPerf",
                $"total={phaseSw.Elapsed.TotalSeconds:F2}s frames={framesRendered} load={loadWaitTicks * toMs:F0}ms wait={waitMpvTicks * toMs:F0}ms render={renderTicks * toMs:F0}ms read={pboReadTicks * toMs:F0}ms pipe={channelWaitTicks * toMs:F0}ms");
            progress.Report(new UpscaleProgress(
                Percent: 99,
                CurrentFrame: framesRendered,
                TotalFrames: framesRendered,
                Fps: elapsedWatch.Elapsed.TotalSeconds > 0 ? framesRendered / elapsedWatch.Elapsed.TotalSeconds : 0,
                Speed: source.Fps > 0 && elapsedWatch.Elapsed.TotalSeconds > 0
                    ? (framesRendered / elapsedWatch.Elapsed.TotalSeconds) / source.Fps
                    : 0,
                TimeRemaining: TimeSpan.Zero,
                Message: "Finalizing encoded video stream...",
                LogLine:
                $"Rendered {framesRendered:N0} upscaled frames via libmpv GLSL shaders ({phaseSw.Elapsed.TotalSeconds:F2}s)."));
        }
        finally
        {
            frameChannel.Writer.TryComplete();
            while (frameChannel.Reader.TryRead(out var leftover))
            {
                ArrayPool<byte>.Shared.Return(leftover.Buffer);
            }

            try
            {
                writerTask.Wait(2000);
            }
            catch
            {
            }

            GC.KeepAlive(updateCb);
            if (renderCtx != 0)
            {
                try
                {
                    gl.MakeCurrent();
                    LibMpvNative.mpv_render_context_set_update_callback(renderCtx, 0, 0);
                    LibMpvNative.mpv_render_context_free(renderCtx);
                }
                catch
                {
                }
            }

            if (mpv != 0)
            {
                try
                {
                    LibMpvNative.mpv_terminate_destroy(mpv);
                }
                catch
                {
                }
            }
        }
    }

    private static async Task PipeWriterLoopAsync(
        ChannelReader<(byte[] Buffer, int Length)> reader,
        Stream ffmpegInputStream,
        CancellationToken ct)
    {
        await foreach (var (buf, len) in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await ffmpegInputStream.WriteAsync(buf.AsMemory(0, len), ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
    }

    private static void ConfigureMpvOptions(nint mpv, UpscalerSettings settings)
    {
        LibMpvNative.Check(LibMpvNative.SetOptionString(mpv, "vo", "libmpv"), "set vo=libmpv");
        LibMpvNative.Check(LibMpvNative.SetOptionString(mpv, "gpu-api", "opengl"), "set gpu-api=opengl");
        _ = LibMpvNative.SetOptionString(mpv, "ao", "null");
        _ = LibMpvNative.SetOptionString(mpv, "audio", "no");
        _ = LibMpvNative.SetOptionString(mpv, "sub", "no");
        _ = LibMpvNative.SetOptionString(mpv, "osd-level", "0");
        _ = LibMpvNative.SetOptionString(mpv, "pause", "no");
        _ = LibMpvNative.SetOptionString(mpv, "keep-open", "no");
        _ = LibMpvNative.SetOptionString(mpv, "untimed", "yes");
        _ = LibMpvNative.SetOptionString(mpv, "video-timing-offset", "0");
        _ = LibMpvNative.SetOptionString(mpv, "video-sync", "desync");
        _ = LibMpvNative.SetOptionString(mpv, "speed", "100");
        _ = LibMpvNative.SetOptionString(mpv, "framedrop", "no");
        _ = LibMpvNative.SetOptionString(mpv, "hr-seek-framedrop", "no");
        _ = LibMpvNative.SetOptionString(mpv, "interpolation", "no");
        _ = LibMpvNative.SetOptionString(mpv, "deinterlace", "auto");
        _ = LibMpvNative.SetOptionString(mpv, "hwdec",
            string.IsNullOrWhiteSpace(settings.MpvHwdec) ? "auto-copy" : settings.MpvHwdec);
        _ = LibMpvNative.SetOptionString(mpv, "scale",
            string.IsNullOrWhiteSpace(settings.MpvScaler) ? "ewa_lanczossharp" : settings.MpvScaler);
        _ = LibMpvNative.SetOptionString(mpv, "cscale",
            string.IsNullOrWhiteSpace(settings.MpvScaler) ? "ewa_lanczossharp" : settings.MpvScaler);
        _ = LibMpvNative.SetOptionString(mpv, "dscale", "mitchell");
    }

    private static unsafe nint CreateMpvRenderContext(nint mpv, OffscreenGlContext gl, UpscalerSettings settings)
    {
        string rendererChoice = settings.MpvRenderer;
        nint apiTypePtr = Marshal.StringToCoTaskMemUTF8("opengl");
        nint rendererNamePtr = Marshal.StringToCoTaskMemUTF8(
            string.Equals(rendererChoice, "gpu", StringComparison.OrdinalIgnoreCase) ? "gpu" : "gpu-next");
        try
        {
            var glInit = new LibMpvNative.MpvOpenglInitParams
            {
                GetProcAddress = gl.MpvGetProcAddressPointer,
                GetProcAddressCtx = 0
            };
            int advControl = 1;

            LibMpvNative.MpvRenderParam* @params = stackalloc LibMpvNative.MpvRenderParam[5];
            @params[0] = new LibMpvNative.MpvRenderParam
                { Type = LibMpvNative.MPV_RENDER_PARAM_API_TYPE, Data = apiTypePtr };
            @params[1] = new LibMpvNative.MpvRenderParam
                { Type = LibMpvNative.MPV_RENDER_PARAM_OPENGL_INIT_PARAMS, Data = (nint)(&glInit) };
            @params[2] = new LibMpvNative.MpvRenderParam
                { Type = LibMpvNative.MPV_RENDER_PARAM_ADVANCED_CONTROL, Data = (nint)(&advControl) };
            @params[3] = new LibMpvNative.MpvRenderParam
                { Type = LibMpvNative.MPV_RENDER_PARAM_RENDERER, Data = rendererNamePtr };
            @params[4] = new LibMpvNative.MpvRenderParam { Type = LibMpvNative.MPV_RENDER_PARAM_INVALID, Data = 0 };

            int rc = LibMpvNative.mpv_render_context_create(out nint ctx, mpv, @params);
            if (rc < 0)
            {
                // Retry with "gpu" fallback without explicit renderer if gpu-next fails
                @params[3] = new LibMpvNative.MpvRenderParam { Type = LibMpvNative.MPV_RENDER_PARAM_INVALID, Data = 0 };
                rc = LibMpvNative.mpv_render_context_create(out ctx, mpv, @params);
            }

            LibMpvNative.Check(rc, "mpv_render_context_create failed");
            return ctx;
        }
        finally
        {
            Marshal.FreeCoTaskMem(apiTypePtr);
            Marshal.FreeCoTaskMem(rendererNamePtr);
        }
    }

    private static unsafe void ControlLoop(
        nint mpv,
        string sourceFile,
        IProgress<UpscaleProgress> progress,
        long estimatedFrames,
        AutoResetEvent updateSignal,
        ManualResetEventSlim stopControl,
        Func<bool> isFileLoaded,
        Func<bool> isEof,
        Action<bool> setFileLoaded,
        Action<bool> setEof,
        CancellationToken ct)
    {
        LibMpvNative.Check(LibMpvNative.Command(mpv, "loadfile", sourceFile), "loadfile failed");

        // Wait for MPV_EVENT_FILE_LOADED
        while (!ct.IsCancellationRequested && !stopControl.IsSet && !isFileLoaded())
        {
            var ev = LibMpvNative.mpv_wait_event(mpv, 0.05);
            if (ev->EventId == LibMpvNative.MPV_EVENT_LOG_MESSAGE)
            {
                LogMpvEvent(ev, progress, estimatedFrames);
            }
            else if (ev->EventId == LibMpvNative.MPV_EVENT_FILE_LOADED)
            {
                setFileLoaded(true);
                updateSignal.Set();
                break;
            }
            else if (ev->EventId == LibMpvNative.MPV_EVENT_END_FILE)
            {
                var end = Marshal.PtrToStructure<LibMpvNative.MpvEventEndFile>(ev->Data);
                if (end.Reason == LibMpvNative.MPV_END_FILE_REASON_ERROR)
                    throw new InvalidOperationException(
                        $"libmpv failed to load video: {LibMpvNative.GetErrorMessage(end.Error)}");
                setEof(true);
                return;
            }
        }

        if (ct.IsCancellationRequested || stopControl.IsSet)
            return;

        while (!ct.IsCancellationRequested && !stopControl.IsSet && !isEof())
        {
            var ev = LibMpvNative.mpv_wait_event(mpv, 0.02);
            if (ev->EventId == LibMpvNative.MPV_EVENT_LOG_MESSAGE)
            {
                LogMpvEvent(ev, progress, estimatedFrames);
            }
            else if (ev->EventId == LibMpvNative.MPV_EVENT_END_FILE)
            {
                var end = Marshal.PtrToStructure<LibMpvNative.MpvEventEndFile>(ev->Data);
                if (end.Reason == LibMpvNative.MPV_END_FILE_REASON_ERROR)
                    throw new InvalidOperationException(
                        $"libmpv playback error: {LibMpvNative.GetErrorMessage(end.Error)}");
                setEof(true);
                updateSignal.Set();
                return;
            }
            else if (ev->EventId == LibMpvNative.MPV_EVENT_SHUTDOWN)
            {
                setEof(true);
                updateSignal.Set();
                return;
            }

            if (LibMpvNative.GetFlagProperty(mpv, "eof-reached"))
            {
                setEof(true);
                updateSignal.Set();
                return;
            }
        }
    }

    private static unsafe void LogMpvEvent(LibMpvNative.MpvEvent* ev, IProgress<UpscaleProgress> progress,
        long estimatedFrames)
    {
        if (ev->Data == 0) return;
        var msg = Marshal.PtrToStructure<LibMpvNative.MpvEventLogMessage>(ev->Data);
        string prefix = msg.Prefix != 0 ? Marshal.PtrToStringUTF8(msg.Prefix) ?? "mpv" : "mpv";
        string level = msg.Level != 0 ? Marshal.PtrToStringUTF8(msg.Level) ?? "info" : "info";
        string text = msg.Text != 0 ? (Marshal.PtrToStringUTF8(msg.Text) ?? string.Empty).Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;

        DiagnosticLogger.Verbose("libmpv", $"[{level}] [{prefix}] {text}");
        if (level is "error" or "fatal")
        {
            progress.Report(new UpscaleProgress(-1, 0, estimatedFrames, 0, 0, TimeSpan.Zero, "",
                $"[libmpv {level}] [{prefix}] {text}"));
        }
    }
}