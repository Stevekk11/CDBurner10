using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CDBurner10.Core.Native;

public static class SystemPowerHelper
{
    private const int SE_PRIVILEGE_ENABLED = 0x00000002;
    private const int TOKEN_QUERY = 0x00000008;
    private const int TOKEN_ADJUST_PRIVILEGES = 0x00000020;
    private const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";
    private const uint EWX_SHUTDOWN = 0x00000001;
    private const uint EWX_POWEROFF = 0x00000008;
    private const uint EWX_FORCEIFHUNG = 0x00000010;
    private const uint SHTDN_REASON_MAJOR_APPLICATION = 0x00040000;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TokPriv1Luid
    {
        public int Count;
        public long Luid;
        public int Attr;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr h, int acc, ref IntPtr phtok);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? host, string name, ref long pluid);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr htok, bool disall, ref TokPriv1Luid newst, int len, IntPtr prev, IntPtr relen);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    public static bool ShutdownComputer(bool forceIfHung = false)
    {
        try
        {
            IntPtr htok = IntPtr.Zero;
            if (OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, ref htok))
            {
                var tp = new TokPriv1Luid
                {
                    Count = 1,
                    Luid = 0,
                    Attr = SE_PRIVILEGE_ENABLED
                };
                if (LookupPrivilegeValue(null, SE_SHUTDOWN_NAME, ref tp.Luid))
                {
                    AdjustTokenPrivileges(htok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }

            uint flags = EWX_SHUTDOWN | EWX_POWEROFF;
            if (forceIfHung) flags |= EWX_FORCEIFHUNG;
            if (ExitWindowsEx(flags, SHTDN_REASON_MAJOR_APPLICATION))
            {
                return true;
            }
        }
        catch
        {
            // Fallback below
        }

        try
        {
            Process.Start(new ProcessStartInfo("shutdown", "/s /t 5")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
