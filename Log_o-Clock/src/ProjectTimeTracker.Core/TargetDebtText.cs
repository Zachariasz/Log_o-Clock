namespace ProjectTimeTracker.Core;

public static class TargetDebtText
{
    public static string Format(long outstandingSeconds)
    {
        if (outstandingSeconds <= 0)
        {
            return string.Empty;
        }

        var totalMinutes = Math.Max(1, outstandingSeconds / 60);
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        if (hours == 0)
        {
            return $"+{minutes} min";
        }

        return minutes == 0
            ? $"+{hours}h"
            : $"+{hours}h {minutes} min";
    }
}
