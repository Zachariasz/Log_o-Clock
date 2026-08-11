using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class RecentEntryResumeSettingsTests
{
    [Theory]
    [InlineData(null, 2)]
    [InlineData("", 2)]
    [InlineData("-1", 2)]
    [InlineData("1441", 2)]
    [InlineData("0", 0)]
    [InlineData("17", 17)]
    [InlineData("1440", 1440)]
    public void MaximumGapUsesDefaultForInvalidValues(string? stored, int expected)
    {
        Assert.Equal(
            expected,
            RecentEntryResumeSettings.ParseMaximumGapMinutes(stored));
    }
}
