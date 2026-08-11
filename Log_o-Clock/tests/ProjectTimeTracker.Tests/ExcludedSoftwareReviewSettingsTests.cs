using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class ExcludedSoftwareReviewSettingsTests
{
    [Theory]
    [InlineData(null, 5)]
    [InlineData("", 5)]
    [InlineData("invalid", 5)]
    [InlineData("0", 5)]
    [InlineData("1441", 5)]
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    [InlineData("60", 60)]
    [InlineData("1440", 1440)]
    public void MinimumReviewMinutesUseSafeBounds(string? value, int expected)
    {
        Assert.Equal(
            expected,
            ExcludedSoftwareReviewSettings.ParseMinimumMinutes(value));
    }
}
