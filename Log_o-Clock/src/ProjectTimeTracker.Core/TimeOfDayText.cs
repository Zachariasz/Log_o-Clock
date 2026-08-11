using System.Globalization;

namespace ProjectTimeTracker.Core;

public static class TimeOfDayText
{
    public static bool TryParse(string? text, out TimeSpan timeOfDay)
    {
        timeOfDay = default;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.All(static character => character is >= '0' and <= '9'))
        {
            return TryParseCompactDigits(value, out timeOfDay);
        }

        if (!DateTime.TryParse(
                value,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsedTime))
        {
            return false;
        }

        timeOfDay = parsedTime.TimeOfDay;
        return true;
    }

    public static string Format(TimeSpan timeOfDay) =>
        $"{(int)timeOfDay.TotalHours:00}:{timeOfDay.Minutes:00}";

    private static bool TryParseCompactDigits(string value, out TimeSpan timeOfDay)
    {
        timeOfDay = default;
        var hours = 0;
        var minutes = 0;
        switch (value.Length)
        {
            case 1:
            case 2:
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out hours))
                {
                    return false;
                }

                break;
            case 3:
                hours = value[0] - '0';
                if (!int.TryParse(value.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out minutes))
                {
                    return false;
                }

                break;
            case 4:
                if (!int.TryParse(value.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out hours) ||
                    !int.TryParse(value.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out minutes))
                {
                    return false;
                }

                break;
            default:
                return false;
        }

        if (hours is < 0 or > 23 || minutes is < 0 or > 59)
        {
            return false;
        }

        timeOfDay = new TimeSpan(hours, minutes, 0);
        return true;
    }
}
