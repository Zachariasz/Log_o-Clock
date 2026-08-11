using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ProjectTimeTracker.Windows.Services;

public enum WindowBackdropRole
{
    Auto,
    MainShell,
    Dialog,
    TransparentPopup,
}

public static class WindowBackdropService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int RoundedCorners = 2;
    private static bool _registered;

    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
        "Role",
        typeof(WindowBackdropRole),
        typeof(WindowBackdropService),
        new FrameworkPropertyMetadata(WindowBackdropRole.Auto));

    public static void SetRole(DependencyObject element, WindowBackdropRole value) => element.SetValue(RoleProperty, value);

    public static WindowBackdropRole GetRole(DependencyObject element) => (WindowBackdropRole)element.GetValue(RoleProperty);

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(ApplyToWindow));
    }

    private static void ApplyToWindow(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Window window)
        {
            return;
        }

        ApplyTypography(window);
        var role = GetRole(window);
        if (role == WindowBackdropRole.Auto)
        {
            role = window.AllowsTransparency
                ? WindowBackdropRole.TransparentPopup
                : window is MainWindow ? WindowBackdropRole.MainShell : WindowBackdropRole.Dialog;
        }

        if (role == WindowBackdropRole.TransparentPopup || window.AllowsTransparency)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var darkMode = 1;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
        var corners = RoundedCorners;
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));

        // Main and dialog surfaces stay opaque. DWM still supplies native outer rounding and shadow.
    }

    private static void ApplyTypography(Window? window)
    {
        if (window is null)
        {
            return;
        }

        if (Application.Current.TryFindResource("InkBrush") is Brush foreground)
        {
            window.Foreground = foreground;
        }

        window.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
