using System.Diagnostics;
using CDBurner10.Core.Burning;
using CDBurner10.Upscaler.Converters;
using Microsoft.UI.Dispatching;

namespace CDBurner10.Upscaler.Services;

public sealed class OperationTracker : IDisposable
{
    private const string ProgressTag = "op-progress";
    private const string StatusTag = "op-status";
    private const string OpGroup = "cdburner10-upscaler-op";

    private readonly object _lock = new();
    private TaskbarProgressService? _taskbar;
    private NotificationService? _notifications;
    private TrayIconService? _tray;
    private long _activeOpId;
    private bool _hasActiveOperation;

    public bool HasActiveOperation
    {
        get
        {
            lock (_lock) return _hasActiveOperation;
        }
    }

    public void AttachWindow(nint hwnd, DispatcherQueue dispatcher, TrayIconService? trayService)
    {
        lock (_lock)
        {
            _taskbar?.Dispose();
            _notifications?.Dispose();
            _tray = trayService;
            _taskbar = new TaskbarProgressService(hwnd, dispatcher);
            _notifications = new NotificationService(hwnd, dispatcher, trayService);
        }
    }

    public void OnWindowActivated()
    {
        TaskbarProgressService? taskbar;
        bool active;
        lock (_lock)
        {
            taskbar = _taskbar;
            active = _hasActiveOperation;
        }

        taskbar?.ClearErrorIfIdle(active);
    }

    public void DetachWindow()
    {
        lock (_lock)
        {
            _hasActiveOperation = false;
            _taskbar?.Dispose();
            _taskbar = null;
            _notifications?.Dispose();
            _notifications = null;
            _tray = null;
        }
    }

    public OperationSession Begin(string operationTitle, string startMessage)
    {
        long id = Interlocked.Increment(ref _activeOpId);
        TaskbarProgressService? taskbar;
        NotificationService? notifications;
        TrayIconService? tray;

        lock (_lock)
        {
            _hasActiveOperation = true;
            taskbar = _taskbar;
            notifications = _notifications;
            tray = _tray;
        }

        taskbar?.SetState(TaskbarProgressState.Indeterminate);
        tray?.UpdateTooltip($"CDBurner10 Upscaler — {operationTitle}");

        if (UpscalerSettingsService.Default.Current.ShowNotifications)
        {
            notifications?.Show(
                $"{operationTitle} — Started",
                startMessage,
                NotificationSeverity.Info,
                silent: false,
                tag: StatusTag,
                group: OpGroup,
                operationActive: true);
        }

        return new OperationSession(this, id, operationTitle);
    }

    internal void ReportProgress(
        long opId,
        string operationTitle,
        double percent,
        TimeSpan elapsed,
        TimeSpan reportedRemaining,
        ProgressMilestoneTracker milestoneTracker)
    {
        if (Volatile.Read(ref _activeOpId) != opId) return;

        TaskbarProgressService? taskbar;
        NotificationService? notifications;
        TrayIconService? tray;
        lock (_lock)
        {
            if (!_hasActiveOperation) return;
            taskbar = _taskbar;
            notifications = _notifications;
            tray = _tray;
        }

        double clamped = Math.Clamp(percent, 0.0, 100.0);
        if (clamped <= 0)
            taskbar?.SetState(TaskbarProgressState.Indeterminate);
        else
            taskbar?.SetProgress(clamped, TaskbarProgressState.Normal);

        tray?.UpdateTooltip($"CDBurner10 Upscaler — Upscaling ({(int)Math.Floor(clamped)}%)");

        var milestone = milestoneTracker.Evaluate(BurnPhase.Converting, clamped, elapsed, reportedRemaining);
        if (milestone is ProgressMilestone m && UpscalerSettingsService.Default.Current.ShowProgressNotifications)
        {
            string elapsedStr = FormatHelper.FormatDuration(m.Elapsed);
            string body = m.EstimatedRemaining is TimeSpan rem && rem > TimeSpan.Zero
                ? $"Upscaling: {m.MilestonePercent}% • Elapsed: {elapsedStr} • Remaining: ~{FormatHelper.FormatDuration(rem)} (ETA {(DateTime.Now + rem):HH:mm})"
                : $"Upscaling: {m.MilestonePercent}% • Elapsed: {elapsedStr}";

            notifications?.Show(
                operationTitle,
                body,
                NotificationSeverity.Info,
                silent: true,
                tag: ProgressTag,
                group: OpGroup,
                operationActive: true);
        }
    }

