using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class HistoryDefaultDateRangeTests
{
    [Fact]
    public void EmptyNewMonthFallsBackToLatestMonthWithHistory()
    {
        var range = HistoryDefaultDateRange.Resolve(
            new DateTime(2026, 8, 1),
            new DateTime(2026, 7, 31));

        Assert.Equal(new DateTime(2026, 7, 1), range.Start);
        Assert.Equal(new DateTime(2026, 7, 31), range.End);
    }

    [Fact]
    public void CurrentMonthHistoryKeepsCurrentMonthDefault()
    {
        var range = HistoryDefaultDateRange.Resolve(
            new DateTime(2026, 8, 14),
            new DateTime(2026, 8, 2));

        Assert.Equal(new DateTime(2026, 8, 1), range.Start);
        Assert.Equal(new DateTime(2026, 8, 31), range.End);
    }

    [Fact]
    public void NoHistoryKeepsCurrentMonthDefault()
    {
        var range = HistoryDefaultDateRange.Resolve(new DateTime(2026, 8, 1), null);

        Assert.Equal(new DateTime(2026, 8, 1), range.Start);
        Assert.Equal(new DateTime(2026, 8, 31), range.End);
    }
}
