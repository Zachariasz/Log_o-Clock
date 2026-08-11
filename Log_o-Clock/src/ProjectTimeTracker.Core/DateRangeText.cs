using System.Globalization;
using System.Text.RegularExpressions;

namespace ProjectTimeTracker.Core;

public static class DateRangeText
{
    private static readonly Regex RangePattern = new(
        @"^\s*(?<start>\d{1,2}\.\d{1,2}\.\d{4})\s*[-–]\s*(?<end>\d{1,2}\.\d{1,2}\.\d{4})\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] AcceptedDateFormats = ["d.M.yyyy", "dd.MM.yyyy"];

    public static bool TryParse(string? text, out DateTime startDate, out DateTime endDate)
    {
        startDate = default;
        endDate = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = RangePattern.Match(text);
        if (!match.Success ||
            !DateTime.TryParseExact(
                match.Groups["start"].Value,
                AcceptedDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out startDate) ||
            !DateTime.TryParseExact(
                match.Groups["end"].Value,
                AcceptedDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out endDate))
        {
            startDate = default;
            endDate = default;
            return false;
        }

        startDate = startDate.Date;
        endDate = endDate.Date;
        if (endDate < startDate)
        {
            (startDate, endDate) = (endDate, startDate);
        }

        return true;
    }

    public static string Format(DateTime startDate, DateTime endDate)
    {
        startDate = startDate.Date;
        endDate = endDate.Date;
        if (endDate < startDate)
        {
            (startDate, endDate) = (endDate, startDate);
        }

        return $"{startDate:dd.MM.yyyy} - {endDate:dd.MM.yyyy}";
    }
}
