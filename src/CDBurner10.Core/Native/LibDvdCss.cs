using System.Runtime.InteropServices;

namespace CDBurner10.Core.Native;

/// <summary>
/// Minimal P/Invoke surface for the bundled FOSS libdvdcss library.
/// libdvdcss performs the CSS authentication and decrypts sectors while reading.
/// </summary>
internal static class LibDvdCss
{
    public const string DllName = "libdvdcss-2.dll";

    /// <summary>Ask libdvdcss to decrypt the returned sectors.</summary>
    public const int ReadDecrypt = 0x01;

    public const int BlockSize = 2048;

    public static bool IsAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, DllName));

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr dvdcss_open(string pszTarget);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dvdcss_close(IntPtr dvdcss);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dvdcss_seek(IntPtr dvdcss, int iBlocks, int iFlags);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int dvdcss_read(IntPtr dvdcss, byte[] pBuffer, int iBlocks, int iFlags);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dvdcss_error(IntPtr dvdcss);

    public static string GetError(IntPtr dvdcss) =>
        Marshal.PtrToStringAnsi(dvdcss_error(dvdcss)) ?? "Unknown libdvdcss error.";
}
