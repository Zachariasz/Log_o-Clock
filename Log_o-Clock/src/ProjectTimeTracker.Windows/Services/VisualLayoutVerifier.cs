using System.Windows;
using System.Windows.Media;

namespace ProjectTimeTracker.Windows.Services;

internal static class VisualLayoutVerifier
{
    private const double LayoutTolerance = 0.5d;

    public static void VerifyVisibleElementsFitWithin(FrameworkElement panel, string panelName)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentException.ThrowIfNullOrWhiteSpace(panelName);

        panel.UpdateLayout();
        if (panel.ActualWidth <= 0 || panel.ActualHeight <= 0)
        {
            throw new InvalidOperationException($"The {panelName} panel was not arranged.");
        }

        foreach (var element in FindVisualDescendants(panel).OfType<FrameworkElement>())
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                continue;
            }

            var bounds = element.TransformToAncestor(panel).TransformBounds(
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            if (bounds.Left < -LayoutTolerance ||
                bounds.Top < -LayoutTolerance ||
                bounds.Right > panel.ActualWidth + LayoutTolerance ||
                bounds.Bottom > panel.ActualHeight + LayoutTolerance)
            {
                var elementName = string.IsNullOrWhiteSpace(element.Name)
                    ? element.GetType().Name
                    : element.Name;
                throw new InvalidOperationException(
                    $"The visible {elementName} element does not fit within the {panelName} panel.");
            }
        }
    }

    private static IEnumerable<DependencyObject> FindVisualDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;

            foreach (var descendant in FindVisualDescendants(child))
            {
                yield return descendant;
            }
        }
    }
}
