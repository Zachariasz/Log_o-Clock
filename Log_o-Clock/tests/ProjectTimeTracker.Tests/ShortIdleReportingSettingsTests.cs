using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class ShortIdleReportingSettingsTests
{
    [Theory]
    [InlineData(null, 60)]
    [InlineData("", 60)]
    [InlineData("0", 60)]
    [InlineData("61", 60)]
    [InlineData("1", 1)]
    [InlineData("45", 45)]
    [InlineData("60", 60)]
    public void MaximumMinutesAreLimitedToOneHour(string? value, int expected)
    {
        Assert.Equal(expected, ShortIdleReportingSettings.ParseMaximumMinutes(value));
    }
}
