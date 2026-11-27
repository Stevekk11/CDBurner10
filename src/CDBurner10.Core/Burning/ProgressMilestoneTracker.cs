namespace CDBurner10.Core.Burning;

public readonly record struct ProgressMilestone(
    BurnPhase Phase,
    int MilestonePercent,
    int ActualPercent,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining);

/// <summary>
/// Evaluates progress updates across phases and emits 10% milestone notifications
/// (10%, 20%, ..., 90%) along with elapsed and estimated remaining time.
/// </summary>
public sealed class ProgressMilestoneTracker
{
    private BurnPhase? _currentPhase;
    private int _lastMilestone;
    private TimeSpan _phaseStartElapsed;

    public static bool IsMilestonePhase(BurnPhase phase) =>
        phase is BurnPhase.Converting
            or BurnPhase.Burning
            or BurnPhase.Verifying
            or BurnPhase.Erasing
            or BurnPhase.Grabbing;

    public ProgressMilestone? Evaluate(
        BurnPhase phase,
        double percent,
        TimeSpan elapsed,
        TimeSpan reportedRemaining = default)
    {
        if (!IsMilestonePhase(phase))
            return null;

        if (_currentPhase != phase)
        {
            _currentPhase = phase;
            _lastMilestone = 0;
            _phaseStartElapsed = elapsed;
        }

        double clamped = Math.Clamp(percent, 0.0, 100.0);
        int actualPct = (int)Math.Floor(clamped);
        int milestone = (actualPct / 10) * 10;

        if (milestone < 10 || milestone >= 100 || milestone <= _lastMilestone)
            return null;

        _lastMilestone = milestone;

        TimeSpan? estimatedRemaining = null;
        if (reportedRemaining > TimeSpan.Zero)
        {
            estimatedRemaining = reportedRemaining;
        }
        else
        {
            TimeSpan phaseElapsed = elapsed - _phaseStartElapsed;
            if (clamped >= 2.0 && phaseElapsed >= TimeSpan.FromSeconds(1))
            {
                double remainingSeconds = phaseElapsed.TotalSeconds * (100.0 - clamped) / clamped;
                if (remainingSeconds > 0 && remainingSeconds < TimeSpan.FromDays(7).TotalSeconds)
                {
                    estimatedRemaining = TimeSpan.FromSeconds(remainingSeconds);
                }
            }
        }

        return new ProgressMilestone(phase, milestone, actualPct, elapsed, estimatedRemaining);
    }
}
