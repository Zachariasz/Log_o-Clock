using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class BreakReminderSettingsTests
{
    [Theory]
    [InlineData(null, 120)]
    [InlineData("", 120)]
    [InlineData("0", 120)]
    [InlineData("1441", 120)]
    [InlineData("1", 1)]
    [InlineData("120", 120)]
    [InlineData("1440", 1440)]
    public void IntervalUsesDefaultForInvalidValues(string? stored, int expected)
    {
        Assert.Equal(expected, BreakReminderSettings.ParseIntervalMinutes(stored));
    }

    [Theory]
    [InlineData(null, BreakReminderPlacement.BottomRight)]
    [InlineData("", BreakReminderPlacement.BottomRight)]
    [InlineData("unknown", BreakReminderPlacement.BottomRight)]
    [InlineData("bottomright", BreakReminderPlacement.BottomRight)]
    [InlineData("ScreenCenter", BreakReminderPlacement.ScreenCenter)]
    public void PlacementUsesSafeDefaultAndAcceptsKnownValues(
        string? stored,
        BreakReminderPlacement expected)
    {
        Assert.Equal(expected, BreakReminderSettings.ParsePlacement(stored));
    }
}
