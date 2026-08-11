using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows;

internal static class EnglishUiCulture
{
    private static int _isApplied;

    public static void Apply()
    {
        if (Interlocked.Exchange(ref _isApplied, 1) != 0)
        {
            return;
        }

        CultureInfo.CurrentUICulture = AppTextCulture.English;
        CultureInfo.DefaultThreadCurrentUICulture = AppTextCulture.English;
        var language = XmlLanguage.GetLanguage(AppTextCulture.English.IetfLanguageTag);
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(language));
    }
}
