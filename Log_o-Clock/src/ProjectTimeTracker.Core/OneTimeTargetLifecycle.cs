namespace ProjectTimeTracker.Core;

public static class OneTimeTargetLifecycle
{
    public static TrackingPeriod GetProgressPeriod(
        CustomTarget target,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Cadence != CustomTargetCadence.OneTime)
        {
            throw new ArgumentException("The target must be a one-time target.", nameof(target));
        }

        var startUtc = target.CreatedUtc.ToUniversalTime();
        var endUtc = nowUtc.ToUniversalTime();
        if (endUtc <= startUtc)
        {
            endUtc = startUtc.AddTicks(1);
        }

        return new TrackingPeriod(startUtc, endUtc);
    }

    public static DateTimeOffset? ResolveCompletionUtc(
        CustomTarget target,
        long completedSeconds,
        DateTimeOffset? latestActivityUtc,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Cadence != CustomTargetCadence.OneTime)
        {
            return null;
        }

        if (target.CompletedUtc is { } completedUtc)
        {
            return completedUtc.ToUniversalTime();
        }

        if (completedSeconds < target.TargetHours * 3600d)
        {
            return null;
        }

        return latestActivityUtc?.ToUniversalTime()
            ?? nowUtc.ToUniversalTime();
    }

    public static bool IsVisible(
        CustomTarget target,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (target.Cadence != CustomTargetCadence.OneTime || target.CompletedUtc is null)
        {
            return true;
        }

        var completedLocalDate = TimeZoneInfo.ConvertTime(target.CompletedUtc.Value, timeZone).Date;
        var completionWeek = TrackingPeriodCalculator.WeekContaining(completedLocalDate, timeZone);
        return nowUtc.ToUniversalTime() < completionWeek.EndUtc;
    }
}
