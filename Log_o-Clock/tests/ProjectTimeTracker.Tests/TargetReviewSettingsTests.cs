using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class TargetReviewSettingsTests
{
    [Theory]
    [InlineData(TargetReviewMonthWeek.First, 3)]
    [InlineData(TargetReviewMonthWeek.Second, 10)]
    [InlineData(TargetReviewMonthWeek.Penultimate, 24)]
    [InlineData(TargetReviewMonthWeek.Last, 31)]
    public void ScheduleMatchesTheChosenMondayOccurrenceInMonth(
        TargetReviewMonthWeek monthWeek,
        int day)
    {
        var schedule = new TargetReviewSchedule(true, DayOfWeek.Monday, monthWeek);

        Assert.True(schedule.IsDueOn(new DateTime(2026, 8, day)));
        Assert.False(schedule.IsDueOn(new DateTime(2026, 8, day == 31 ? 24 : day + 1)));
    }

    [Fact]
    public void PenultimateAndLastWorkForMonthsWithFourWeekdayOccurrences()
    {
        var penultimate = new TargetReviewSchedule(true, DayOfWeek.Monday, TargetReviewMonthWeek.Penultimate);
        var last = new TargetReviewSchedule(true, DayOfWeek.Monday, TargetReviewMonthWeek.Last);

        Assert.True(penultimate.IsDueOn(new DateTime(2026, 2, 16)));
        Assert.True(last.IsDueOn(new DateTime(2026, 2, 23)));
    }

    [Fact]
    public void ParsedScheduleUsesSafeDefaultsForInvalidStoredValues()
    {
        var schedule = TargetReviewSettings.Parse("true", "not-a-day", "unknown");

        Assert.True(schedule.Enabled);
        Assert.Equal(DayOfWeek.Monday, schedule.DayOfWeek);
        Assert.Equal(TargetReviewMonthWeek.Last, schedule.MonthWeek);
    }

    [Fact]
    public void DisabledScheduleNeverMatches()
    {
        var schedule = new TargetReviewSchedule(false, DayOfWeek.Friday, TargetReviewMonthWeek.Last);

        Assert.False(schedule.IsDueOn(new DateTime(2026, 7, 31)));
    }
}
