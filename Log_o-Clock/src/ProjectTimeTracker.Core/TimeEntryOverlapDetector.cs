namespace ProjectTimeTracker.Core;

public static class TimeEntryOverlapDetector
{
    public static IReadOnlySet<Guid> FindOverlappingEntries(
        IEnumerable<TimeEntryView> entries,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var intervals = entries
            .Select(entry => new Interval(
                entry.Id,
                ToMinute(entry.StartUtc),
                ToMinute(entry.EndUtc ?? nowUtc)))
            .Where(interval => interval.EndMinute > interval.StartMinute)
            .OrderBy(interval => interval.StartMinute)
            .ThenBy(interval => interval.EndMinute)
            .ToArray();
        var overlappingIds = new HashSet<Guid>();

        for (var leftIndex = 0; leftIndex < intervals.Length; leftIndex++)
        {
            var left = intervals[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < intervals.Length; rightIndex++)
            {
                var right = intervals[rightIndex];
                if (right.StartMinute >= left.EndMinute)
                {
                    break;
                }

                if (right.EndMinute <= left.StartMinute)
                {
                    continue;
                }

                overlappingIds.Add(left.Id);
                overlappingIds.Add(right.Id);
            }
        }

        return overlappingIds;
    }

    private static long ToMinute(DateTimeOffset timestamp) =>
        timestamp.UtcTicks / TimeSpan.TicksPerMinute;

    private sealed record Interval(
        Guid Id,
        long StartMinute,
        long EndMinute);
}
