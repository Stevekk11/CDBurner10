using System.Diagnostics;
using CDBurner10.App.Converters;
using CDBurner10.Core.Burning;
using CDBurner10.Core.Settings;
using Microsoft.UI.Dispatching;

namespace CDBurner10.App.Services;

/// <summary>
/// Coordinates taskbar progress, tray tooltips, and desktop notifications
/// (start, 10% milestones with estimates, completion, and error) across all long-running operations.
/// </summary>
public sealed class OperationTracker : IDisposable
{
    private const string ProgressTag = "op-progress";
    private const string StatusTag = "op-status";
    private const string OpGroup = "cdburner10-op";

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
        tray?.UpdateTooltip($"CDBurner10 — {operationTitle}");

        if (SettingsService.Default.Current.ShowNotifications)
        {
            notifications?.Show(
                $"{operationTitle} — {Loc.T("Started")}",
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
        BurnPhase phase,
        double percent,
        TimeSpan elapsed,
        TimeSpan reportedRemaining,
        ProgressMilestoneTracker milestoneTracker,
        bool isPaused)
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
        if (!isPaused)
        {
            if (phase == BurnPhase.Preparing && clamped <= 0)
                taskbar?.SetState(TaskbarProgressState.Indeterminate);
            else
                taskbar?.SetProgress(clamped, TaskbarProgressState.Normal);
        }

        string phaseLabel = GetPhaseLabel(phase);
        tray?.UpdateTooltip($"CDBurner10 — {phaseLabel} ({(int)Math.Floor(clamped)}%)");

        var milestone = milestoneTracker.Evaluate(phase, clamped, elapsed, reportedRemaining);
        if (milestone is ProgressMilestone m && SettingsService.Default.Current.ShowProgressNotifications)
        {
            string elapsedStr = FormatHelper.FormatDuration(m.Elapsed);
            string body;
            if (m.EstimatedRemaining is TimeSpan rem && rem > TimeSpan.Zero)
            {
                var eta = DateTime.Now + rem;
                body = $"{phaseLabel}: {m.MilestonePercent}% • {Loc.T("Elapsed")}: {elapsedStr} • {Loc.T("Remaining")}: ~{FormatHelper.FormatDuration(rem)} ({Loc.T("ETA")} {eta:HH:mm})";
            }
            else
            {
                body = $"{phaseLabel}: {m.MilestonePercent}% • {Loc.T("Elapsed")}: {elapsedStr}";
            }

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

    internal void SetPaused(long opId, bool paused, double lastPercent)
    {
        if (Volatile.Read(ref _activeOpId) != opId) return;

        TaskbarProgressService? taskbar;
        lock (_lock)
        {
            if (!_hasActiveOperation) return;
            taskbar = _taskbar;
        }

        if (paused)
            taskbar?.SetProgress(lastPercent, TaskbarProgressState.Paused);
        else if (lastPercent > 0)
            taskbar?.SetProgress(lastPercent, TaskbarProgressState.Normal);
        else
            taskbar?.SetState(TaskbarProgressState.Indeterminate);
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
        tray?.UpdateTooltip("CDBurner10");
        notifications?.ClearProgressNotification();

        if (SettingsService.Default.Current.ShowNotifications)
        {
            string elapsedStr = FormatHelper.FormatDuration(elapsed);
            string body = string.IsNullOrWhiteSpace(summaryMessage)
                ? $"{Loc.T("Completed")} ({Loc.T("Elapsed")}: {elapsedStr})"
                : $"{summaryMessage} ({Loc.T("Elapsed")}: {elapsedStr})";

            notifications?.Show(
                $"{operationTitle} — {Loc.T("Completed")}",
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
        tray?.UpdateTooltip("CDBurner10");
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
        tray?.UpdateTooltip("CDBurner10");
        notifications?.ClearProgressNotification();

        if (SettingsService.Default.Current.ShowNotifications)
        {
            notifications?.Show(
                $"{operationTitle} — {Loc.T("Failed")}",
                string.IsNullOrWhiteSpace(errorMessage) ? Loc.T("Operation failed.") : errorMessage,
                NotificationSeverity.Error,
                silent: false,
                tag: StatusTag,
                group: OpGroup,
                operationActive: false);
        }
    }

    private static string GetPhaseLabel(BurnPhase phase) => phase switch
    {
        BurnPhase.Preparing => Loc.T("Preparing"),
        BurnPhase.Converting => Loc.T("Converting"),
        BurnPhase.Burning => Loc.T("Burning"),
        BurnPhase.ClosingSession => Loc.T("Closing session"),
        BurnPhase.Verifying => Loc.T("Verifying"),
        BurnPhase.Erasing => Loc.T("Erasing"),
        BurnPhase.Grabbing => Loc.T("Reading disc"),
        BurnPhase.Completed => Loc.T("Completed"),
        BurnPhase.Cancelled => Loc.T("Cancelled"),
        BurnPhase.Failed => Loc.T("Failed"),
        _ => phase.ToString()
    };

    public void Dispose()
    {
        DetachWindow();
    }
}

public sealed class OperationSession : IDisposable
{
    private readonly OperationTracker _tracker;
    private readonly long _opId;
    private readonly string _operationTitle;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly ProgressMilestoneTracker _milestoneTracker = new();
    private double _lastPercent;
    private bool _isPaused;
    private int _finished;

    internal OperationSession(OperationTracker tracker, long opId, string operationTitle)
    {
        _tracker = tracker;
        _opId = opId;
        _operationTitle = operationTitle;
    }

    public void Report(BurnProgress progress)
    {
        if (Volatile.Read(ref _finished) != 0) return;
        _lastPercent = Math.Clamp(progress.Percent, 0, 100);
        _tracker.ReportProgress(
            _opId,
            _operationTitle,
            progress.Phase,
            progress.Percent,
            _sw.Elapsed,
            progress.TimeRemaining,
            _milestoneTracker,
            _isPaused);
    }

    public void Report(EraseProgress progress)
    {
        if (Volatile.Read(ref _finished) != 0) return;
        _lastPercent = Math.Clamp(progress.Percent, 0, 100);
        _tracker.ReportProgress(
            _opId,
            _operationTitle,
            BurnPhase.Erasing,
            progress.Percent,
            _sw.Elapsed,
            progress.TimeRemaining,
            _milestoneTracker,
            _isPaused);
    }

    public void Report(double percent, BurnPhase phase = BurnPhase.Burning, TimeSpan timeRemaining = default)
    {
        if (Volatile.Read(ref _finished) != 0) return;
        _lastPercent = Math.Clamp(percent, 0, 100);
        _tracker.ReportProgress(
            _opId,
            _operationTitle,
            phase,
            percent,
            _sw.Elapsed,
            timeRemaining,
            _milestoneTracker,
            _isPaused);
    }

    public void SetPaused(bool paused)
    {
        if (Volatile.Read(ref _finished) != 0) return;
        _isPaused = paused;
        _tracker.SetPaused(_opId, paused, _lastPercent);
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
