using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CDBurner10.Core.Utilities;
using CDBurner10.Upscaler.Services;

namespace CDBurner10.Upscaler.Engine;

internal static unsafe class MpvShaderUpscaler
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

    private static void RunCore(
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
            "Initializing libmpv...", $"OpenGL context: {gl.GlRendererInfo}"));

        nint mpv = LibMpvNative.mpv_create();
        if (mpv == 0)
            throw new InvalidOperationException(
                "mpv_create() returned NULL. Ensure libmpv-2.dll is valid and LC_NUMERIC is C.");

        nint renderCtx = 0;
        Exception? controlException = null;

        using var updateSignal = new AutoResetEvent(false);
        using var renderThreadReady = new ManualResetEventSlim(false);
        using var stopControl = new ManualResetEventSlim(false);
        using var frameCaptured = new AutoResetEvent(false);

        int eofReached = 0;
        int fileLoaded = 0;
        int renderFinished = 0;
        int awaitingFrame = 1;
        long framesRendered = 0;
        double lastRenderedPts = -1.0;

        LibMpvNative.RenderUpdateCallbackFn updateCb = _ => updateSignal.Set();
        nint updateCbPtr = Marshal.GetFunctionPointerForDelegate(updateCb);

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
                        frameCaptured,
                        stopControl,
                        () => Volatile.Read(ref fileLoaded) != 0,
                        () => Volatile.Read(ref eofReached) != 0,
                        val => Volatile.Write(ref fileLoaded, val ? 1 : 0),
                        val => Volatile.Write(ref eofReached, val ? 1 : 0),
                        () => Volatile.Write(ref awaitingFrame, 1),
                        ct);
                }
                catch (Exception ex)
                {
                    controlException = ex;
                    Volatile.Write(ref renderFinished, 1);
                    updateSignal.Set();
                }
                finally
                {
                    Volatile.Write(ref renderFinished, 1);
                    updateSignal.Set();
                }
            })
            {
                Name = "CDBurner10.Upscaler.MpvControl",
                IsBackground = true
            };
            controlThread.Start();

            byte[] rgbaBuffer = new byte[targetWidth * targetHeight * 4];
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

            while (Volatile.Read(ref renderFinished) == 0 && !ct.IsCancellationRequested)
            {
                updateSignal.WaitOne(50);

                if (ct.IsCancellationRequested || Volatile.Read(ref renderFinished) != 0)
                    break;

                ulong flags = LibMpvNative.mpv_render_context_update(renderCtx);
                if ((flags & LibMpvNative.MPV_RENDER_UPDATE_FRAME) != 0 &&
                    Volatile.Read(ref fileLoaded) != 0)
                {
                    gl.MakeCurrent();
                    int rc = LibMpvNative.mpv_render_context_render(renderCtx, renderParams);
                    if (rc < 0)
                        throw new InvalidOperationException(
                            $"mpv_render_context_render failed: {LibMpvNative.GetErrorMessage(rc)}");

                    double pts = LibMpvNative.GetDoubleProperty(mpv, "time-pos");
                    bool isDuplicateFrame = framesRendered > 0 && pts >= 0 && lastRenderedPts >= 0 &&
                                            Math.Abs(pts - lastRenderedPts) < 1e-6;

                    // Only emit frame to encoder if we requested a frame step, haven't hit EOF, and it isn't a duplicate redraw
                    if (Volatile.Read(ref eofReached) == 0 &&
                        !isDuplicateFrame &&
                        Interlocked.CompareExchange(ref awaitingFrame, 0, 1) == 1)
                    {
                        gl.ReadFboRgba(rgbaBuffer);
                        ffmpegInputStream.Write(rgbaBuffer, 0, rgbaBuffer.Length);
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

                        frameCaptured.Set();
                    }
                }
            }

            stopControl.Set();
            LibMpvNative.mpv_wakeup(mpv);
            controlThread.Join(5000);

            ct.ThrowIfCancellationRequested();
            if (controlException is not null)
                throw controlException;

            ffmpegInputStream.Flush();
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
                LogLine: $"Rendered {framesRendered:N0} upscaled frames via libmpv GLSL shaders."));
        }
        finally
        {
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

    private static void ConfigureMpvOptions(nint mpv, UpscalerSettings settings)
    {
        LibMpvNative.Check(LibMpvNative.SetOptionString(mpv, "vo", "libmpv"), "set vo=libmpv");
        LibMpvNative.Check(LibMpvNative.SetOptionString(mpv, "gpu-api", "opengl"), "set gpu-api=opengl");
        _ = LibMpvNative.SetOptionString(mpv, "ao", "null");
        _ = LibMpvNative.SetOptionString(mpv, "audio", "no");
        _ = LibMpvNative.SetOptionString(mpv, "sub", "no");
        _ = LibMpvNative.SetOptionString(mpv, "osd-level", "0");
        _ = LibMpvNative.SetOptionString(mpv, "pause", "yes");
        _ = LibMpvNative.SetOptionString(mpv, "keep-open", "yes");
        _ = LibMpvNative.SetOptionString(mpv, "video-timing-offset", "0");
        _ = LibMpvNative.SetOptionString(mpv, "framedrop", "no");
        _ = LibMpvNative.SetOptionString(mpv, "hr-seek-framedrop", "no");
        _ = LibMpvNative.SetOptionString(mpv, "interpolation", "no");
        _ = LibMpvNative.SetOptionString(mpv, "hwdec",
            string.IsNullOrWhiteSpace(settings.MpvHwdec) ? "auto-copy" : settings.MpvHwdec);
        _ = LibMpvNative.SetOptionString(mpv, "scale",
            string.IsNullOrWhiteSpace(settings.MpvScaler) ? "ewa_lanczossharp" : settings.MpvScaler);
        _ = LibMpvNative.SetOptionString(mpv, "cscale",
            string.IsNullOrWhiteSpace(settings.MpvScaler) ? "ewa_lanczossharp" : settings.MpvScaler);
        _ = LibMpvNative.SetOptionString(mpv, "dscale", "mitchell");
    }

    private static nint CreateMpvRenderContext(nint mpv, OffscreenGlContext gl, UpscalerSettings settings)
    {
        nint apiTypePtr = Marshal.StringToCoTaskMemUTF8("opengl");
        nint rendererNamePtr = Marshal.StringToCoTaskMemUTF8(
            string.Equals(settings.MpvRenderer, "gpu", StringComparison.OrdinalIgnoreCase) ? "gpu" : "gpu-next");
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

    private static void ControlLoop(
        nint mpv,
        string sourceFile,
        IProgress<UpscaleProgress> progress,
        long estimatedFrames,
        AutoResetEvent updateSignal,
        AutoResetEvent frameCaptured,
        ManualResetEventSlim stopControl,
        Func<bool> isFileLoaded,
        Func<bool> isEof,
        Action<bool> setFileLoaded,
        Action<bool> setEof,
        Action requestNextFrame,
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

        // Wait for frame 0 capture, then deterministically step frame by frame
        while (!ct.IsCancellationRequested && !stopControl.IsSet && !isEof())
        {
            while (!frameCaptured.WaitOne(25))
            {
                if (ct.IsCancellationRequested || stopControl.IsSet)
                    return;

                DrainMpvEvents(mpv, progress, estimatedFrames, setEof);
                if (isEof() || LibMpvNative.GetFlagProperty(mpv, "eof-reached"))
                {
                    setEof(true);
                    return;
                }
            }

            if (ct.IsCancellationRequested || stopControl.IsSet)
                return;

            requestNextFrame();
            int stepRc = LibMpvNative.Command(mpv, "frame-step");
            DrainMpvEvents(mpv, progress, estimatedFrames, setEof);

            if (stepRc < 0 || isEof() || LibMpvNative.GetFlagProperty(mpv, "eof-reached"))
            {
                setEof(true);
                return;
            }
        }
    }

    private static void DrainMpvEvents(
        nint mpv,
        IProgress<UpscaleProgress> progress,
        long estimatedFrames,
        Action<bool> setEof)
    {
        while (true)
        {
            var ev = LibMpvNative.mpv_wait_event(mpv, 0);
            if (ev->EventId == LibMpvNative.MPV_EVENT_NONE)
                break;

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
            }
            else if (ev->EventId == LibMpvNative.MPV_EVENT_SHUTDOWN)
            {
                setEof(true);
            }
        }
    }

    private static void LogMpvEvent(LibMpvNative.MpvEvent* ev, IProgress<UpscaleProgress> progress,
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