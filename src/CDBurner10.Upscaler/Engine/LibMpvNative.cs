using System.Reflection;
using System.Runtime.InteropServices;
using CDBurner10.Upscaler.Services;

namespace CDBurner10.Upscaler.Engine;

internal static unsafe class LibMpvNative
{
    public const string DllName = "libmpv-2.dll";

    public const string FallbackDownloadUrl =
        "https://github.com/zhongfly/mpv-winbuild/releases/latest";

    private static int _resolverRegistered;
    private static nint _loadedHandle;
    private static string? _loadedPath;

    public const int MPV_FORMAT_NONE = 0;
    public const int MPV_FORMAT_STRING = 1;
    public const int MPV_FORMAT_OSD_STRING = 2;
    public const int MPV_FORMAT_FLAG = 3;
    public const int MPV_FORMAT_INT64 = 4;
    public const int MPV_FORMAT_DOUBLE = 5;

    public const int MPV_EVENT_NONE = 0;
    public const int MPV_EVENT_SHUTDOWN = 1;
    public const int MPV_EVENT_LOG_MESSAGE = 2;
    public const int MPV_EVENT_START_FILE = 6;
    public const int MPV_EVENT_END_FILE = 7;
    public const int MPV_EVENT_FILE_LOADED = 8;
    public const int MPV_EVENT_VIDEO_RECONFIG = 17;

    public const int MPV_END_FILE_REASON_EOF = 0;
    public const int MPV_END_FILE_REASON_STOP = 2;
    public const int MPV_END_FILE_REASON_QUIT = 3;
    public const int MPV_END_FILE_REASON_ERROR = 4;

    public const int MPV_RENDER_PARAM_INVALID = 0;
    public const int MPV_RENDER_PARAM_API_TYPE = 1;
    public const int MPV_RENDER_PARAM_OPENGL_INIT_PARAMS = 2;
    public const int MPV_RENDER_PARAM_OPENGL_FBO = 3;
    public const int MPV_RENDER_PARAM_FLIP_Y = 4;
    public const int MPV_RENDER_PARAM_DEPTH = 5;
    public const int MPV_RENDER_PARAM_ADVANCED_CONTROL = 10;
    public const int MPV_RENDER_PARAM_BLOCK_FOR_TARGET_TIME = 12;
    public const int MPV_RENDER_PARAM_RENDERER = 23;

