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

    [Fact]
    public void EnabledMessagesDefaultToAllButPreserveAnExplicitlyEmptyList()
    {
        Assert.Equal(
            BreakReminderSettings.Messages.Select(message => message.Id).OrderBy(id => id),
            BreakReminderSettings.ParseEnabledMessageIds(null).OrderBy(id => id));
        Assert.Empty(BreakReminderSettings.ParseEnabledMessageIds("[]"));
    }

    [Fact]
    public void SelectionPrioritizesTheLeastUsedAvailableMessage()
    {
        var usage = new BreakReminderDailyUsage(
            new DateOnly(2026, 8, 26),
            new Dictionary<string, int>
            {
                ["bathroom"] = 3,
                ["coffee"] = 1,
            });
        var selected = BreakReminderSettings.SelectMessage(
            new HashSet<string>(["bathroom", "coffee"]),
            usage,
            LocalTime(9),
            new Random(1));

        Assert.Equal("coffee", selected?.Id);
    }

    [Theory]
    [InlineData("dinner", 11, false)]
    [InlineData("dinner", 12, true)]
    [InlineData("dinner", 17, true)]
    [InlineData("dinner", 18, false)]
    [InlineData("episode", 9, false)]
    [InlineData("episode", 10, true)]
    [InlineData("episode", 21, true)]
    [InlineData("episode", 22, false)]
    public void MessagesRespectTheirBuiltInLocalHourWindows(
        string messageId,
        int localHour,
        bool expected)
    {
        var message = Assert.Single(BreakReminderSettings.Messages.Where(item => item.Id == messageId));

        Assert.Equal(expected, message.IsAvailableAt(LocalTime(localHour)));
    }

    [Fact]
    public void DailyUsageResetsWhenTheLocalDateChanges()
    {
        var stored = BreakReminderSettings.SerializeDailyUsage(
            new BreakReminderDailyUsage(
                new DateOnly(2026, 8, 25),
                new Dictionary<string, int> { ["coffee"] = 2 }));

        var usage = BreakReminderSettings.ParseDailyUsage(stored, new DateOnly(2026, 8, 26));

        Assert.Empty(usage.Counts);
        Assert.Equal(new DateOnly(2026, 8, 26), usage.LocalDate);
    }

    private static DateTimeOffset LocalTime(int hour)
    {
        var local = new DateTime(2026, 1, 15, hour, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }
}
