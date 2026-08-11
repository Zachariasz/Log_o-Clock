using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class DateRangeTextTests
{
    [Fact]
    public void ParsesRequestedDateRangeFormat()
    {
        Assert.True(DateRangeText.TryParse("02.07.2026 - 15.07.2026", out var start, out var end));
        Assert.Equal(new DateTime(2026, 7, 2), start);
        Assert.Equal(new DateTime(2026, 7, 15), end);
    }

    [Fact]
    public void AcceptsCompactInputAndNormalizesDisplay()
    {
        Assert.True(DateRangeText.TryParse("2.7.2026 - 5.7.2026", out var start, out var end));
        Assert.Equal("02.07.2026 - 05.07.2026", DateRangeText.Format(start, end));
    }

    [Fact]
    public void ReversedInputBecomesAWorkingAscendingRange()
    {
        Assert.True(DateRangeText.TryParse("15.07.2026 - 02.07.2026", out var start, out var end));
        Assert.Equal(new DateTime(2026, 7, 2), start);
        Assert.Equal(new DateTime(2026, 7, 15), end);
    }

    [Theory]
    [InlineData("")]
    [InlineData("02/07/2026 - 15/07/2026")]
    [InlineData("31.02.2026 - 15.03.2026")]
    [InlineData("02.07.2026")]
    public void RejectsInvalidRanges(string text)
    {
        Assert.False(DateRangeText.TryParse(text, out _, out _));
    }
}
