using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ProjectTimeTracker.Windows.ViewModels;

namespace ProjectTimeTracker.Windows.Converters;

public sealed class HistoryGroupDurationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        _ = targetType;
        _ = parameter;
        _ = culture;

        if (value is not CollectionViewGroup group)
        {
            return DependencyProperty.UnsetValue;
        }

        var totalSeconds = group.Items
            .OfType<TimeEntryRow>()
            .Sum(row => row.Entry.NetDurationSeconds(row.NowUtc));

        return $"{totalSeconds / 3600:00}:{totalSeconds % 3600 / 60:00}:{totalSeconds % 60:00}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        _ = value;
        _ = targetType;
        _ = parameter;
        _ = culture;
        throw new NotSupportedException();
    }
}
