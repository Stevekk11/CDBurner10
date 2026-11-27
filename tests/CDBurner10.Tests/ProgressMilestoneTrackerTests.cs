using CDBurner10.Core.Burning;

namespace CDBurner10.Tests;

public sealed class ProgressMilestoneTrackerTests
{
    [Theory]
    [InlineData(BurnPhase.Preparing)]
    [InlineData(BurnPhase.ClosingSession)]
    [InlineData(BurnPhase.Completed)]
    [InlineData(BurnPhase.Cancelled)]
    [InlineData(BurnPhase.Failed)]
    public void Evaluate_NonMilestonePhase_ReturnsNull(BurnPhase phase)
    {
        var tracker = new ProgressMilestoneTracker();

        Assert.False(ProgressMilestoneTracker.IsMilestonePhase(phase));
        Assert.Null(tracker.Evaluate(phase, 50, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Evaluate_EmitsTenThroughNinetyInOrder_AndIgnoresDuplicatesOrBounds()
    {
        var tracker = new ProgressMilestoneTracker();

        Assert.Null(tracker.Evaluate(BurnPhase.Burning, -5, TimeSpan.FromSeconds(1)));
        Assert.Null(tracker.Evaluate(BurnPhase.Burning, 9.9, TimeSpan.FromSeconds(5)));

        for (int expected = 10; expected <= 90; expected += 10)
        {
            var hit = tracker.Evaluate(BurnPhase.Burning, expected + 0.4, TimeSpan.FromSeconds(expected));
            Assert.NotNull(hit);
            Assert.Equal(BurnPhase.Burning, hit.Value.Phase);
            Assert.Equal(expected, hit.Value.MilestonePercent);
            Assert.Equal(expected, hit.Value.ActualPercent);

            // Same bucket or regression must not emit again
            Assert.Null(tracker.Evaluate(BurnPhase.Burning, expected + 5.9, TimeSpan.FromSeconds(expected + 5)));
            Assert.Null(tracker.Evaluate(BurnPhase.Burning, expected - 1, TimeSpan.FromSeconds(expected + 6)));
        }

        Assert.Null(tracker.Evaluate(BurnPhase.Burning, 100, TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public void Evaluate_JumpingBuckets_EmitsCurrentBucketOnce()
    {
        var tracker = new ProgressMilestoneTracker();

        Assert.Null(tracker.Evaluate(BurnPhase.Converting, 5, TimeSpan.FromSeconds(2)));

        var m30 = tracker.Evaluate(BurnPhase.Converting, 35.7, TimeSpan.FromSeconds(10));
        Assert.NotNull(m30);
        Assert.Equal(30, m30.Value.MilestonePercent);
        Assert.Equal(35, m30.Value.ActualPercent);

        // Staying within the 30% bucket does not re-emit
        Assert.Null(tracker.Evaluate(BurnPhase.Converting, 38.9, TimeSpan.FromSeconds(12)));

        var m40 = tracker.Evaluate(BurnPhase.Converting, 40.0, TimeSpan.FromSeconds(14));
        Assert.NotNull(m40);
        Assert.Equal(40, m40.Value.MilestonePercent);
    }

    [Fact]
    public void Evaluate_PhaseTransition_ResetsMilestonesAndUsesPhaseRelativeElapsed()
    {
        var tracker = new ProgressMilestoneTracker();

        // Phase 1: Grabbing reaches 80% at 80s total elapsed
        var grab80 = tracker.Evaluate(BurnPhase.Grabbing, 80, TimeSpan.FromSeconds(80));
        Assert.NotNull(grab80);
        Assert.Equal(80, grab80.Value.MilestonePercent);

        // Phase 2: Burning begins at 0% at 80s, reaches 10% at 90s (10s into Burning)
        Assert.Null(tracker.Evaluate(BurnPhase.Burning, 0, TimeSpan.FromSeconds(80)));
        var burn10 = tracker.Evaluate(BurnPhase.Burning, 10, TimeSpan.FromSeconds(90));
        Assert.NotNull(burn10);
        Assert.Equal(BurnPhase.Burning, burn10.Value.Phase);
        Assert.Equal(10, burn10.Value.MilestonePercent);
        Assert.Equal(TimeSpan.FromSeconds(90), burn10.Value.Elapsed);
        // Phase elapsed is 90 - 80 = 10s for 10% -> remaining = 10 * (90 / 10) = 90s
        Assert.Equal(TimeSpan.FromSeconds(90), burn10.Value.EstimatedRemaining);

        // Phase 3: Verifying starting directly at 10% sets phase start to 200s -> phaseElapsed = 0 -> null ETA
        var verify10 = tracker.Evaluate(BurnPhase.Verifying, 10, TimeSpan.FromSeconds(200));
        Assert.NotNull(verify10);
        Assert.Equal(BurnPhase.Verifying, verify10.Value.Phase);
        Assert.Equal(10, verify10.Value.MilestonePercent);
        Assert.Null(verify10.Value.EstimatedRemaining);
    }

    [Fact]
    public void Evaluate_UsesReportedRemainingWhenPositive_OtherwiseExtrapolatesAfterOneSecond()
    {
        var tracker = new ProgressMilestoneTracker();

        // Explicit positive reportedRemaining overrides extrapolation
        var reported = tracker.Evaluate(
            BurnPhase.Erasing,
            20,
            TimeSpan.FromSeconds(10),
            reportedRemaining: TimeSpan.FromSeconds(42));
        Assert.NotNull(reported);
        Assert.Equal(TimeSpan.FromSeconds(42), reported.Value.EstimatedRemaining);

        // Second milestone in same phase without reportedRemaining uses elapsed - phaseStartElapsed
        // phaseStartElapsed = 10s, elapsed = 20s -> phaseElapsed = 10s at 40% -> remaining = 10 * 60 / 40 = 15s
        var extrapolated = tracker.Evaluate(BurnPhase.Erasing, 40, TimeSpan.FromSeconds(20));
        Assert.NotNull(extrapolated);
        Assert.NotNull(extrapolated.Value.EstimatedRemaining);
        Assert.Equal(15.0, extrapolated.Value.EstimatedRemaining.Value.TotalSeconds, precision: 3);
    }
}
