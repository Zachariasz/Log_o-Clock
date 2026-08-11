using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class TimeOfDayTextTests
{
    [Theory]
    [InlineData("13", 13, 0)]
    [InlineData("1312", 13, 12)]
    [InlineData("132", 1, 32)]
    [InlineData("1303", 13, 3)]
    [InlineData("034", 0, 34)]
    [InlineData("00:34", 0, 34)]
    public void ParsesHourAndCompactHourMinuteInput(string text, int expectedHours, int expectedMinutes)
    {
        Assert.True(TimeOfDayText.TryParse(text, out var time));
        Assert.Equal(new TimeSpan(expectedHours, expectedMinutes, 0), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2400")]
    [InlineData("1360")]
    [InlineData("999")]
    [InlineData("12345")]
    public void RejectsInvalidCompactTimeInput(string text) =>
        Assert.False(TimeOfDayText.TryParse(text, out _));

    [Fact]
    public void FormatsCompactTimeAsHourAndMinute() =>
        Assert.Equal("13:03", TimeOfDayText.Format(new TimeSpan(13, 3, 0)));
}
