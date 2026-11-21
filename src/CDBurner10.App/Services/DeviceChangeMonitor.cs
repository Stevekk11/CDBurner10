using System.Runtime.InteropServices;

namespace CDBurner10.App.Services;

public sealed class DeviceChangeEventArgs(char driveLetter, bool isArrival) : EventArgs
{
    public char DriveLetter { get; } = driveLetter;
    public bool IsArrival { get; } = isArrival;
}

/// <summary>
/// Hooks WM_DEVICECHANGE on a WinUI 3 Window HWND using comctl32 SetWindowSubclass
/// to detect optical disc arrival and removal events in real time.
/// </summary>
public sealed partial class DeviceChangeMonitor : IDisposable
{
    private const uint WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 0x00000002;
    private const nuint SubclassId = 0xCDB10;

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_HDR
    {
        public uint dbch_size;
        public uint dbch_devicetype;
        public uint dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_VOLUME
    {
        public uint dbcv_size;
        public uint dbcv_devicetype;
        public uint dbcv_reserved;
        public uint dbcv_unitmask;
        public ushort dbcv_flags;
    }

    private readonly nint _hwnd;
    private readonly SubclassProc _proc;
    private bool _disposed;

    public event EventHandler<DeviceChangeEventArgs>? MediaChanged;

    public DeviceChangeMonitor(nint hwnd)
    {
        _hwnd = hwnd;
        _proc = WindowProc;
        SetWindowSubclass(_hwnd, _proc, SubclassId, 0);
    }

    private nint WindowProc(nint hWnd, uint uMsg, nuint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WM_DEVICECHANGE && lParam != 0)
        {
            int evt = (int)wParam;
            if (evt is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
            {
                var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
                if (hdr.dbch_devicetype == DBT_DEVTYP_VOLUME)
                {
                    var vol = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
                    uint mask = vol.dbcv_unitmask;
                    bool arrival = evt == DBT_DEVICEARRIVAL;
                    for (int i = 0; i < 26; i++)
                    {
                        if ((mask & (1u << i)) != 0)
                        {
                            char letter = (char)('A' + i);
                            MediaChanged?.Invoke(this, new DeviceChangeEventArgs(letter, arrival));
                        }
                    }
                }
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        }
    }
}
