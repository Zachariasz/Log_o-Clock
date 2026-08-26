using System.Windows;
using System.Windows.Threading;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class BreakReminderWindow : Window
{
    private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(3);
    private readonly BreakReminderPlacement _placement;
    private readonly DispatcherTimer _dismissTimer;

    public BreakReminderWindow(BreakReminderPlacement placement, string message)
    {
        _placement = placement;
        InitializeComponent();
        MessageText.Text = string.IsNullOrWhiteSpace(message) ? "Take a break!" : message;
        _dismissTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = AutoDismissDelay,
        };
        _dismissTimer.Tick += DismissTimer_Tick;
        Closed += (_, _) => _dismissTimer.Stop();
    }

    internal BreakReminderPlacement PlacementForPreview => _placement;
    internal string MessageForPreview => MessageText.Text;
    internal bool IsDismissTimerRunningForPreview => _dismissTimer.IsEnabled;

    private void BreakReminderWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var area = SystemParameters.WorkArea;
        if (_placement == BreakReminderPlacement.ScreenCenter)
        {
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + (area.Height - ActualHeight) / 2;
        }
        else
        {
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        }

        _dismissTimer.Start();
    }

    private void DismissTimer_Tick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }
}
