using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class EntryEditorWindow : Window
{
    private readonly ITrackerStore _store;
    private readonly TimeEntryView? _existing;
    private readonly DateTime? _initialLocalDate;
    private TextBox? _taskEditor;
    private ListCollectionView? _taskSearchView;
    private string _taskSearchText = string.Empty;
    private bool _updatingTaskSearch;
    private bool _loading;

    private sealed class TaskSearchComparer(string query) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not SavedTask left || y is not SavedTask right)
            {
                return 0;
            }

            var leftIndex = string.IsNullOrEmpty(query)
                ? 0
                : left.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            var rightIndex = string.IsNullOrEmpty(query)
                ? 0
                : right.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            var byMatchPosition = leftIndex.CompareTo(rightIndex);
            return byMatchPosition != 0
                ? byMatchPosition
                : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        }
    }

    public EntryEditorWindow(
        ITrackerStore store,
        TimeEntryView? existing = null,
        DateTime? initialLocalDate = null)
    {
        InitializeComponent();
        _store = store;
        _existing = existing;
        _initialLocalDate = existing is null ? initialLocalDate?.Date : null;
        HeadingText.Text = existing is null ? "Add time entry" : "Edit time entry";
        AddHandler(
            Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(EditableField_GotKeyboardFocus),
            handledEventsToo: true);
        AddHandler(
            Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(EditableField_PreviewMouseLeftButtonDown),
            handledEventsToo: true);
        SourceInitialized += FitToAvailableWorkArea;
        Loaded += OnLoaded;
    }

    public EntryEditResult? Result { get; private set; }

    private void FitToAvailableWorkArea(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        const double edgeAllowance = 48;
        var availableHeight = Math.Max(360, SystemParameters.WorkArea.Height - edgeAllowance);
        MinHeight = Math.Min(MinHeight, availableHeight);
        MaxHeight = availableHeight;
        Height = Math.Min(Height, availableHeight);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _loading = true;
        var projects = await _store.GetProjectOptionsAsync();
        ProjectCombo.ItemsSource = projects;
        ProjectCombo.SelectedValue = _existing?.ProjectId ?? projects.FirstOrDefault()?.ProjectId;
        if (ProjectCombo.SelectedValue is Guid projectId)
        {
            DescriptionText.SetTagDefinitions(await _store.GetTagsAsync(projectId));
        }
        await ReloadTasksAsync(_existing?.TaskId);
        InitializeTaskSearch();

        var defaultStart = DateTimeOffset.Now.AddHours(-1);
        var defaultEnd = DateTimeOffset.Now;
        var startLocal = _existing?.StartUtc.ToLocalTime() ?? defaultStart;
        var endLocal = _existing?.EndUtc?.ToLocalTime() ?? defaultEnd;
        StartDatePicker.SelectedDate = _initialLocalDate ?? startLocal.Date;
        StartTimeText.Text = AppTextCulture.FormatShortTime(startLocal);
        EndDatePicker.SelectedDate = _initialLocalDate ?? endLocal.Date;
        EndTimeText.Text = AppTextCulture.FormatShortTime(endLocal);
        DescriptionText.Text = _existing?.Description ?? string.Empty;
        PaidCheck.IsChecked = _existing?.IsPaid == true;
        SoftwarePanel.Visibility = string.IsNullOrWhiteSpace(_existing?.SoftwareLabels)
            ? Visibility.Collapsed
            : Visibility.Visible;
        SoftwareText.Text = _existing?.SoftwareLabels ?? string.Empty;
        IdleTimePanel.Visibility = _existing is null ? Visibility.Collapsed : Visibility.Visible;
        IdleTimeText.Text = FormatDuration(_existing?.ExcludedSeconds ?? 0);
        _loading = false;
    }

    private async void ProjectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loading)
        {
            await ReloadTasksAsync(null);
            if (ProjectCombo.SelectedValue is Guid projectId)
            {
                DescriptionText.SetTagDefinitions(await _store.GetTagsAsync(projectId));
            }
        }
    }

    private void TaskCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_updatingTaskSearch)
        {
            return;
        }

        if (TaskCombo.SelectedItem is SavedTask selectedTask)
        {
            _updatingTaskSearch = true;
            try
            {
                TaskCombo.Text = selectedTask.Name;
                TaskCombo.IsDropDownOpen = false;
                if (_taskEditor is not null)
                {
                    _taskEditor.CaretIndex = _taskEditor.Text.Length;
                    _taskEditor.SelectionLength = 0;
                }
            }
            finally
            {
                _updatingTaskSearch = false;
            }
        }
    }

    private void TaskCombo_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        TaskCombo.IsDropDownOpen = false;
        SaveEntryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private void StartDatePicker_SelectedDateChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loading ||
            _existing is not null ||
            StartDatePicker.SelectedDate is not { } startDate)
        {
            return;
        }

        EndDatePicker.SelectedDate = startDate.Date;
    }

    private void TimeText_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _ = e;
        if (sender is TextBox timeText &&
            TimeOfDayText.TryParse(timeText.Text, out var timeOfDay))
        {
            timeText.Text = TimeOfDayText.Format(timeOfDay);
        }

        if (ReferenceEquals(sender, EndTimeText))
        {
            TryAdvanceEndDateForOvernightEntry();
        }
    }

    private static void EditableField_GotKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        _ = sender;
        if (FindEditableTextBox(e.NewFocus as DependencyObject) is { } textBox)
        {
            SelectAll(textBox);
        }
    }

    private static void EditableField_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _ = sender;
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var textBox = FindEditableTextBox(e.OriginalSource as DependencyObject);
        if (textBox is null || textBox.IsKeyboardFocusWithin)
        {
            return;
        }

        e.Handled = true;
        Keyboard.Focus(textBox);
        SelectAll(textBox);
    }

    private static TextBoxBase? FindEditableTextBox(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBoxBase textBox)
            {
                return textBox;
            }

            source = source switch
            {
                Visual or Visual3D => VisualTreeHelper.GetParent(source),
                FrameworkContentElement content => content.Parent,
                _ => LogicalTreeHelper.GetParent(source),
            };
        }

        return null;
    }

    private static void SelectAll(TextBoxBase textBox)
    {
        switch (textBox)
        {
            case TextBox plainText:
                plainText.SelectAll();
                break;
            case RichTextBox richText:
                richText.SelectAll();
                break;
        }
    }

    private async Task ReloadTasksAsync(Guid? selectedTaskId)
    {
        if (ProjectCombo.SelectedValue is not Guid projectId)
        {
            _taskSearchView = null;
            TaskCombo.ItemsSource = null;
            return;
        }

        var tasks = await _store.GetTasksAsync(projectId);
        _updatingTaskSearch = true;
        try
        {
            _taskSearchView = new ListCollectionView(tasks.ToList());
            TaskCombo.ItemsSource = _taskSearchView;
            TaskCombo.SelectedValue = selectedTaskId;
            if (selectedTaskId is null)
            {
                TaskCombo.SelectedIndex = -1;
                TaskCombo.Text = string.Empty;
            }

            ApplyTaskSearch(TaskCombo.Text, openDropDown: false);
        }
        finally
        {
            _updatingTaskSearch = false;
        }
    }

    private void InitializeTaskSearch()
    {
        TaskCombo.ApplyTemplate();
        var editor = TaskCombo.Template.FindName(
            "PART_EditableTextBox",
            TaskCombo) as TextBox;
        if (editor is null || ReferenceEquals(editor, _taskEditor))
        {
            return;
        }

        if (_taskEditor is not null)
        {
            _taskEditor.TextChanged -= TaskEditor_TextChanged;
        }

        _taskEditor = editor;
        _taskEditor.TextChanged += TaskEditor_TextChanged;
    }

    private void TaskEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_updatingTaskSearch || _taskSearchView is null)
        {
            return;
        }

        var typedText = _taskEditor?.Text ?? TaskCombo.Text;
        var selectedTaskMatches =
            TaskCombo.SelectedItem is SavedTask selectedTask &&
            string.Equals(
                typedText?.Trim(),
                selectedTask.Name,
                StringComparison.OrdinalIgnoreCase);
        if (!selectedTaskMatches &&
            TaskCombo.SelectedItem is SavedTask &&
            _taskEditor is { } editor)
        {
            var pendingText = editor.Text;
            var selectionStart = editor.SelectionStart;
            var selectionLength = editor.SelectionLength;
            var restoreKeyboardFocus = editor.IsKeyboardFocusWithin;
            _updatingTaskSearch = true;
            try
            {
                TaskCombo.SelectedIndex = -1;
                if (!string.Equals(editor.Text, pendingText, StringComparison.Ordinal))
                {
                    editor.Text = pendingText;
                }

                var safeStart = Math.Clamp(selectionStart, 0, editor.Text.Length);
                editor.Select(
                    safeStart,
                    Math.Clamp(
                        selectionLength,
                        0,
                        editor.Text.Length - safeStart));
                if (restoreKeyboardFocus)
                {
                    _ = Keyboard.Focus(editor);
                }
            }
            finally
            {
                _updatingTaskSearch = false;
            }
        }

        ApplyTaskSearch(
            typedText,
            openDropDown:
                !selectedTaskMatches &&
                _taskEditor?.IsKeyboardFocusWithin == true);
    }

    private void ApplyTaskSearch(string? text, bool openDropDown)
    {
        if (_taskSearchView is null)
        {
            return;
        }

        _taskSearchText = text?.Trim() ?? string.Empty;
        _taskSearchView.Filter = item =>
            item is SavedTask task &&
            (string.IsNullOrEmpty(_taskSearchText) ||
             task.Name.Contains(
                 _taskSearchText,
                 StringComparison.OrdinalIgnoreCase));
        _taskSearchView.CustomSort = new TaskSearchComparer(_taskSearchText);
        _taskSearchView.Refresh();

        if (openDropDown &&
            !string.IsNullOrEmpty(_taskSearchText) &&
            _taskSearchView.Count > 0)
        {
            OpenTaskDropDownWithoutSelectingText(_taskSearchText);
        }
        else if (_taskSearchView.Count == 0 ||
                 string.IsNullOrEmpty(_taskSearchText))
        {
            TaskCombo.IsDropDownOpen = false;
        }
    }

    private void OpenTaskDropDownWithoutSelectingText(
        string expectedSearchText)
    {
        var editor = _taskEditor;
        if (editor is null)
        {
            TaskCombo.IsDropDownOpen = true;
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (!editor.IsKeyboardFocusWithin ||
                    !string.Equals(
                        editor.Text.Trim(),
                        expectedSearchText,
                        StringComparison.Ordinal))
                {
                    if (!string.Equals(
                            editor.Text.Trim(),
                            expectedSearchText,
                            StringComparison.Ordinal) ||
                        Keyboard.Focus(editor) is null)
                    {
                        return;
                    }
                }

                var expectedText = editor.Text;
                var selectionStart = editor.SelectionStart;
                var selectionLength = editor.SelectionLength;
                if (expectedText.Length > 0 &&
                    selectionStart == 0 &&
                    selectionLength == expectedText.Length)
                {
                    selectionStart = expectedText.Length;
                    selectionLength = 0;
                }

                TaskCombo.IsDropDownOpen = true;
                _ = Keyboard.Focus(editor);
                var safeStart = Math.Clamp(
                    selectionStart,
                    0,
                    editor.Text.Length);
                editor.Select(
                    safeStart,
                    Math.Clamp(
                        selectionLength,
                        0,
                        editor.Text.Length - safeStart));

                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.ContextIdle,
                    () =>
                    {
                        if (!string.Equals(
                                editor.Text,
                                expectedText,
                                StringComparison.Ordinal) ||
                            selectionLength >= expectedText.Length ||
                            editor.SelectionLength != editor.Text.Length)
                        {
                            return;
                        }

                        var restoredStart = Math.Clamp(
                            selectionStart,
                            0,
                            editor.Text.Length);
                        editor.Select(
                            restoredStart,
                            Math.Clamp(
                                selectionLength,
                                0,
                                editor.Text.Length - restoredStart));
                    });
            });
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        var saveButton = sender as Button;
        if (saveButton is not null)
        {
            saveButton.IsEnabled = false;
        }

        try
        {
            await SubmitAsync(closeDialog: true);
        }
        catch (Exception exception)
        {
            ValidationText.Text = $"Could not save the task: {exception.Message}";
        }
        finally
        {
            if (saveButton is not null && IsLoaded)
            {
                saveButton.IsEnabled = true;
            }
        }
    }

    private async Task SubmitAsync(bool closeDialog)
    {
        if (ProjectCombo.SelectedValue is not Guid projectId)
        {
            ValidationText.Text = "Choose a project.";
            return;
        }

        TryAdvanceEndDateForOvernightEntry();
        if (!TryReadLocalDateTime(StartDatePicker, StartTimeText, out var startLocal) ||
            !TryReadLocalDateTime(EndDatePicker, EndTimeText, out var endLocal))
        {
            ValidationText.Text = "Choose valid local start and end dates and times.";
            return;
        }

        var start = new DateTimeOffset(startLocal).ToUniversalTime();
        var end = new DateTimeOffset(endLocal).ToUniversalTime();
        if (end <= start)
        {
            ValidationText.Text = "End time must be after start time.";
            return;
        }

        var excludedSeconds = 0L;
        if (_existing is not null &&
            !TryReadDuration(IdleTimeText.Text, out excludedSeconds))
        {
            ValidationText.Text = "Enter subtracted idle time as HH:MM:SS.";
            return;
        }

        var grossSeconds = (long)(end - start).TotalSeconds;
        if (excludedSeconds > Math.Max(0, grossSeconds - 60))
        {
            ValidationText.Text = "Subtracted idle time must leave at least one minute in the log.";
            return;
        }

        Guid? taskId = null;
        var taskName = TaskCombo.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(taskName))
        {
            taskId = TaskCombo.SelectedItem is SavedTask selectedTask &&
                     string.Equals(taskName, selectedTask.Name, StringComparison.OrdinalIgnoreCase)
                ? selectedTask.Id
                : (await _store.GetOrAddTaskAsync(projectId, taskName)).Id;
        }

        Result = new EntryEditResult(
            _existing?.Id,
            projectId,
            taskId,
            DescriptionText.Text,
            start,
            end,
            PaidCheck.IsChecked == true,
            excludedSeconds);
        if (closeDialog)
        {
            DialogResult = true;
        }
    }

    internal string ExcludedTimeForPreview => IdleTimeText.Text;

    internal DateTime? StartDateForPreview => StartDatePicker.SelectedDate;

    internal DateTime? EndDateForPreview => EndDatePicker.SelectedDate;

    internal void SetExcludedTimeForPreview(string value) => IdleTimeText.Text = value;

    internal void SetDatesForPreview(DateTime startDate, DateTime endDate)
    {
        StartDatePicker.SelectedDate = startDate.Date;
        EndDatePicker.SelectedDate = endDate.Date;
    }

    internal void SetStartDateForPreview(DateTime startDate) =>
        StartDatePicker.SelectedDate = startDate.Date;

    internal void SetEndDateForPreview(DateTime endDate) =>
        EndDatePicker.SelectedDate = endDate.Date;

    internal bool ApplyOvernightEndDateForPreview(string startTime, string endTime)
    {
        StartTimeText.Text = startTime;
        EndTimeText.Text = endTime;
        return TryAdvanceEndDateForOvernightEntry();
    }

    internal Task SubmitForPreviewAsync() => SubmitAsync(closeDialog: false);

    internal async Task SetManualValuesForPreviewAsync(
        Guid projectId,
        string taskName,
        string startTime,
        string endTime)
    {
        _loading = true;
        try
        {
            ProjectCombo.SelectedValue = projectId;
            await ReloadTasksAsync(null);
        }
        finally
        {
            _loading = false;
        }

        TaskCombo.SelectedIndex = -1;
        TypeTaskForPreview(taskName);
        StartDatePicker.SelectedDate = DateTime.Today;
        EndDatePicker.SelectedDate = DateTime.Today;
        StartTimeText.Text = startTime;
        EndTimeText.Text = endTime;
    }

    private void TypeTaskForPreview(string taskName)
    {
        TaskCombo.ApplyTemplate();
        var editor = TaskCombo.Template.FindName("PART_EditableTextBox", TaskCombo) as TextBox
            ?? throw new InvalidOperationException(
                "The Add Entry task field does not expose its editable textbox.");
        if (editor.IsReadOnly || editor.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException(
                "The Add Entry task textbox is not writable.");
        }

        Keyboard.Focus(editor);
        editor.SelectAll();
        editor.SelectedText = taskName;
        editor.CaretIndex = editor.Text.Length;
        if (!string.Equals(TaskCombo.Text, taskName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Text entered into the Add Entry task textbox did not reach the task chooser.");
        }
    }

    internal async Task VerifyTaskSearchForPreviewAsync(
        Guid expectedTaskId,
        string expectedTaskName)
    {
        InitializeTaskSearch();
        var editor = _taskEditor
            ?? throw new InvalidOperationException(
                "The Add Entry task-search textbox is unavailable.");
        var originalTopmost = Topmost;
        try
        {
            Topmost = true;
            Activate();
            Focus();
            _ = Keyboard.Focus(editor);
            editor.Text = "A";
            editor.CaretIndex = editor.Text.Length;
            editor.SelectionLength = 0;
            await Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ContextIdle);
            await Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ContextIdle);

            if (!string.Equals(editor.Text, "A", StringComparison.Ordinal) ||
                editor.SelectionLength != 0 ||
                editor.CaretIndex != 1 ||
                !TaskCombo.IsDropDownOpen ||
                _taskSearchView?.Cast<SavedTask>().Any(task =>
                    task.Id == expectedTaskId) != true)
            {
                throw new InvalidOperationException(
                    "Opening Add Entry task search after the first character changed the editor or omitted a matching task.");
            }

            editor.SelectedText = "n";
            editor.CaretIndex = editor.Text.Length;
            editor.SelectionLength = 0;
            await Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ContextIdle);
            if (!string.Equals(editor.Text, "An", StringComparison.Ordinal) ||
                editor.SelectionLength != 0 ||
                editor.CaretIndex != 2 ||
                !TaskCombo.IsDropDownOpen)
            {
                throw new InvalidOperationException(
                    "Typing a second Add Entry task character replaced the first character or closed matching suggestions. " +
                    $"Text='{editor.Text}', selection={editor.SelectionStart}:{editor.SelectionLength}, " +
                    $"caret={editor.CaretIndex}, open={TaskCombo.IsDropDownOpen}, " +
                    $"matches={_taskSearchView?.Count ?? -1}, search='{_taskSearchText}'.");
            }

            TaskCombo.SelectedValue = expectedTaskId;
            await Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.Input);
            if (TaskCombo.SelectedValue is not Guid selectedTaskId ||
                selectedTaskId != expectedTaskId ||
                !string.Equals(
                    TaskCombo.Text,
                    expectedTaskName,
                    StringComparison.Ordinal) ||
                TaskCombo.IsDropDownOpen)
            {
                throw new InvalidOperationException(
                    "Choosing an Add Entry task suggestion did not replace the search text.");
            }
        }
        finally
        {
            TaskCombo.IsDropDownOpen = false;
            Topmost = originalTopmost;
        }
    }

    internal bool IsReadyForSelectionPreview =>
        !_loading &&
        StartDatePicker.SelectedDate is not null &&
        EndDatePicker.SelectedDate is not null &&
        !string.IsNullOrWhiteSpace(StartTimeText.Text) &&
        !string.IsNullOrWhiteSpace(EndTimeText.Text);

    internal void VerifyScrollableLayoutForPreview()
    {
        SoftwarePanel.Visibility = Visibility.Visible;
        SoftwareText.Text = string.Join(
            " · ",
            Enumerable.Repeat("Long software label used while tracking", 12));
        IdleTimePanel.Visibility = Visibility.Visible;
        Height = MinHeight;
        UpdateLayout();

        if (EntryFormScrollViewer.VerticalScrollBarVisibility != ScrollBarVisibility.Auto ||
            EntryFormScrollViewer.ComputedVerticalScrollBarVisibility != Visibility.Visible ||
            EntryFormScrollViewer.ScrollableHeight <= 0)
        {
            throw new InvalidOperationException(
                "The time-entry form did not become vertically scrollable when its content exceeded the dialog height.");
        }

        EntryFormScrollViewer.ScrollToEnd();
        UpdateLayout();
        if (Math.Abs(
                EntryFormScrollViewer.VerticalOffset -
                EntryFormScrollViewer.ScrollableHeight) > 0.5)
        {
            throw new InvalidOperationException(
                "The time-entry form could not scroll to its final fields.");
        }

        if (!SaveEntryButton.IsVisible || !SaveEntryButton.IsArrangeValid)
        {
            throw new InvalidOperationException(
                "The time-entry actions were not kept visible outside the scrolling form.");
        }
    }

    internal async Task VerifyEditableValuesSelectOnFocusForPreviewAsync()
    {
        Activate();
        StartDatePicker.ApplyTemplate();
        EndDatePicker.ApplyTemplate();
        var startDateText = StartDatePicker.Template.FindName(
                "PART_TextBox",
                StartDatePicker)
            as DatePickerTextBox
            ?? throw new InvalidOperationException(
                "The entry editor start-date text field was not available.");
        var endDateText = EndDatePicker.Template.FindName(
                "PART_TextBox",
                EndDatePicker)
            as DatePickerTextBox
            ?? throw new InvalidOperationException(
                "The entry editor end-date text field was not available.");

        await VerifyKeyboardSelectionAsync(startDateText, "start date");
        await VerifyKeyboardSelectionAsync(StartTimeText, "start time");
        await VerifyKeyboardSelectionAsync(endDateText, "end date");
        await VerifyKeyboardSelectionAsync(EndTimeText, "end time");

        Keyboard.Focus(StartTimeText);
        await Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.Input);
        var click = new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
            Source = EndTimeText,
        };
        EndTimeText.RaiseEvent(click);
        await Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.Input);
        if (!click.Handled || !HasFullSelection(EndTimeText))
        {
            throw new InvalidOperationException(
                "Clicking an unfocused entry value did not select its full contents.");
        }
    }

    internal async Task FocusEndTimeForSelectionPreviewAsync()
    {
        Activate();
        Keyboard.Focus(EndTimeText);
        await Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.Input);
    }

    private async Task VerifyKeyboardSelectionAsync(
        TextBox textBox,
        string fieldName)
    {
        Keyboard.Focus(ProjectCombo);
        await Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.Input);
        Keyboard.Focus(textBox);
        await Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.Input);
        if (!HasFullSelection(textBox))
        {
            throw new InvalidOperationException(
                $"Keyboard focus did not select the full {fieldName} value.");
        }
    }

    private static bool HasFullSelection(TextBox textBox) =>
        textBox.Text.Length > 0 &&
        textBox.SelectionStart == 0 &&
        textBox.SelectionLength == textBox.Text.Length;

    private static string FormatDuration(long seconds) =>
        $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}";

    private static bool TryReadDuration(string? text, out long seconds)
    {
        seconds = 0;
        var parts = text?.Trim().Split(':');
        if (parts is null ||
            parts.Length is < 2 or > 3 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            minutes is < 0 or > 59)
        {
            return false;
        }

        var remainingSeconds = 0;
        if (parts.Length == 3 &&
            (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out remainingSeconds) ||
             remainingSeconds is < 0 or > 59))
        {
            return false;
        }

        try
        {
            seconds = checked(hours * 3600 + minutes * 60L + remainingSeconds);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadLocalDateTime(DatePicker datePicker, TextBox timeText, out DateTime localDateTime)
    {
        localDateTime = default;
        if (datePicker.SelectedDate is not { } selectedDate ||
            !TryReadLocalTime(timeText.Text, out var timeOfDay))
        {
            return false;
        }

        localDateTime = DateTime.SpecifyKind(
            selectedDate.Date.Add(timeOfDay),
            DateTimeKind.Local);
        return true;
    }

    private static bool TryReadLocalTime(string? text, out TimeSpan timeOfDay)
    {
        timeOfDay = default;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return TimeOfDayText.TryParse(value, out timeOfDay);
    }

    private bool TryAdvanceEndDateForOvernightEntry()
    {
        if (_loading ||
            _existing is not null ||
            StartDatePicker.SelectedDate is not { } startDate ||
            EndDatePicker.SelectedDate is not { } endDate ||
            startDate.Date != endDate.Date ||
            !TryReadLocalTime(StartTimeText.Text, out var startTime) ||
            !TryReadLocalTime(EndTimeText.Text, out var endTime) ||
            endTime >= startTime)
        {
            return false;
        }

        EndDatePicker.SelectedDate = startDate.Date.AddDays(1);
        return true;
    }
}

public sealed record EntryEditResult(
    Guid? EntryId,
    Guid ProjectId,
    Guid? TaskId,
    string? Description,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool IsPaid,
    long ExcludedSeconds);
