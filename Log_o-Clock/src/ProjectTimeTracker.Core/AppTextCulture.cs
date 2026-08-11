using System.Globalization;

namespace ProjectTimeTracker.Core;

public static class AppTextCulture
{
    public const string Name = "en-GB";

    public static CultureInfo English { get; } = CultureInfo.GetCultureInfo(Name);

    public static string FormatLongDate(DateTime value) =>
        value.ToString("dddd, d MMMM yyyy", English);

    public static string FormatShortDate(DateTime value) =>
        value.ToString("dd.MM.yyyy", English);

    public static string FormatShortDate(DateTimeOffset value) =>
        value.ToString("dd.MM.yyyy", English);

    public static string FormatShortTime(DateTimeOffset value) =>
        value.ToString("HH:mm", English);
}
