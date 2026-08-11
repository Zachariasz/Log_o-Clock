namespace ProjectTimeTracker.Core;

public static class RunningStartTimeText
{
    public static bool TryResolve(
        string? text,
        DateTimeOffset currentStartUtc,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        out DateTimeOffset resolvedStartUtc)
    {
        resolvedStartUtc = default;
        if (!TimeOfDayText.TryParse(text, out var timeOfDay))
        {
            return false;
        }

        currentStartUtc = currentStartUtc.ToUniversalTime();
        nowUtc = nowUtc.ToUniversalTime();
        var currentStartLocal = TimeZoneInfo.ConvertTime(currentStartUtc, timeZone);
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var localDateTime = DateTime.SpecifyKind(
            currentStartLocal.Date.Add(timeOfDay),
            DateTimeKind.Unspecified);

        if (currentStartLocal.Date == nowLocal.Date &&
            localDateTime > nowLocal.DateTime)
        {
            localDateTime = localDateTime.AddDays(-1);
        }

        if (timeZone.IsInvalidTime(localDateTime))
        {
            return false;
        }

        resolvedStartUtc = new DateTimeOffset(
                localDateTime,
                timeZone.GetUtcOffset(localDateTime))
            .ToUniversalTime();
        return resolvedStartUtc <= nowUtc;
    }
}
