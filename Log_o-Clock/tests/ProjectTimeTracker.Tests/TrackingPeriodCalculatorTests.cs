using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class TrackingPeriodCalculatorTests
{
    [Fact]
    public void DailyPeriodCoversTheCurrentCalendarDay()
    {
        var period = TrackingPeriodCalculator.CurrentDay(
            new DateTimeOffset(2026, 7, 20, 5, 59, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero), period.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero), period.EndUtc);
    }

    [Fact]
    public void WeeklyPeriodCoversMondayThroughSunday()
    {
        var sunday = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var period = TrackingPeriodCalculator.CurrentWeek(sunday, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero), period.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero), period.EndUtc);

        var monday = TrackingPeriodCalculator.CurrentWeek(
            new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero), monday.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero), monday.EndUtc);
    }

    [Fact]
    public void MonthlyPeriodCoversTheWholeCurrentCalendarMonth()
    {
        var period = TrackingPeriodCalculator.CurrentMonth(
            new DateTimeOffset(2026, 8, 1, 5, 59, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), period.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), period.EndUtc);
    }
}
