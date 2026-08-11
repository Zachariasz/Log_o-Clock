namespace ProjectTimeTracker.Core;

public enum CalendarDateRangePreset
{
    ThisMonth,
    ThisWeek,
    Today,
}

public static class CalendarDateRangePresets
{
    public static (DateTime Start, DateTime End) Resolve(
        DateTime date,
        CalendarDateRangePreset preset)
    {
        date = date.Date;
        return preset switch
        {
            CalendarDateRangePreset.ThisMonth => GetMonth(date),
            CalendarDateRangePreset.ThisWeek => GetWeek(date),
            CalendarDateRangePreset.Today => (date, date),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
        };
    }

    private static (DateTime Start, DateTime End) GetMonth(DateTime date)
    {
        var start = new DateTime(date.Year, date.Month, 1);
        return (start, start.AddMonths(1).AddDays(-1));
    }

    private static (DateTime Start, DateTime End) GetWeek(DateTime date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        var start = date.AddDays(-daysSinceMonday);
        return (start, start.AddDays(6));
    }
}
