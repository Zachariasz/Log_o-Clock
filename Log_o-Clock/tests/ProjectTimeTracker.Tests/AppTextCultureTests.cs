using System.Globalization;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class AppTextCultureTests
{
    [Fact]
    public void InterfaceDatesAndTimesStayEnglishUnderPolishAmbientCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pl-PL");
            var date = new DateTime(2026, 7, 15);
            var time = new DateTimeOffset(2026, 7, 15, 13, 5, 0, TimeSpan.Zero);

            Assert.Equal("Wednesday, 15 July 2026", AppTextCulture.FormatLongDate(date));
            Assert.Equal("15.07.2026", AppTextCulture.FormatShortDate(date));
            Assert.Equal("13:05", AppTextCulture.FormatShortTime(time));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
