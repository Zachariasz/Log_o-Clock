using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Controls;

public partial class DateRangePicker : UserControl
{
    public static readonly DependencyProperty StartDateProperty = DependencyProperty.Register(
        nameof(StartDate),
        typeof(DateTime?),
        typeof(DateRangePicker),
        new PropertyMetadata(null, OnDatePropertyChanged));

    public static readonly DependencyProperty EndDateProperty = DependencyProperty.Register(
        nameof(EndDate),
        typeof(DateTime?),
        typeof(DateRangePicker),
        new PropertyMetadata(null, OnDatePropertyChanged));

    private DateTime? _pendingStartDate;
    private bool _settingRange;
    private bool _updatingCalendar;
    private bool _updatingText;

    public DateRangePicker()
    {
        InitializeComponent();
    }

    public event EventHandler<DateRangeChangedEventArgs>? RangeChanged;

    public DateTime? StartDate
    {
        get => (DateTime?)GetValue(StartDateProperty);
        set => SetValue(StartDateProperty, value?.Date);
    }

    public DateTime? EndDate
    {
        get => (DateTime?)GetValue(EndDateProperty);
        set => SetValue(EndDateProperty, value?.Date);
    }

    public bool IsCalendarOpen
    {
        get => RangePopup.IsOpen;
        set => RangePopup.IsOpen = value;
    }

    public void SetRange(DateTime startDate, DateTime endDate, bool notify = true)
    {
        startDate = startDate.Date;
        endDate = endDate.Date;
        if (endDate < startDate)
        {
            (startDate, endDate) = (endDate, startDate);
        }

        var changed = StartDate?.Date != startDate || EndDate?.Date != endDate;
        _settingRange = true;
        try
        {
            SetCurrentValue(StartDateProperty, startDate);
            SetCurrentValue(EndDateProperty, endDate);
        }
        finally
        {
            _settingRange = false;
        }

        _pendingStartDate = null;
        SynchronizeVisuals();
        if (changed && notify)
        {
            RangeChanged?.Invoke(this, new DateRangeChangedEventArgs(startDate, endDate));
        }
    }

    internal bool SetTextForPreview(string text)
    {
        RangeTextBox.Text = text;
        return TryCommitText(notify: false);
    }

    internal string TextForPreview => RangeTextBox.Text;
    internal int SelectedDateCountForPreview => RangeCalendar.SelectedDates.Count;
    internal Calendar CalendarForPreview => RangeCalendar;

    internal void SelectCalendarDateForPreview(DateTime date) =>
        ProcessSelectedDates([date.Date], notify: false);

    internal void SelectCalendarRangeForPreview(DateTime startDate, DateTime endDate)
    {
        startDate = startDate.Date;
        endDate = endDate.Date;
        if (endDate < startDate)
        {
            (startDate, endDate) = (endDate, startDate);
        }

        ProcessSelectedDates(
            Enumerable.Range(0, (endDate - startDate).Days + 1)
                .Select(offset => startDate.AddDays(offset))
                .ToArray(),
            notify: false);
    }

    private static void OnDatePropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        _ = args;
        var picker = (DateRangePicker)dependencyObject;
        if (!picker._settingRange)
        {
            picker.SynchronizeVisuals();
        }
    }

    private void CalendarButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        RangePopup.IsOpen = !RangePopup.IsOpen;
    }

    private void RangePopup_Opened(object sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _pendingStartDate = null;
        ClearValidationError();
        SynchronizeVisuals();
        RangeCalendar.Focus();
    }

    private void RangePopup_Closed(object sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (_pendingStartDate is not null)
        {
            _pendingStartDate = null;
            SynchronizeVisuals();
        }
    }

    private void RangeCalendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_updatingCalendar)
        {
            return;
        }

        ProcessSelectedDates(RangeCalendar.SelectedDates.Select(date => date.Date).ToArray(), notify: true);
    }

    private void ProcessSelectedDates(IReadOnlyCollection<DateTime> selectedDates, bool notify)
    {
        if (selectedDates.Count == 0)
        {
            return;
        }

        var orderedDates = selectedDates
            .Select(date => date.Date)
            .OrderBy(date => date)
            .ToArray();
        if (orderedDates.Length > 1)
        {
            _pendingStartDate = null;
            SetRange(orderedDates[0], orderedDates[^1], notify);
            return;
        }

        var clickedDate = orderedDates[0];
        if (_pendingStartDate is null)
        {
            SetRange(clickedDate, clickedDate, notify);
            _pendingStartDate = clickedDate;
            return;
        }

        var startDate = _pendingStartDate.Value;
        _pendingStartDate = null;
        SetRange(startDate, clickedDate, notify);
    }

    private void RangeTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (TryCommitText())
            {
                Keyboard.ClearFocus();
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _pendingStartDate = null;
            ClearValidationError();
            SynchronizeVisuals();
            RangePopup.IsOpen = false;
        }
    }

    private void RangeTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_updatingText && !string.Equals(RangeTextBox.Text, GetFormattedRange(), StringComparison.Ordinal))
        {
            _ = TryCommitText();
        }
    }

    private void RangeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_updatingText)
        {
            ClearValidationError();
        }
    }

    private bool TryCommitText(bool notify = true)
    {
        if (!DateRangeText.TryParse(RangeTextBox.Text, out var startDate, out var endDate))
        {
            ShowValidationError();
            return false;
        }

        ClearValidationError();
        SetRange(startDate, endDate, notify);
        RangePopup.IsOpen = false;
        return true;
    }

    private void SynchronizeVisuals()
    {
        SetText(GetFormattedRange());
        ApplyCalendarSelection(StartDate, EndDate);
    }

    private string GetFormattedRange() =>
        StartDate is { } startDate && EndDate is { } endDate
            ? DateRangeText.Format(startDate, endDate)
            : string.Empty;

    private void SetText(string text)
    {
        _updatingText = true;
        try
        {
            RangeTextBox.Text = text;
            RangeTextBox.CaretIndex = RangeTextBox.Text.Length;
        }
        finally
        {
            _updatingText = false;
        }
    }

    private void ApplyCalendarSelection(DateTime? startDate, DateTime? endDate)
    {
        _updatingCalendar = true;
        try
        {
            RangeCalendar.SelectedDates.Clear();
            if (startDate is not { } start)
            {
                return;
            }

            start = start.Date;
            RangeCalendar.DisplayDate = start;
            if (endDate is { } end)
            {
                end = end.Date;
                if (end < start)
                {
                    (start, end) = (end, start);
                }

                RangeCalendar.SelectedDates.AddRange(start, end);
            }
            else
            {
                RangeCalendar.SelectedDates.Add(start);
            }
        }
        finally
        {
            _updatingCalendar = false;
        }
    }

    private void ShowValidationError()
    {
        RangeTextBox.BorderBrush = TryFindResource("DangerBrush") as Brush ?? Brushes.IndianRed;
        RangeTextBox.ToolTip = "Use DD.MM.YYYY - DD.MM.YYYY and enter valid calendar dates.";
    }

    private void ClearValidationError()
    {
        RangeTextBox.ClearValue(Control.BorderBrushProperty);
        RangeTextBox.ToolTip = null;
    }

}

public sealed class DateRangeChangedEventArgs(DateTime startDate, DateTime endDate) : EventArgs
{
    public DateTime StartDate { get; } = startDate.Date;
    public DateTime EndDate { get; } = endDate.Date;
}
