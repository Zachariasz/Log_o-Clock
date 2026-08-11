using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class TimeEntryOverlapDetectorTests
{
    [Fact]
    public void MarksBothEntriesButNotTouchingBoundaries()
    {
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var first = Entry(now.AddHours(-4), now.AddHours(-2));
        var nested = Entry(now.AddHours(-3), now.AddHours(-2.5));
        var touching = Entry(now.AddHours(-2), now.AddHours(-1));

        var overlaps = TimeEntryOverlapDetector.FindOverlappingEntries(
            [first, nested, touching],
            now);

        Assert.Contains(first.Id, overlaps);
        Assert.Contains(nested.Id, overlaps);
        Assert.DoesNotContain(touching.Id, overlaps);
    }

    [Fact]
    public void RunningEntryUsesCurrentTimeAsItsEnd()
    {
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var running = Entry(now.AddHours(-2), endUtc: null);
        var completed = Entry(now.AddHours(-1), now.AddMinutes(-30));

        var overlaps = TimeEntryOverlapDetector.FindOverlappingEntries(
            [running, completed],
            now);

        Assert.Contains(running.Id, overlaps);
        Assert.Contains(completed.Id, overlaps);
    }

    [Fact]
    public void HiddenSecondsWithinTheSameDisplayedBoundaryMinuteDoNotOverlap()
    {
        var boundary = new DateTimeOffset(2026, 7, 16, 12, 13, 0, TimeSpan.Zero);
        var first = Entry(boundary.AddHours(-1), boundary.AddSeconds(45));
        var next = Entry(boundary, boundary.AddHours(1));

        var overlaps = TimeEntryOverlapDetector.FindOverlappingEntries(
            [first, next],
            boundary.AddHours(2));

        Assert.Empty(overlaps);
    }

    [Fact]
    public void OverlapThatCrossesAVisibleMinuteBoundaryRemainsMarked()
    {
        var boundary = new DateTimeOffset(2026, 7, 16, 12, 13, 0, TimeSpan.Zero);
        var first = Entry(boundary.AddHours(-1), boundary.AddMinutes(1).AddSeconds(5));
        var next = Entry(boundary.AddSeconds(50), boundary.AddHours(1));

        var overlaps = TimeEntryOverlapDetector.FindOverlappingEntries(
            [first, next],
            boundary.AddHours(2));

        Assert.Contains(first.Id, overlaps);
        Assert.Contains(next.Id, overlaps);
    }

    private static TimeEntryView Entry(
        DateTimeOffset startUtc,
        DateTimeOffset? endUtc) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            "Client",
            "Project",
            null,
            null,
            startUtc,
            endUtc,
            0,
            false,
            TrackingSource.Manual);
}
