using System.Runtime.InteropServices;

namespace CDBurner10.Core.Native;

/// <summary>
/// P/Invoke declarations mirroring third_party\libdvdnav\src\dvdnav\dvdnav.h,
/// dvdnav_events.h, and third_party\libdvdread\src\dvdread\dvd_reader.h.
/// </summary>
public static class DvdNavInterop
{
    public const string DvdNavDllName = "libdvdnav-4.dll";
    public const string DvdReadDllName = "libdvdread-8.dll";

    // From dvd_reader.h
    public const int DVD_VIDEO_LB_LEN = 2048;

    // From dvdnav.h
    public const int DVDNAV_STATUS_ERR = 0;
    public const int DVDNAV_STATUS_OK = 1;

    // From dvdnav_events.h
    public const int DVDNAV_BLOCK_OK = 0;
    public const int DVDNAV_NOP = 1;
    public const int DVDNAV_STILL_FRAME = 2;
    public const int DVDNAV_SPU_STREAM_CHANGE = 3;
    public const int DVDNAV_AUDIO_STREAM_CHANGE = 4;
    public const int DVDNAV_VTS_CHANGE = 5;
    public const int DVDNAV_CELL_CHANGE = 6;
    public const int DVDNAV_NAV_PACKET = 7;
    public const int DVDNAV_STOP = 8;
    public const int DVDNAV_HIGHLIGHT = 9;
    public const int DVDNAV_SPU_CLUT_CHANGE = 10;
    public const int DVDNAV_HOP_CHANNEL = 12;
    public const int DVDNAV_WAIT = 13;

    public static bool IsAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, DvdNavDllName)) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, DvdReadDllName));

    // =========================================================================
    // libdvdnav (dvdnav.h)
    // =========================================================================

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_open(
        out IntPtr dest,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_close(IntPtr self);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_reset(IntPtr self);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr dvdnav_err_to_string(IntPtr self);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr dvdnav_version();

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_set_region_mask(IntPtr self, int region_mask);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_get_region_mask(IntPtr self, out int region_mask);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_get_disk_region_mask(IntPtr self, out int region_mask);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_set_readahead_flag(IntPtr self, int read_ahead_flag);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_get_readahead_flag(IntPtr self, out int read_ahead_flag);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_set_PGC_positioning_flag(IntPtr self, int pgc_based_flag);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_get_PGC_positioning_flag(IntPtr self, out int pgc_based_flag);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_get_next_block(
        IntPtr self,
        [Out] byte[] buf,
        out int @event,
        out int len);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_still_skip(IntPtr self);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_wait_skip(IntPtr self);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern uint dvdnav_get_next_still_flag(IntPtr self);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_stop(IntPtr self);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_get_number_of_titles(IntPtr self, out int titles);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_get_number_of_parts(IntPtr self, int title, out int parts);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_get_number_of_angles(IntPtr self, int title, out int angles);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_title_play(IntPtr self, int title);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_part_play(IntPtr self, int title, int part);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_program_play(IntPtr self, int title, int pgcn, int pgn);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern uint dvdnav_describe_title_chapters(
        IntPtr self,
        int title,
        out IntPtr times,
        out ulong duration);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_current_title_info(IntPtr self, out int title, out int part);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_current_title_program(IntPtr self, out int title, out int pgcn, out int pgn);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int dvdnav_get_position(IntPtr self, out uint pos, out uint len);

    [DllImport(DvdNavDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern long dvdnav_get_current_time(IntPtr self);

    // =========================================================================
    // libdvdread (dvd_reader.h)
    // =========================================================================

    [DllImport(DvdReadDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr DVDOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(DvdReadDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void DVDClose(IntPtr dvd);

    [DllImport(DvdReadDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern void DVDCloseFile(IntPtr dvd_file);

    [DllImport(DvdReadDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern nint DVDFileSize(IntPtr dvd_file);

    [DllImport(DvdReadDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern nint DVDReadBlocks(IntPtr dvd_file, int offset, nuint block_count, [Out] byte[] data);

    [DllImport(DvdReadDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern nint DVDReadBytes(IntPtr dvd_file, [Out] byte[] data, nuint size);

    [DllImport(DvdReadDllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int DVDFileSeek(IntPtr dvd_file, int seek_byte);

    // =========================================================================
    // Helpers
    // =========================================================================

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "free")]
    public static extern void CrtFree(IntPtr ptr);

    public static string GetErrorString(IntPtr self)
    {
        if (self == IntPtr.Zero) return "libdvdnav handle is null.";
        IntPtr ptr = dvdnav_err_to_string(self);
        return Marshal.PtrToStringUTF8(ptr) ?? "Unknown libdvdnav error.";
    }

    public static string GetVersion()
    {
        IntPtr ptr = dvdnav_version();
        return Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
    }
}
