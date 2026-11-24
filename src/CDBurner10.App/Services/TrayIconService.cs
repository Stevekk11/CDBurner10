using System.Runtime.InteropServices;

namespace CDBurner10.App.Services;

/// <summary>
/// Pure-Win32 Shell_NotifyIconW tray icon with subclassed window message handling
/// for restoring or exiting CDBurner10 when ExitToNotify ("Minimize / exit to notification tray") is enabled.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint WM_USER = 0x0400;
    private const uint WM_TRAYICON = WM_USER + 0x101;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;

    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;

    private const nuint SubclassId = 0xCD10;
    private const uint CmdRestore = 1001;
    private const uint CmdExit = 1002;

    private readonly nint _hwnd;
    private readonly SubclassProc _subclassDelegate;
    private bool _subclassInstalled;
    private bool _iconAdded;
    private nint _hIcon;
    private bool _disposed;

    public event EventHandler? RestoreRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService(nint hwnd)
    {
        _hwnd = hwnd;
        _subclassDelegate = WindowSubclassProc;
        _subclassInstalled = SetWindowSubclass(_hwnd, _subclassDelegate, SubclassId, 0);
    }

    public void HideToTray(string tooltip = "CDBurner10")
    {
        if (_disposed || _hwnd == 0) return;

        EnsureTrayIcon(tooltip);
        ShowWindow(_hwnd, SW_HIDE);
    }

    public void RestoreFromTray()
    {
        if (_disposed || _hwnd == 0) return;

        ShowWindow(_hwnd, SW_RESTORE);
        SetForegroundWindow(_hwnd);
        RemoveTrayIcon();
    }

    public void RemoveTrayIcon()
    {
        if (!_iconAdded) return;

        var data = CreateNotifyData(string.Empty);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _iconAdded = false;
    }

    private void EnsureTrayIcon(string tooltip)
    {
        if (_iconAdded) return;

        if (_hIcon == 0)
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
                _hIcon = ExtractIconW(0, exe, 0);
        }

        var data = CreateNotifyData(tooltip);
        data.uFlags = NIF_MESSAGE | NIF_TIP | (_hIcon != 0 ? NIF_ICON : 0u);
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = _hIcon;
        _iconAdded = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    private NOTIFYICONDATAW CreateNotifyData(string tooltip)
    {
        return new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip
        };
    }

    private nint WindowSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WM_TRAYICON)
        {
            uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == WM_LBUTTONDBLCLK)
            {
                RestoreRequested?.Invoke(this, EventArgs.Empty);
                return 0;
            }
            if (mouseMsg is WM_RBUTTONUP or WM_CONTEXTMENU)
            {
                ShowContextMenu();
                return 0;
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return;

        try
        {
            AppendMenuW(menu, MF_STRING, CmdRestore, Loc.T("Open CDBurner10"));
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, CmdExit, Loc.T("Exit"));

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hwnd);
            uint cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, _hwnd, 0);
            if (cmd == CmdRestore)
                RestoreRequested?.Invoke(this, EventArgs.Empty);
            else if (cmd == CmdExit)
                ExitRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RemoveTrayIcon();
        if (_subclassInstalled)
        {
            RemoveWindowSubclass(_hwnd, _subclassDelegate, SubclassId);
            _subclassInstalled = false;
        }

        if (_hIcon != 0)
        {
            DestroyIcon(_hIcon);
            _hIcon = 0;
        }
    }

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractIconW(nint hInst, string lpszExeFileName, uint nIconIndex);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hwnd, nint lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);
}
