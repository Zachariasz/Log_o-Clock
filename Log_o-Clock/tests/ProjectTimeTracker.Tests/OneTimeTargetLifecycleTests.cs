using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class OneTimeTargetLifecycleTests
{
    [Fact]
    public void ProgressStartsWhenTheTargetIsCreatedAndHasNoCalendarDate()
    {
        var createdUtc = new DateTimeOffset(2026, 7, 28, 9, 30, 0, TimeSpan.Zero);
        var nowUtc = createdUtc.AddDays(3);
        var target = CreateTarget(createdUtc);

        var period = OneTimeTargetLifecycle.GetProgressPeriod(target, nowUtc);

        Assert.Equal(createdUtc, period.StartUtc);
        Assert.Equal(nowUtc, period.EndUtc);
    }

    [Fact]
    public void IncompleteTargetRemainsVisibleWithoutAnExpiryDate()
    {
        var target = CreateTarget(
            new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero));

        Assert.Null(OneTimeTargetLifecycle.ResolveCompletionUtc(
            target,
            completedSeconds: 3 * 3600,
            latestActivityUtc: new DateTimeOffset(2026, 8, 30, 16, 0, 0, TimeSpan.Zero),
            nowUtc: new DateTimeOffset(2026, 8, 30, 16, 0, 0, TimeSpan.Zero)));
        Assert.True(OneTimeTargetLifecycle.IsVisible(
            target,
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc));
    }

    [Fact]
    public void CompletedTargetRemainsVisibleThroughSundayAndDisappearsOnMonday()
    {
        var completedUtc = new DateTimeOffset(2026, 7, 29, 14, 0, 0, TimeSpan.Zero);
        var target = CreateTarget(completedUtc.AddDays(-10)) with { CompletedUtc = completedUtc };

        Assert.True(OneTimeTargetLifecycle.IsVisible(
            target,
            new DateTimeOffset(2026, 8, 2, 23, 59, 59, TimeSpan.Zero),
            TimeZoneInfo.Utc));
        Assert.False(OneTimeTargetLifecycle.IsVisible(
            target,
            new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc));
    }

    [Fact]
    public void FirstCompletionUsesLatestActivityAndKeepsThePersistedTimestampLater()
    {
        var latestActivityUtc = new DateTimeOffset(2026, 7, 30, 15, 0, 0, TimeSpan.Zero);
        var nowUtc = latestActivityUtc.AddMinutes(5);
        var target = CreateTarget(latestActivityUtc.AddDays(-1));

        var firstCompletion = OneTimeTargetLifecycle.ResolveCompletionUtc(
            target,
            completedSeconds: 4 * 3600,
            latestActivityUtc,
            nowUtc);
        var persistedTarget = target with { CompletedUtc = firstCompletion };
        var laterCompletion = OneTimeTargetLifecycle.ResolveCompletionUtc(
            persistedTarget,
            completedSeconds: 0,
            latestActivityUtc: nowUtc.AddDays(1),
            nowUtc: nowUtc.AddDays(1));

        Assert.Equal(latestActivityUtc, firstCompletion);
        Assert.Equal(firstCompletion, laterCompletion);
    }

    private static CustomTarget CreateTarget(DateTimeOffset createdUtc) =>
        new(
            Guid.NewGuid(),
            "One-time delivery",
            null,
            CustomTargetCadence.OneTime,
            4,
            createdUtc,
            createdUtc);
}