    internal void Complete(long opId, string operationTitle, string? summaryMessage, TimeSpan elapsed)
    {
        if (Volatile.Read(ref _activeOpId) != opId) return;

        TaskbarProgressService? taskbar;
        NotificationService? notifications;
        TrayIconService? tray;
        lock (_lock)
        {
            _hasActiveOperation = false;
            taskbar = _taskbar;
            notifications = _notifications;
            tray = _tray;
        }

        taskbar?.Clear();
        tray?.UpdateTooltip("CDBurner10 Upscaler");
        notifications?.ClearProgressNotification();

        if (UpscalerSettingsService.Default.Current.ShowNotifications)
        {
            string elapsedStr = FormatHelper.FormatDuration(elapsed);
            string body = string.IsNullOrWhiteSpace(summaryMessage)
                ? $"Completed (Elapsed: {elapsedStr})"
                : $"{summaryMessage} (Elapsed: {elapsedStr})";

            notifications?.Show(
                $"{operationTitle} — Completed",
                body,
                NotificationSeverity.Success,
                silent: false,
                tag: StatusTag,
                group: OpGroup,
                operationActive: false);
        }
    }

    internal void Cancel(long opId)
    {
        if (Volatile.Read(ref _activeOpId) != opId) return;

        TaskbarProgressService? taskbar;
        NotificationService? notifications;
        TrayIconService? tray;
        lock (_lock)
        {
            _hasActiveOperation = false;
            taskbar = _taskbar;
            notifications = _notifications;
            tray = _tray;
        }

        taskbar?.Clear();
        tray?.UpdateTooltip("CDBurner10 Upscaler");
        notifications?.ClearProgressNotification();
    }

    internal void Fail(long opId, string operationTitle, string errorMessage, double lastPercent)
    {
        if (Volatile.Read(ref _activeOpId) != opId) return;

        TaskbarProgressService? taskbar;
        NotificationService? notifications;
        TrayIconService? tray;
        lock (_lock)
        {
            _hasActiveOperation = false;
            taskbar = _taskbar;
            notifications = _notifications;
            tray = _tray;
        }

        taskbar?.SetError(lastPercent);
        tray?.UpdateTooltip("CDBurner10 Upscaler");
        notifications?.ClearProgressNotification();

        if (UpscalerSettingsService.Default.Current.ShowNotifications)
        {
            notifications?.Show(
                $"{operationTitle} — Failed",
                string.IsNullOrWhiteSpace(errorMessage) ? "Upscale operation failed." : errorMessage,
                NotificationSeverity.Error,
                silent: false,
                tag: StatusTag,
                group: OpGroup,
                operationActive: false);
        }
    }

    public void Dispose() => DetachWindow();
}

public sealed class OperationSession : IDisposable
{
    private readonly OperationTracker _tracker;
    private readonly long _opId;
    private readonly string _operationTitle;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly ProgressMilestoneTracker _milestoneTracker = new();
    private double _lastPercent;
    private int _finished;

    internal OperationSession(OperationTracker tracker, long opId, string operationTitle)
    {
        _tracker = tracker;
        _opId = opId;
        _operationTitle = operationTitle;
    }

    public void Report(double percent, TimeSpan timeRemaining = default)
    {
        if (Volatile.Read(ref _finished) != 0) return;
        _lastPercent = Math.Clamp(percent, 0, 100);
        _tracker.ReportProgress(_opId, _operationTitle, percent, _sw.Elapsed, timeRemaining, _milestoneTracker);
    }

    public void Complete(string? summaryMessage = null)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        _sw.Stop();
        _tracker.Complete(_opId, _operationTitle, summaryMessage, _sw.Elapsed);
    }

    public void Cancel()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        _sw.Stop();
        _tracker.Cancel(_opId);
    }

    public void Fail(string errorMessage)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        _sw.Stop();
        _tracker.Fail(_opId, _operationTitle, errorMessage, _lastPercent);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        _sw.Stop();
        _tracker.Cancel(_opId);
    }
}