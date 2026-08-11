using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class SidebarTargetsPanelSettingsTests
{
    [Theory]
    [InlineData(null, 312)]
    [InlineData("", 312)]
    [InlineData("95", 312)]
    [InlineData("2001", 312)]
    [InlineData("96", 96)]
    [InlineData("420", 420)]
    [InlineData("2000", 2000)]
    public void HeightUsesDefaultForInvalidValues(string? stored, int expected)
    {
        Assert.Equal(expected, SidebarTargetsPanelSettings.ParseHeight(stored));
    }
}
