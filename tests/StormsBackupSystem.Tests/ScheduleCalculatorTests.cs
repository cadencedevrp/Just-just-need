using StormsBackupSystem.Core;
using Xunit;

namespace StormsBackupSystem.Tests;

public class ScheduleCalculatorTests
{
    private static readonly TimeSpan ThreeAm = new(3, 0, 0);

    [Fact]
    public void FirstRunIsNextOccurrenceOfTimeOfDay()
    {
        var now = new DateTime(2026, 11, 10, 14, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 11, 3, 0, 0), ScheduleCalculator.GetNextRun(null, false, 0, ThreeAm, 24, now));

        var early = new DateTime(2026, 11, 10, 1, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 10, 3, 0, 0), ScheduleCalculator.GetNextRun(null, false, 0, ThreeAm, 24, early));
    }

    [Fact]
    public void RunsEvery24HoursAfterLastBackup()
    {
        var last = new DateTime(2026, 11, 10, 3, 0, 5);
        var now = new DateTime(2026, 11, 10, 12, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 11, 3, 0, 0), ScheduleCalculator.GetNextRun(last, false, 0, ThreeAm, 24, now));
    }

    [Fact]
    public void MissedBackupIsDueImmediately()
    {
        var last = new DateTime(2026, 11, 7, 3, 0, 0);
        var now = new DateTime(2026, 11, 10, 12, 0, 0);
        var next = ScheduleCalculator.GetNextRun(last, false, 0, ThreeAm, 24, now);
        Assert.True(next <= now);
    }

    [Fact]
    public void ManualBackupPushesNextRunToFollowingSlot()
    {
        // manual backup at 02:00, scheduled slot at 03:00 -> next is still 03:00 the same day
        var last = new DateTime(2026, 11, 10, 2, 0, 0);
        var now = last.AddMinutes(1);
        Assert.Equal(new DateTime(2026, 11, 10, 3, 0, 0), ScheduleCalculator.GetNextRun(last, false, 0, ThreeAm, 24, now));
    }

    [Fact]
    public void ShorterIntervalsAreSupported()
    {
        var last = new DateTime(2026, 11, 10, 9, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 10, 15, 0, 0), ScheduleCalculator.GetNextRun(last, false, 0, ThreeAm, 6, last));
    }

    [Fact]
    public void FailureRetriesAfterAnHourThenGivesUpUntilNextSlot()
    {
        var last = new DateTime(2026, 11, 10, 3, 0, 0);
        Assert.Equal(last.AddHours(1), ScheduleCalculator.GetNextRun(last, true, 1, ThreeAm, 24, last));
        Assert.Equal(new DateTime(2026, 11, 11, 3, 0, 0), ScheduleCalculator.GetNextRun(last, true, ScheduleCalculator.MaxRetries + 1, ThreeAm, 24, last));
    }
}