    public const ulong MPV_RENDER_UPDATE_FRAME = 1UL << 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEvent
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventEndFile
    {
        public int Reason;
        public int Error;
        public long PlaylistEntryId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventLogMessage
    {
        public nint Prefix;
        public nint Level;
        public nint Text;
        public int LogLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvRenderParam
    {
        public int Type;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvOpenglInitParams
    {
        public nint GetProcAddress;
        public nint GetProcAddressCtx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvOpenglFbo
    {
        public int Fbo;
        public int W;
        public int H;
        public int InternalFormat;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate nint GetProcAddressFn(nint ctx, nint name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void RenderUpdateCallbackFn(nint cbCtx);

    public static void EnsureInitialized()
    {
        if (Interlocked.CompareExchange(ref _resolverRegistered, 1, 0) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(LibMpvNative).Assembly, ResolveDll);
        }

        // libmpv requires LC_NUMERIC="C" in C runtime before mpv_create()
        try
        {
            _ = ucrt_setlocale(4 /* LC_NUMERIC */, "C");
        }
        catch
        {
            // Ignore if CRT export differs
        }
    }

    public static string? FindLibMpvPath()
    {
        string custom = UpscalerSettingsService.Default.Current.CustomLibMpvPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
            return Path.GetFullPath(custom);

        string besideExe = Path.Combine(AppContext.BaseDirectory, DllName);
        if (File.Exists(besideExe))
            return besideExe;

        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && !string.IsNullOrWhiteSpace(dir); i++)
        {
            string candidate = Path.Combine(dir, "native", "upscaler", DllName);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var entry in pathEnv.Split(Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string candidate = Path.Combine(entry, DllName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    public static bool IsAvailable(out string? resolvedPath, out string? versionText)
    {
        EnsureInitialized();
        resolvedPath = FindLibMpvPath();
        if (resolvedPath is null)
        {
            versionText = null;
            return false;
        }

        try
        {
            ulong apiVer = mpv_client_api_version();
            ulong major = apiVer >> 16;
            ulong minor = apiVer & 0xFFFF;
            versionText = $"libmpv API {major}.{minor}";
            return true;
        }
        catch
        {
            versionText = null;
            return false;
        }
    }

    public static void ResetLoadedLibrary()
    {
        _loadedPath = null;
        _loadedHandle = 0;
    }

    private static nint ResolveDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals(DllName, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (_loadedHandle != 0)
            return _loadedHandle;

        string? path = FindLibMpvPath();
        if (!string.IsNullOrWhiteSpace(path) && NativeLibrary.TryLoad(path, out nint handle))
        {
            _loadedPath = path;
            _loadedHandle = handle;
            return handle;
        }

        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out nint sysHandle))
        {
            _loadedPath = libraryName;
            _loadedHandle = sysHandle;
            return sysHandle;
        }

        return 0;
    }

    public static string? LoadedPath => _loadedPath ?? FindLibMpvPath();

    public static string GetErrorMessage(int errorCode)
    {
        nint ptr = mpv_error_string(errorCode);
        return ptr != 0 ? Marshal.PtrToStringUTF8(ptr) ?? $"mpv error {errorCode}" : $"mpv error {errorCode}";
    }

    public static void Check(int errorCode, string context)
    {
        if (errorCode < 0)
            throw new InvalidOperationException($"{context}: {GetErrorMessage(errorCode)} ({errorCode})");
    }

    public static int SetOptionString(nint mpv, string name, string value)
    {
        nint nPtr = Marshal.StringToCoTaskMemUTF8(name);
        nint vPtr = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return mpv_set_option_string(mpv, nPtr, vPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(nPtr);
            Marshal.FreeCoTaskMem(vPtr);
        }
    }

    public static int SetPropertyString(nint mpv, string name, string value)
    {
        nint nPtr = Marshal.StringToCoTaskMemUTF8(name);
        nint vPtr = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return mpv_set_property_string(mpv, nPtr, vPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(nPtr);
            Marshal.FreeCoTaskMem(vPtr);
        }
    }

    public static string? GetPropertyString(nint mpv, string name)
    {
        nint nPtr = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            nint valPtr = mpv_get_property_string(mpv, nPtr);
            if (valPtr == 0) return null;
            try
            {
                return Marshal.PtrToStringUTF8(valPtr);
            }
            finally
            {
                mpv_free(valPtr);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(nPtr);
        }
    }

    public static bool GetFlagProperty(nint mpv, string name)
    {
        nint nPtr = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            int flag = 0;
            int rc = mpv_get_property(mpv, nPtr, MPV_FORMAT_FLAG, (nint)(&flag));
            return rc >= 0 && flag != 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(nPtr);
        }
    }

    public static double GetDoubleProperty(nint mpv, string name)
    {
        nint nPtr = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            double value = 0;
            int rc = mpv_get_property(mpv, nPtr, MPV_FORMAT_DOUBLE, (nint)(&value));
            return rc >= 0 ? value : 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(nPtr);
        }
    }

    public static int Command(nint mpv, params string[] args)
    {
        nint* ptrs = stackalloc nint[args.Length + 1];
        for (int i = 0; i < args.Length; i++)
            ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
        ptrs[args.Length] = 0;

        try
        {
            return mpv_command(mpv, (nint)ptrs);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (ptrs[i] != 0)
                    Marshal.FreeCoTaskMem(ptrs[i]);
            }
        }
    }

    public static int CommandAsync(nint mpv, ulong replyUserdata, params string[] args)
    {
        nint* ptrs = stackalloc nint[args.Length + 1];
        for (int i = 0; i < args.Length; i++)
            ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
        ptrs[args.Length] = 0;

        try
        {
            return mpv_command_async(mpv, replyUserdata, (nint)ptrs);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (ptrs[i] != 0)
                    Marshal.FreeCoTaskMem(ptrs[i]);
            }
        }
    }

    [DllImport("ucrtbase.dll", EntryPoint = "setlocale", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint ucrt_setlocale(int category, [MarshalAs(UnmanagedType.LPStr)] string locale);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong mpv_client_api_version();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint mpv_error_string(int error);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(nint data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint mpv_create();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(nint ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(nint ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_destroy(nint ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(nint ctx, nint name, nint data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(nint ctx, nint name, nint data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint mpv_get_property_string(nint ctx, nint name);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(nint ctx, nint name, int format, nint data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(nint ctx, nint args);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command_async(nint ctx, ulong replyUserdata, nint args);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern MpvEvent* mpv_wait_event(nint ctx, double timeout);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_wakeup(nint ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_request_log_messages(nint ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string minLevel);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_render_context_create(out nint res, nint mpv, MpvRenderParam* @params);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_set_update_callback(nint ctx, nint callback, nint callbackCtx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong mpv_render_context_update(nint ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_render_context_render(nint ctx, MpvRenderParam* @params);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_report_swap(nint ctx);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_free(nint ctx);
}