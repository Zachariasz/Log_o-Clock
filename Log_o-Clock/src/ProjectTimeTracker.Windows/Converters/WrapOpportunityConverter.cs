using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Converters;

public sealed class WrapOpportunityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        _ = targetType;
        _ = parameter;
        _ = culture;
        return TextWrapOpportunityFormatter.AddInvisibleBreaks(value?.ToString());
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        _ = value;
        _ = targetType;
        _ = parameter;
        _ = culture;
        return DependencyProperty.UnsetValue;
    }
}
