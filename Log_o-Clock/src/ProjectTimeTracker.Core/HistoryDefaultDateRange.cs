namespace ProjectTimeTracker.Core;

public static class HistoryDefaultDateRange
{
    public static (DateTime Start, DateTime End) Resolve(
        DateTime today,
        DateTime? latestEntryLocalDate)
    {
        var currentMonth = CalendarDateRangePresets.Resolve(
            today,
            CalendarDateRangePreset.ThisMonth);
        if (latestEntryLocalDate is not { } latest || latest.Date >= currentMonth.Start)
        {
            return currentMonth;
        }

        return CalendarDateRangePresets.Resolve(
            latest,
            CalendarDateRangePreset.ThisMonth);
    }
}
