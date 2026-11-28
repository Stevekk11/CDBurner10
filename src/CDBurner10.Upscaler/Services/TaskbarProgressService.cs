using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace CDBurner10.Upscaler.Services;

public enum TaskbarProgressState
{
    NoProgress = 0,
    Indeterminate = 0x1,
    Normal = 0x2,
    Error = 0x4,
    Paused = 0x8
}

/// <summary>
/// Controls the Windows taskbar button progress indicator via ITaskbarList3.
/// </summary>
public sealed class TaskbarProgressService : IDisposable
{
    private readonly nint _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private ITaskbarList3? _taskbar;
    private bool _initialized;
    private bool _hasActiveError;
    private bool _disposed;
    private CancellationTokenSource? _errorClearCts;

    public TaskbarProgressService(nint hwnd, DispatcherQueue dispatcher)
    {
        _hwnd = hwnd;
        _dispatcher = dispatcher;
    }

    public void SetState(TaskbarProgressState state)
    {
        CancelErrorClear();
        _hasActiveError = state == TaskbarProgressState.Error;
        RunOnUiThread(() =>
        {
            if (_disposed || _hwnd == 0) return;
            EnsureTaskbar();
            _taskbar?.SetProgressState(_hwnd, (TBPFLAG)state);
        });
    }

    public void SetProgress(double percent, TaskbarProgressState state = TaskbarProgressState.Normal)
    {
        CancelErrorClear();
        _hasActiveError = state == TaskbarProgressState.Error;
        ulong completed = (ulong)Math.Clamp(Math.Round(percent * 10), 0, 1000);
        RunOnUiThread(() =>
        {
            if (_disposed || _hwnd == 0) return;
            EnsureTaskbar();
            if (_taskbar is null) return;
            _taskbar.SetProgressState(_hwnd, (TBPFLAG)state);
            _taskbar.SetProgressValue(_hwnd, completed, 1000);
        });
    }

    public void SetError(double lastPercent)
    {
        CancelErrorClear();
        _hasActiveError = true;
        double displayPercent = lastPercent > 5 ? lastPercent : 100;
        ulong completed = (ulong)Math.Clamp(Math.Round(displayPercent * 10), 0, 1000);
        bool isForeground = GetForegroundWindow() == _hwnd;

        RunOnUiThread(() =>
        {
            if (_disposed || _hwnd == 0) return;
            EnsureTaskbar();
            if (_taskbar is null) return;
            _taskbar.SetProgressState(_hwnd, TBPFLAG.TBPF_ERROR);
            _taskbar.SetProgressValue(_hwnd, completed, 1000);
        });

        if (isForeground)
        {
            var cts = new CancellationTokenSource();
            _errorClearCts = cts;
            _ = ClearErrorAfterDelayAsync(cts.Token);
        }
    }

    public void ClearErrorIfIdle(bool isOperationActive)
    {
        if (!isOperationActive && _hasActiveError)
            Clear();
    }

    public void Clear()
    {
        CancelErrorClear();
        _hasActiveError = false;
        RunOnUiThread(() =>
        {
            if (_disposed || _hwnd == 0 || _taskbar is null) return;
            _taskbar.SetProgressState(_hwnd, TBPFLAG.TBPF_NOPROGRESS);
        });
    }

    private async Task ClearErrorAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), ct);
            if (!ct.IsCancellationRequested && _hasActiveError)
                Clear();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelErrorClear()
    {
        var cts = Interlocked.Exchange(ref _errorClearCts, null);
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

    private void RunOnUiThread(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => action());
    }

    private void EnsureTaskbar()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var clsid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");
            var type = Type.GetTypeFromCLSID(clsid);
            if (type is not null)
            {
                _taskbar = Activator.CreateInstance(type) as ITaskbarList3;
                _taskbar?.HrInit();
            }
        }
        catch
        {
            _taskbar = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelErrorClear();

        if (_taskbar is not null)
        {
            try
            {
                if (_hwnd != 0) _taskbar.SetProgressState(_hwnd, TBPFLAG.TBPF_NOPROGRESS);
                if (Marshal.IsComObject(_taskbar)) Marshal.ReleaseComObject(_taskbar);
            }
            catch
            {
            }

            _taskbar = null;
        }
    }

    private enum TBPFLAG
    {
        TBPF_NOPROGRESS = 0,
        TBPF_INDETERMINATE = 0x1,
        TBPF_NORMAL = 0x2,
        TBPF_ERROR = 0x4,
        TBPF_PAUSED = 0x8
    }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig]
        int HrInit();

        [PreserveSig]
        int AddTab(nint hwnd);

        [PreserveSig]
        int DeleteTab(nint hwnd);

        [PreserveSig]
        int ActivateTab(nint hwnd);

        [PreserveSig]
        int SetActiveAlt(nint hwnd);

        [PreserveSig]
        int MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        [PreserveSig]
        int SetProgressValue(nint hwnd, ulong ullCompleted, ulong ullTotal);

        [PreserveSig]
        int SetProgressState(nint hwnd, TBPFLAG tbpFlags);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}