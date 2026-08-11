using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class CalendarDateRangePresetsTests
{
    [Fact]
    public void ThisMonthUsesCompleteCalendarMonth()
    {
        var range = CalendarDateRangePresets.Resolve(
            new DateTime(2026, 2, 18),
            CalendarDateRangePreset.ThisMonth);

        Assert.Equal(new DateTime(2026, 2, 1), range.Start);
        Assert.Equal(new DateTime(2026, 2, 28), range.End);
    }

    [Fact]
    public void ThisWeekUsesMondayThroughSunday()
    {
        var range = CalendarDateRangePresets.Resolve(
            new DateTime(2026, 7, 16),
            CalendarDateRangePreset.ThisWeek);

        Assert.Equal(new DateTime(2026, 7, 13), range.Start);
        Assert.Equal(new DateTime(2026, 7, 19), range.End);
    }

    [Fact]
    public void TodayUsesSingleCalendarDay()
    {
        var range = CalendarDateRangePresets.Resolve(
            new DateTime(2026, 7, 16, 22, 15, 0),
            CalendarDateRangePreset.Today);

        Assert.Equal(new DateTime(2026, 7, 16), range.Start);
        Assert.Equal(range.Start, range.End);
    }
}
