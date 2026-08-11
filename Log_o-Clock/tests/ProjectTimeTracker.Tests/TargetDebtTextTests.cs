using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class TargetDebtTextTests
{
    [Theory]
    [InlineData(5 * 3600, "+5h")]
    [InlineData(30 * 60, "+30 min")]
    [InlineData((3 * 60 + 49) * 60, "+3h 49 min")]
    [InlineData(59, "+1 min")]
    [InlineData(30 * 60 + 59, "+30 min")]
    public void FormatsPositiveDebtAsCompactSignedHoursAndMinutes(long seconds, string expected)
    {
        Assert.Equal(expected, TargetDebtText.Format(seconds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void HidesNonPositiveDebt(long seconds)
    {
        Assert.Equal(string.Empty, TargetDebtText.Format(seconds));
    }
}
