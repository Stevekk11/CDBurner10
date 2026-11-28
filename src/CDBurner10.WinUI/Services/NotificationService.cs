using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace CDBurner10.WinUI.Services;

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// Sends Windows desktop notifications via Windows App SDK AppNotificationManager when available,
/// falling back seamlessly to Shell_NotifyIconW balloon/toast notifications via TrayIconService.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private const int SW_RESTORE = 9;

    private readonly nint _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly TrayIconService? _trayService;
    private readonly string _appDisplayName;
    private readonly string _notificationGroup;
    private bool _appNotificationsRegistered;
    private bool _disposed;

    public NotificationService(
        nint hwnd,
        DispatcherQueue dispatcher,
        TrayIconService? trayService,
        string appDisplayName = "CDBurner10",
        string notificationGroup = "cdburner10-op")
    {
        _hwnd = hwnd;
        _dispatcher = dispatcher;
        _trayService = trayService;
        _appDisplayName = string.IsNullOrWhiteSpace(appDisplayName) ? "CDBurner10" : appDisplayName;
        _notificationGroup = string.IsNullOrWhiteSpace(notificationGroup) ? "cdburner10-op" : notificationGroup;
        TryInitializeAppNotifications();
    }

    private void TryInitializeAppNotifications()
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
                return;

            var mgr = AppNotificationManager.Default;
            mgr.NotificationInvoked += OnNotificationInvoked;

            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                mgr.Register(_appDisplayName, new Uri(iconPath));
            }
            else
            {
                mgr.Register();
            }

            _appNotificationsRegistered = true;
        }
        catch
        {
            _appNotificationsRegistered = false;
            try
            {
                AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            }
            catch
            {
            }
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || _hwnd == 0) return;
            if (_trayService is not null)
            {
                _trayService.RestoreFromTray();
            }
            else
            {
                ShowWindow(_hwnd, SW_RESTORE);
                SetForegroundWindow(_hwnd);
            }
        });
    }

    public void Show(
        string title,
        string message,
        NotificationSeverity severity = NotificationSeverity.Info,
        bool silent = false,
        string? tag = null,
        string? group = null,
        bool operationActive = false)
    {
        if (_disposed) return;

        if (_appNotificationsRegistered)
        {
            try
            {
                var builder = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message);

                if (silent)
                    builder.MuteAudio();

                if (!string.IsNullOrWhiteSpace(tag))
                    builder.SetTag(tag);

                if (!string.IsNullOrWhiteSpace(group))
                    builder.SetGroup(group);

                AppNotificationManager.Default.Show(builder.BuildNotification());
                return;
            }
            catch
            {
                // Fall back to Shell_NotifyIconW below
            }
        }

        RunOnUiThread(() =>
            _trayService?.ShowBalloonNotification(title, message, severity, silent, operationActive));
    }

    public void ClearProgressNotification()
    {
        if (_disposed) return;

        if (_appNotificationsRegistered)
        {
            try
            {
                _ = AppNotificationManager.Default.RemoveByTagAndGroupAsync("op-progress", _notificationGroup);
            }
            catch
            {
            }
        }

        RunOnUiThread(() => _trayService?.ClearTemporaryIconSoon(TimeSpan.FromSeconds(15)));
    }

    private void RunOnUiThread(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => action());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_appNotificationsRegistered)
        {
            try
            {
                AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
                AppNotificationManager.Default.Unregister();
            }
            catch
            {
            }

            _appNotificationsRegistered = false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
}