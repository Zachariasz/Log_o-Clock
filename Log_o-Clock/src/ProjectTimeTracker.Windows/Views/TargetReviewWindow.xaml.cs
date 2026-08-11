using System.Windows;
using System.Windows.Input;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class TargetReviewWindow : Window
{
    public TargetReviewWindow(IReadOnlyList<TargetReviewItem> items)
    {
        InitializeComponent();
        ItemsList.ItemsSource = items
            .Select(item => new TargetReviewRow(item))
            .OrderBy(row => row.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Client, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void TargetReviewWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Left = SystemParameters.WorkArea.Right - ActualWidth - 16;
        Top = SystemParameters.WorkArea.Bottom - ActualHeight - 16;
    }

    private void TargetReviewWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }

    private sealed record TargetReviewRow(TargetReviewItem Item)
    {
        public string Client => Item.ClientName;
        public string Project => Item.ProjectName;
        public bool HasDebt => Item.TargetDebt is { OutstandingSeconds: > 0 };
        public string Progress => string.Join("   ", new[]
            {
                FormatProgress("W", Item.WeeklySeconds, Item.WeeklyTargetHours),
                FormatProgress("M", Item.MonthlySeconds, Item.MonthlyTargetHours),
            }
            .Where(value => !string.IsNullOrEmpty(value)));
        public string Debt => Item.TargetDebt is { OutstandingSeconds: > 0 } debt
            ? TargetDebtText.Format(debt.OutstandingSeconds)
            : string.Empty;

        private static string FormatProgress(string prefix, long seconds, double? targetHours) => targetHours is > 0
            ? $"{prefix} {FormatDuration(seconds)} / {targetHours.Value:0.##} h"
            : string.Empty;

        private static string FormatDuration(long seconds) =>
            $"{seconds / 3600:00}:{seconds % 3600 / 60:00}";

    }
}
