using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Views;

public partial class ReminderWindow : Window
{
    private static readonly TimeSpan ClickAwayDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MousePollInterval = TimeSpan.FromMilliseconds(25);

    private readonly DispatcherTimer _mousePollTimer;
    private readonly System.Diagnostics.Stopwatch _visibleStopwatch = new();
    private readonly ListCollectionView _taskSearchView;
    private readonly nint _targetWindowHandle;
    private System.Windows.Forms.MouseButtons _previousMouseButtons;
    private bool _clickAwayArmed;
    private bool _suppressClickAwayForTaskDropDown;
    private TextBox? _taskEditor;
    private string _taskSearchText = string.Empty;
    private bool _updatingTaskSearch;

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

    public ReminderWindow(
        string clientName,
        string projectName,
        string color,
        IReadOnlyList<SavedTask> projectTasks,
        IReadOnlyList<TagDefinition> correlatedTags,
        IReadOnlyList<TagDefinition> availableTags,
        bool isProjectSwitch = false,
        Guid? suggestedTaskId = null,
        nint targetWindowHandle = default)
    {
        InitializeComponent();
        _targetWindowHandle = targetWindowHandle;
        IsProjectSwitch = isProjectSwitch;
        if (isProjectSwitch)
        {
            PromptText.Text = "SWITCH TO THIS PROJECT?";
            StartButton.Content = "Switch project";
        }

        ClientText.Text = clientName;
        ProjectText.Text = projectName;
        _taskSearchView = new ListCollectionView(projectTasks.ToList());
        TaskCombo.ItemsSource = _taskSearchView;
        var suggestedTask = suggestedTaskId is { } taskId
            ? projectTasks.FirstOrDefault(task => task.Id == taskId)
            : null;
        if (suggestedTask is not null)
        {
            // Set both the selection and visible editor text. A ComboBox may
            // defer SelectedValue binding until its template is loaded, which
            // previously left a valid recognized task visually blank.
            TaskCombo.SelectedItem = suggestedTask;
            TaskCombo.SelectedValue = suggestedTask.Id;
            TaskCombo.Text = suggestedTask.Name;
        }
        else
        {
            // A project-only recognition must never borrow an old task value.
            TaskCombo.SelectedItem = null;
            TaskCombo.SelectedIndex = -1;
            TaskCombo.Text = string.Empty;
        }

        DescriptionText.SetTagDefinitions(availableTags);
        CorrelatedTagsList.ItemsSource = correlatedTags;
        CorrelatedTagsPanel.Visibility = correlatedTags.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (ColorConverter.ConvertFromString(color) is Color parsed)
        {
            StartButton.Background = new SolidColorBrush(parsed);
        }

        _mousePollTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = MousePollInterval,
        };
        _mousePollTimer.Tick += MousePollTimer_Tick;
        Loaded += ReminderWindow_Loaded;
        ContentRendered += ReminderWindow_ContentRendered;
        Closed += ReminderWindow_Closed;
    }

    public bool Started { get; private set; }
    public bool Snoozed { get; private set; }
    public bool IsProjectSwitch { get; }
    internal nint TargetWindowHandleForPreview => _targetWindowHandle;
    public string? TaskName
    {
        get
        {
            var text = _taskEditor?.Text ?? TaskCombo.Text;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
    }
    public Guid? SelectedTaskId =>
        TaskCombo.SelectedItem is SavedTask selectedTask &&
        string.Equals(TaskName, selectedTask.Name, StringComparison.OrdinalIgnoreCase)
            ? selectedTask.Id
            : null;
    public string? Description => string.IsNullOrWhiteSpace(DescriptionText.Text)
        ? null
        : DescriptionText.Text.Trim();
    public IReadOnlyList<string> SelectedTags => CorrelatedTagsList.SelectedItems
        .OfType<TagDefinition>()
        .Select(tag => tag.Name)
        .ToArray();

    internal void SelectTagsForPreview(params string[] tagNames)
    {
        var names = tagNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in CorrelatedTagsList.Items.OfType<TagDefinition>().Where(tag => names.Contains(tag.Name)))
        {
            CorrelatedTagsList.SelectedItems.Add(tag);
        }
    }

    internal void VerifyTagColorStatesForPreview(string tagName)
    {
        var tag = CorrelatedTagsList.Items
            .OfType<TagDefinition>()
            .Single(item => string.Equals(item.Name, tagName, StringComparison.OrdinalIgnoreCase));
        CorrelatedTagsList.UpdateLayout();
        var container = CorrelatedTagsList.ItemContainerGenerator.ContainerFromItem(tag) as ListBoxItem
            ?? throw new InvalidOperationException(
                "The correlated-tag chip was not generated.");
        container.ApplyTemplate();
        var chip = container.Template.FindName("Chip", container) as Border
            ?? throw new InvalidOperationException(
                "The correlated-tag chip surface is missing.");
        var chipText = container.Template.FindName("ChipText", container) as TextBlock
            ?? throw new InvalidOperationException(
                "The correlated-tag chip label is missing.");
        var expectedColor = ColorConverter.ConvertFromString(tag.Color) is Color parsed
            ? parsed
            : throw new InvalidOperationException("The correlated tag has an invalid color.");
        if (BrushColor(chip.Background) == expectedColor ||
            BrushColor(chip.BorderBrush) == expectedColor ||
            BrushColor(chipText.Foreground) == expectedColor)
        {
            throw new InvalidOperationException(
                "An unchecked correlated tag displayed its assigned color instead of gray.");
        }

        container.IsSelected = true;
        container.UpdateLayout();
        if (BrushColor(chip.Background) != expectedColor ||
            BrushColor(chip.BorderBrush) != expectedColor ||
            BrushColor(chipText.Foreground) != Colors.White)
        {
            throw new InvalidOperationException(
                "A checked correlated tag did not display its assigned color.");
        }
    }

    internal void SetDetailsForPreview(Guid? taskId, string? taskName, string? description)
    {
        TaskCombo.SelectedValue = taskId;
        if (taskId is null)
        {
            TaskCombo.SelectedIndex = -1;
            TaskCombo.Text = taskName ?? string.Empty;
        }

        DescriptionText.Text = description ?? string.Empty;
    }

    internal void SelectTaskForPreview(Guid taskId)
    {
        // The preview may deliberately seed arbitrary typed text, which filters
        // the selected task out of the editable ComboBox view. Clear that
        // transient search first so this exercises the same selection path a
        // user reaches after opening the task list.
        ApplyTaskSearch(string.Empty, openDropDown: false);
        TaskCombo.SelectedItem = _taskSearchView.Cast<SavedTask>()
            .FirstOrDefault(task => task.Id == taskId);
    }

    internal async Task VerifyTaskSearchForPreviewAsync(
        string query,
        IReadOnlyCollection<Guid> expectedTaskIds,
        Guid excludedTaskId)
    {
        if (query.Length < 2)
        {
            throw new ArgumentException(
                "The reminder task-search preview requires at least two characters.",
                nameof(query));
        }

        InitializeTaskSearch();
        Activate();
        if (TaskName is not null || TaskCombo.SelectedItem is not null)
        {
            throw new InvalidOperationException(
                "A project-only recognition reminder prefilled a task without an unambiguous match.");
        }

        if (_taskEditor is null || !_taskEditor.Focus())
        {
            throw new InvalidOperationException(
                "The reminder task search could not focus its editable field.");
        }

        _taskEditor.Text = query[..1];
        _taskEditor.CaretIndex = _taskEditor.Text.Length;
        _taskEditor.SelectionLength = 0;
        await Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ContextIdle);
        await Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ContextIdle);
        if (!string.Equals(_taskEditor.Text, query[..1], StringComparison.Ordinal) ||
            _taskEditor.SelectionLength != 0 ||
            _taskEditor.CaretIndex != 1 ||
            !TaskCombo.IsDropDownOpen)
        {
            throw new InvalidOperationException(
                "Opening reminder task suggestions after the first character selected or replaced the typed text.");
        }

        _taskEditor.SelectedText = query[1..];
        _taskEditor.CaretIndex = _taskEditor.Text.Length;
        _taskEditor.SelectionLength = 0;
        await Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ContextIdle);
        var visibleTasks = _taskSearchView.Cast<SavedTask>().ToArray();
        var expected = expectedTaskIds.ToHashSet();
        if (!TaskCombo.IsDropDownOpen ||
            !string.Equals(TaskCombo.Text, query, StringComparison.Ordinal) ||
            _taskEditor.SelectionLength != 0 ||
            _taskEditor.CaretIndex != query.Length ||
            visibleTasks.Length != expected.Count ||
            visibleTasks.Any(task => !expected.Contains(task.Id)) ||
            visibleTasks.Any(task => task.Id == excludedTaskId) ||
            visibleTasks.Zip(visibleTasks.Skip(1), (left, right) =>
                    left.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) <=
                    right.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase))
                .Any(isOrdered => !isOrdered))
        {
            throw new InvalidOperationException(
                "The reminder task field did not open and filter matching project tasks like the tracker bar.");
        }

        var chosenTask = visibleTasks[0];
        TaskCombo.SelectedItem = chosenTask;
        if (SelectedTaskId != chosenTask.Id ||
            !string.Equals(TaskName, chosenTask.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Choosing a filtered reminder task did not replace the search text with that task.");
        }

        const string newTaskName = "New reminder task";
        _taskEditor.Text = newTaskName;
        _taskEditor.CaretIndex = _taskEditor.Text.Length;
        if (TaskCombo.IsDropDownOpen ||
            _taskSearchView.Count != 0 ||
            !string.Equals(TaskName, newTaskName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The reminder task search did not preserve unmatched text as a new task name " +
                $"(open={TaskCombo.IsDropDownOpen}, count={_taskSearchView.Count}, " +
                $"task='{TaskName}', editor='{_taskEditor.Text}').");
        }
    }

    internal void TypeTaskSearchForPreview(string query)
    {
        InitializeTaskSearch();
        Activate();
        _ = _taskEditor?.Focus();
        if (_taskEditor is not null)
        {
            _taskEditor.Text = query;
            _taskEditor.CaretIndex = _taskEditor.Text.Length;
        }
    }

    internal void StartForPreview() =>
        StartButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    internal void SnoozeForPreview() =>
        GimmeBreakButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    internal void VerifySnoozeButtonForPreview()
    {
        if (!string.Equals(GimmeBreakButton.Content as string, "Gimme break!", StringComparison.Ordinal) ||
            !string.Equals(
                GimmeBreakButton.ToolTip as string,
                "Pause recognition reminders for 5 minutes.",
                StringComparison.Ordinal) ||
            ToolTipService.GetInitialShowDelay(GimmeBreakButton) != 2000)
        {
            throw new InvalidOperationException(
                "The recognition reminder snooze button is missing its five-minute action or delayed tooltip.");
        }

        SnoozeForPreview();
        if (!Snoozed || Started || IsVisible)
        {
            throw new InvalidOperationException(
                "The recognition reminder snooze action did not close the reminder without starting a timer.");
        }
    }

    internal bool IsClickAwayArmedForPreview => _clickAwayArmed;

    internal bool TryDismissForOutsideClickForPreview(Point screenPoint) =>
        TryDismissForOutsideClick(screenPoint);

    private void PlaceBottomRight()
    {
        var area = _targetWindowHandle == nint.Zero
            ? SystemParameters.WorkArea
            : ToWpfWorkArea(System.Windows.Forms.Screen.FromHandle(_targetWindowHandle).WorkingArea);
        Left = area.Right - ActualWidth - 18;
        Top = area.Bottom - ActualHeight - 18;
        Activate();
    }

    private static Rect ToWpfWorkArea(System.Drawing.Rectangle workingArea) =>
        new(workingArea.Left, workingArea.Top, workingArea.Width, workingArea.Height);

    private void ReminderWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        PlaceBottomRight();
        InitializeTaskSearch();
        ApplyTaskSearch(TaskCombo.Text, openDropDown: false);
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
        if (_updatingTaskSearch)
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

    private void ReminderWindow_ContentRendered(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _clickAwayArmed = false;
        _previousMouseButtons = System.Windows.Forms.Control.MouseButtons;
        _visibleStopwatch.Restart();
        _mousePollTimer.Start();
    }

    private void MousePollTimer_Tick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        var currentButtons = System.Windows.Forms.Control.MouseButtons;
        var newlyPressedButtons = currentButtons & ~_previousMouseButtons;
        _previousMouseButtons = currentButtons;
        if (_suppressClickAwayForTaskDropDown)
        {
            if (!TaskCombo.IsDropDownOpen &&
                currentButtons == System.Windows.Forms.MouseButtons.None)
            {
                _suppressClickAwayForTaskDropDown = false;
            }

            return;
        }

        if (!_clickAwayArmed)
        {
            if (_visibleStopwatch.Elapsed < ClickAwayDelay)
            {
                return;
            }

            _clickAwayArmed = true;
        }

        if (newlyPressedButtons == System.Windows.Forms.MouseButtons.None)
        {
            return;
        }

        var cursor = System.Windows.Forms.Cursor.Position;
        _ = TryDismissForOutsideClick(new Point(cursor.X, cursor.Y));
    }

    private bool TryDismissForOutsideClick(Point screenPoint)
    {
        if (!_clickAwayArmed || !IsVisible || _suppressClickAwayForTaskDropDown)
        {
            return false;
        }

        var localPoint = PointFromScreen(screenPoint);
        if (localPoint.X >= 0 &&
            localPoint.X <= ActualWidth &&
            localPoint.Y >= 0 &&
            localPoint.Y <= ActualHeight)
        {
            return false;
        }

        Close();
        return true;
    }

    private void ReminderWindow_Closed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _mousePollTimer.Stop();
        _visibleStopwatch.Stop();
        if (_taskEditor is not null)
        {
            _taskEditor.TextChanged -= TaskEditor_TextChanged;
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
                    _taskEditor.Text = selectedTask.Name;
                    _taskEditor.CaretIndex = _taskEditor.Text.Length;
                    _taskEditor.SelectionLength = 0;
                }
            }
            finally
            {
                _updatingTaskSearch = false;
            }

            ApplyTaskSearch(selectedTask.Name, openDropDown: false);
        }
    }

    private void TaskCombo_DropDownOpened(object sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _suppressClickAwayForTaskDropDown = true;
    }

    private void TaskCombo_DropDownClosed(object sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _suppressClickAwayForTaskDropDown = true;
    }

    private void ReminderWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if ((e.Key != Key.Enter && e.Key != Key.Return) ||
            Keyboard.Modifiers != ModifierKeys.None ||
            TaskCombo.IsDropDownOpen)
        {
            return;
        }

        e.Handled = true;
        Started = true;
        Close();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Started = true;
        Close();
    }

    private void GimmeBreakButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Snoozed = true;
        Close();
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }

    private static Color? BrushColor(Brush? brush) =>
        brush is SolidColorBrush solid ? solid.Color : null;
}
