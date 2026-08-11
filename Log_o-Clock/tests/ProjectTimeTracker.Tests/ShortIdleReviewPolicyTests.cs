using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class ShortIdleReviewPolicyTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(299, true)]
    [InlineData(300, false)]
    [InlineData(301, false)]
    public void OnlyPositiveIntervalsStrictlyBelowFiveMinutesAccumulate(
        int seconds,
        bool expected)
    {
        Assert.Equal(
            expected,
            ShortIdleReviewPolicy.IsAccumulatedInterval(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void RollingWindowKeepsOnlyThePortionWithinThePreviousFourHours()
    {
        var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

        Assert.True(ShortIdleReviewPolicy.TryClipToAccumulationWindow(
            now.AddHours(-4).AddMinutes(-1),
            now.AddHours(-4).AddMinutes(1),
            now,
            out var clippedStart,
            out var clippedEnd));
        Assert.Equal(now.AddHours(-4), clippedStart);
        Assert.Equal(now.AddHours(-4).AddMinutes(1), clippedEnd);

        Assert.False(ShortIdleReviewPolicy.TryClipToAccumulationWindow(
            now.AddHours(-4).AddMinutes(-3),
            now.AddHours(-4).AddMinutes(-1),
            now,
            out _,
            out _));
        Assert.True(ShortIdleReviewPolicy.TryClipToAccumulationWindow(
            now.AddMinutes(-2),
            now,
            now,
            out clippedStart,
            out clippedEnd));
        Assert.Equal(now.AddMinutes(-2), clippedStart);
        Assert.Equal(now, clippedEnd);
    }

    [Fact]
    public void DecliningFirstReviewSkipsSecondMultipleThenUsesEveryMultiple()
    {
        var multiplier = ShortIdleReviewPolicy.NextPromptMultiplier(1, removed: false);
        Assert.Equal(3, multiplier);
        Assert.False(ShortIdleReviewPolicy.ShouldPrompt(14 * 60, 5, multiplier));
        Assert.True(ShortIdleReviewPolicy.ShouldPrompt(15 * 60, 5, multiplier));

        multiplier = ShortIdleReviewPolicy.NextPromptMultiplier(multiplier, removed: false);
        Assert.Equal(4, multiplier);
        Assert.False(ShortIdleReviewPolicy.ShouldPrompt(19 * 60, 5, multiplier));
        Assert.True(ShortIdleReviewPolicy.ShouldPrompt(20 * 60, 5, multiplier));
    }

    [Fact]
    public void AcceptingReviewResetsScheduleToFirstThreshold()
    {
        Assert.Equal(1, ShortIdleReviewPolicy.NextPromptMultiplier(7, removed: true));
        Assert.True(ShortIdleReviewPolicy.ShouldPrompt(5 * 60, 5, 1));
    }
}
