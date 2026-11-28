using System.Runtime.InteropServices;

namespace CDBurner10.WinUI.Services;

/// <summary>
/// Pure-Win32 Shell_NotifyIconW tray icon with subclassed window message handling
/// for restoring or exiting the application when ExitToNotify is enabled,
/// plus fallback balloon/toast notifications when Windows App Notifications are unavailable.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint WM_USER = 0x0400;
    private const uint WM_TRAYICON = WM_USER + 0x101;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint NIN_BALLOONUSERCLICK = 0x0405;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_INFO = 0x00000001;
    private const uint NIIF_WARNING = 0x00000002;
    private const uint NIIF_ERROR = 0x00000003;
    private const uint NIIF_NOSOUND = 0x00000010;
    private const uint NIIF_RESPECT_QUIET_TIME = 0x00000080;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;

    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;

    private const nuint SubclassId = 0xCD10;
    private const uint CmdRestore = 1001;
    private const uint CmdExit = 1002;

    private readonly object _sync = new();
    private readonly nint _hwnd;
    private readonly string _appDisplayName;
    private readonly Func<string, string> _localizer;
    private readonly SubclassProc _subclassDelegate;
    private bool _subclassInstalled;
    private bool _iconAdded;
    private bool _isHiddenToTray;
    private nint _hIcon;
    private bool _disposed;
    private CancellationTokenSource? _tempCleanupCts;

    public event EventHandler? RestoreRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService(nint hwnd, string appDisplayName = "CDBurner10", Func<string, string>? localizer = null)
    {
        _hwnd = hwnd;
        _appDisplayName = string.IsNullOrWhiteSpace(appDisplayName) ? "CDBurner10" : appDisplayName;
        _localizer = localizer ?? (static s => s);
        _subclassDelegate = WindowSubclassProc;
        _subclassInstalled = SetWindowSubclass(_hwnd, _subclassDelegate, SubclassId, 0);
    }

    public void HideToTray(string? tooltip = null)
    {
        string tip = string.IsNullOrWhiteSpace(tooltip) ? _appDisplayName : tooltip;
        lock (_sync)
        {
            if (_disposed || _hwnd == 0) return;

            CancelTemporaryCleanupLocked();
            _isHiddenToTray = true;
            EnsureTrayIconLocked(tip);
        }

        ShowWindow(_hwnd, SW_HIDE);
    }

    public void RestoreFromTray()
    {
        lock (_sync)
        {
            if (_disposed || _hwnd == 0) return;

            _isHiddenToTray = false;
            CancelTemporaryCleanupLocked();
            RemoveTrayIconLocked();
        }

        ShowWindow(_hwnd, SW_RESTORE);
        SetForegroundWindow(_hwnd);
    }

    public void UpdateTooltip(string tooltip)
    {
        lock (_sync)
        {
            if (_disposed || _hwnd == 0 || !_iconAdded) return;

            var data = CreateNotifyData(tooltip);
            data.uFlags = NIF_TIP;
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
    }

    public void ShowBalloonNotification(
        string title,
        string message,
        NotificationSeverity severity,
        bool silent = false,
        bool keepIconDuringOperation = false)
    {
        lock (_sync)
        {
            if (_disposed || _hwnd == 0) return;

            CancelTemporaryCleanupLocked();
            EnsureTrayIconLocked(_appDisplayName);
            if (!_iconAdded) return;

            uint infoFlags = severity switch
            {
                NotificationSeverity.Warning => NIIF_WARNING,
                NotificationSeverity.Error => NIIF_ERROR,
                _ => NIIF_INFO
            };
            infoFlags |= NIIF_RESPECT_QUIET_TIME;
            if (silent) infoFlags |= NIIF_NOSOUND;

            var data = CreateNotifyData(_appDisplayName);
            data.uFlags = NIF_INFO;
            data.szInfoTitle = Truncate(title, 63);
            data.szInfo = Truncate(message, 255);
            data.dwInfoFlags = infoFlags;
            Shell_NotifyIconW(NIM_MODIFY, ref data);

            if (!_isHiddenToTray && !keepIconDuringOperation)
            {
                ScheduleTemporaryCleanupLocked(TimeSpan.FromSeconds(15));
            }
        }
    }

    public void ClearTemporaryIconSoon(TimeSpan delay)
    {
        lock (_sync)
        {
            if (_disposed || _isHiddenToTray || !_iconAdded) return;
            ScheduleTemporaryCleanupLocked(delay);
        }
    }

    public void RemoveTrayIcon()
    {
        lock (_sync)
        {
            CancelTemporaryCleanupLocked();
            RemoveTrayIconLocked();
        }
    }

    private void RemoveTrayIconLocked()
    {
        if (!_iconAdded) return;

        var data = CreateNotifyData(string.Empty);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _iconAdded = false;
    }

    private void ScheduleTemporaryCleanupLocked(TimeSpan delay)
    {
        CancelTemporaryCleanupLocked();
        var cts = new CancellationTokenSource();
        _tempCleanupCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
                lock (_sync)
                {
                    if (!cts.IsCancellationRequested && !_isHiddenToTray && !_disposed)
                    {
                        RemoveTrayIconLocked();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void CancelTemporaryCleanupLocked()
    {
        var cts = Interlocked.Exchange(ref _tempCleanupCts, null);
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            cts.Dispose();
        }
    }

    private void EnsureTrayIconLocked(string tooltip)
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
            szTip = Truncate(tooltip, 127),
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length > maxLength ? value[..maxLength] : value;
    }

    private nint WindowSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WM_TRAYICON)
        {
            uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg is WM_LBUTTONDBLCLK or NIN_BALLOONUSERCLICK)
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
            AppendMenuW(menu, MF_STRING, CmdRestore, _localizer($"Open {_appDisplayName}"));
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, CmdExit, _localizer("Exit"));

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
        nint iconToDestroy;
        bool removeSubclass;

        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            CancelTemporaryCleanupLocked();
            RemoveTrayIconLocked();

            removeSubclass = _subclassInstalled;
            _subclassInstalled = false;

            iconToDestroy = _hIcon;
            _hIcon = 0;
        }

        if (removeSubclass)
        {
            RemoveWindowSubclass(_hwnd, _subclassDelegate, SubclassId);
        }

        if (iconToDestroy != 0)
        {
            DestroyIcon(iconToDestroy);
        }
    }

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass,
        nuint dwRefData);

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
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass,
        nuint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);
}