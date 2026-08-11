using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class RunningStartTimeTextTests
{
    [Fact]
    public void ResolvesEditedTimeOnTheCurrentStartDate()
    {
        var currentStart = new DateTimeOffset(2026, 7, 23, 10, 15, 42, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 7, 23, 14, 0, 0, TimeSpan.Zero);

        Assert.True(RunningStartTimeText.TryResolve(
            "1230",
            currentStart,
            now,
            TimeZoneInfo.Utc,
            out var resolved));
        Assert.Equal(
            new DateTimeOffset(2026, 7, 23, 12, 30, 0, TimeSpan.Zero),
            resolved);
    }

    [Fact]
    public void TreatsTimeLaterThanNowAsYesterdayForAStartFromToday()
    {
        var currentStart = new DateTimeOffset(2026, 7, 23, 0, 30, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 7, 23, 1, 0, 0, TimeSpan.Zero);

        Assert.True(RunningStartTimeText.TryResolve(
            "23:45",
            currentStart,
            now,
            TimeZoneInfo.Utc,
            out var resolved));
        Assert.Equal(
            new DateTimeOffset(2026, 7, 22, 23, 45, 0, TimeSpan.Zero),
            resolved);
    }

    [Fact]
    public void PreservesTheOriginalDateForALongRunningTimer()
    {
        var currentStart = new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 7, 23, 14, 0, 0, TimeSpan.Zero);

        Assert.True(RunningStartTimeText.TryResolve(
            "16",
            currentStart,
            now,
            TimeZoneInfo.Utc,
            out var resolved));
        Assert.Equal(
            new DateTimeOffset(2026, 7, 21, 16, 0, 0, TimeSpan.Zero),
            resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("25:00")]
    [InlineData("start")]
    public void RejectsInvalidStartTime(string text)
    {
        Assert.False(RunningStartTimeText.TryResolve(
            text,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow,
            TimeZoneInfo.Utc,
            out _));
    }
}
