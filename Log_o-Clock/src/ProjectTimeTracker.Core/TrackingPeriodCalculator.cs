namespace ProjectTimeTracker.Core;

public static class TrackingPeriodCalculator
{
    public static TrackingPeriod CurrentDay(DateTimeOffset nowUtc, TimeZoneInfo timeZone)
        => CurrentPeriod(nowUtc, timeZone, CalendarDateRangePreset.Today);

    public static TrackingPeriod CurrentWeek(DateTimeOffset nowUtc, TimeZoneInfo timeZone)
        => CurrentPeriod(nowUtc, timeZone, CalendarDateRangePreset.ThisWeek);

    public static TrackingPeriod CurrentMonth(DateTimeOffset nowUtc, TimeZoneInfo timeZone)
        => CurrentPeriod(nowUtc, timeZone, CalendarDateRangePreset.ThisMonth);

    public static TrackingPeriod DayContaining(DateTime localDate, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var day = localDate.Date;
        return new TrackingPeriod(ToUtc(day, timeZone), ToUtc(day.AddDays(1), timeZone));
    }

    public static TrackingPeriod WeekContaining(DateTime localDate, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var day = localDate.Date;
        var monday = day.AddDays(-((7 + (int)day.DayOfWeek - (int)DayOfWeek.Monday) % 7));
        return new TrackingPeriod(ToUtc(monday, timeZone), ToUtc(monday.AddDays(7), timeZone));
    }

    public static TrackingPeriod MonthContaining(DateTime localDate, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var month = new DateTime(localDate.Year, localDate.Month, 1);
        return new TrackingPeriod(ToUtc(month, timeZone), ToUtc(month.AddMonths(1), timeZone));
    }

    private static TrackingPeriod CurrentPeriod(
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        CalendarDateRangePreset preset)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var localDate = TimeZoneInfo.ConvertTime(nowUtc, timeZone).Date;
        var (startLocal, endLocalInclusive) = CalendarDateRangePresets.Resolve(localDate, preset);
        return new TrackingPeriod(
            ToUtc(startLocal, timeZone),
            ToUtc(endLocalInclusive.AddDays(1), timeZone));
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
    }
}
