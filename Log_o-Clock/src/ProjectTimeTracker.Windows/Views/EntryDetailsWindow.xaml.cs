using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ProjectTimeTracker.Core;
using MessageBox = ProjectTimeTracker.Windows.Views.ThemedMessageBox;

namespace ProjectTimeTracker.Windows.Views;

public partial class EntryDetailsWindow : Window
{
    private const string DefaultHeading = "What are you working on?";

    private readonly ITrackerStore _store;
    private Func<Guid, Guid, Guid?, string?, Task<TimeEntry>>? _ripEntry;
    private Func<Task>? _stopTimer;
    private readonly Func<Guid, DateTimeOffset, Task<TimeEntry>>? _updateRunningStart;
    private Guid _entryId;
    private Guid _projectId;
    private readonly bool _allowProjectSelection;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private DateTimeOffset? _runningStartUtc;
    private bool _suppressAutoClose;
    private bool _loadingTasks;
    private bool _closingAfterSave;
    private bool _settingStartTimeText;
    private bool _startTimeDirty;
    private bool _saved;
    private TextBox? _taskEditor;
    private ListCollectionView? _taskSearchView;
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

    public EntryDetailsWindow(
        ITrackerStore store,
        Guid entryId,
        Guid projectId,
        string displayProject,
        Guid? taskId,
        string? description,
        Func<Guid, Guid, Guid?, string?, Task<TimeEntry>>? ripEntry = null,
        Func<Task>? stopTimer = null,
        bool allowProjectSelection = false,
        DateTimeOffset? runningStartUtc = null,
        Func<Guid, DateTimeOffset, Task<TimeEntry>>? updateRunningStart = null,
        string? heading = null)
    {
        InitializeComponent();
        _store = store;
        _ripEntry = ripEntry;
        _stopTimer = stopTimer;
        _updateRunningStart = updateRunningStart;
        _entryId = entryId;
        _projectId = projectId;
        _allowProjectSelection = allowProjectSelection;
        _runningStartUtc = runningStartUtc?.ToUniversalTime();
        ApplyHeading(heading);

        ProjectText.Text = displayProject;
        ProjectText.Visibility = allowProjectSelection
            ? Visibility.Collapsed
            : Visibility.Visible;
        ProjectChooserPanel.Visibility = allowProjectSelection
            ? Visibility.Visible
            : Visibility.Collapsed;
        Height = allowProjectSelection ? 390 : 340;
        DescriptionText.Text = description ?? string.Empty;
        if (_runningStartUtc is { } startUtc && _updateRunningStart is not null)
        {
            StartTimePanel.Visibility = Visibility.Visible;
            SetStartTimeText(startUtc);
        }

        UpdateRunningActionsVisibility();
        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
        InitialTaskId = taskId;
    }

    public event EventHandler<EntryDetailsSavedEventArgs>? DetailsSaved;
    public Guid EntryId => _entryId;
    public bool WasSaved => _saved;
    internal string HeadingForPreview => HeadingText.Text;

    internal void ApplyHeading(string? heading)
    {
        HeadingText.Text = string.IsNullOrWhiteSpace(heading)
            ? DefaultHeading
            : heading;
        Title = HeadingText.Text;
    }
    private Guid? InitialTaskId { get; }

    public void CloseWithoutSaving()
    {
        _closingAfterSave = true;
        Close();
    }

    internal async Task SelectTaskForPreviewAsync(Guid taskId)
    {
        await ReloadTasksAsync(taskId);
        await TryPersistAsync();
    }

    internal async Task TypeTaskForPreviewAsync(string taskName)
    {
        await ReloadTasksAsync(selectedTaskId: null, pendingTaskText: taskName);
        await TryPersistAsync();
    }

    internal async Task VerifyTaskSearchAfterClearingSelectionForPreviewAsync(
        Guid expectedTaskId,
        string expectedTaskName,
        string searchText)
    {
        if (searchText.Length < 2 ||
            !expectedTaskName.Contains(searchText, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The popup task-search preview requires a two-character query matching the expected task.",
                nameof(searchText));
        }

        InitializeTaskSearch();
        var editor = _taskEditor
            ?? throw new InvalidOperationException(
                "The running-entry popup task-search textbox is unavailable.");
        if (TaskCombo.SelectedItem is not SavedTask)
        {
            throw new InvalidOperationException(
                "The running-entry popup needs an existing selected task before testing replacement search.");
        }

        var originalTopmost = Topmost;
        try
        {
            Topmost = true;
            Activate();
            Focus();
            _ = Keyboard.Focus(editor);
            editor.SelectAll();
            editor.SelectedText = searchText[..1];
            editor.CaretIndex = editor.Text.Length;
            editor.SelectionLength = 0;
            await Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ContextIdle);
            await Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ContextIdle);

            if (TaskCombo.SelectedItem is not null ||
                !string.Equals(editor.Text, searchText[..1], StringComparison.Ordinal) ||
                editor.SelectionLength != 0 ||
                editor.CaretIndex != 1 ||
                !TaskCombo.IsDropDownOpen ||
                _taskSearchView?.Cast<SavedTask>().Any(task =>
                    task.Id == expectedTaskId) != true)
            {
                throw new InvalidOperationException(
                    "Clearing an existing popup task and typing the first search character did not detach the old selection or open matching suggestions.");
            }

            editor.SelectedText = searchText[1..];
            editor.CaretIndex = editor.Text.Length;
            editor.SelectionLength = 0;
            await Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ContextIdle);
            if (!string.Equals(editor.Text, searchText, StringComparison.Ordinal) ||
                editor.SelectionLength != 0 ||
                editor.CaretIndex != searchText.Length ||
                !TaskCombo.IsDropDownOpen)
            {
                throw new InvalidOperationException(
                    "Typing into the running-entry popup task search lost text, moved the caret, or closed matching suggestions.");
            }

            TaskCombo.SelectedValue = expectedTaskId;
            await Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.Input);
            await _saveGate.WaitAsync();
            _saveGate.Release();
            var persistedEntry = await _store.GetTimeEntryAsync(_entryId);
            if (TaskCombo.SelectedValue is not Guid selectedTaskId ||
                selectedTaskId != expectedTaskId ||
                !string.Equals(
                    TaskCombo.Text,
                    expectedTaskName,
                    StringComparison.Ordinal) ||
                TaskCombo.IsDropDownOpen ||
                persistedEntry?.TaskId != expectedTaskId)
            {
                throw new InvalidOperationException(
                    "Choosing a running-entry popup task suggestion did not replace and autosave the task.");
            }
        }
        finally
        {
            TaskCombo.IsDropDownOpen = false;
            Topmost = originalTopmost;
        }
    }

    internal async Task SelectProjectForPreviewAsync(Guid projectId)
    {
        _loadingTasks = true;
        try
        {
            ProjectCombo.SelectedValue = projectId;
        }
        finally
        {
            _loadingTasks = false;
        }

        var pendingTaskText = TaskCombo.Text;
        _projectId = projectId;
        await ReloadTasksAsync(selectedTaskId: null, pendingTaskText);
        await TryPersistAsync();
    }

    internal Task RipForPreviewAsync() => RipAsync();

    internal Task StopForPreviewAsync() => StopTimerAsync();

    internal Task PersistForPreviewAsync() => TryPersistAsync();

    internal async Task<DateTimeOffset?> SetStartTimeForPreviewAsync(string value)
    {
        StartTimeText.Text = value;
        await TryPersistAsync();
        return _runningStartUtc;
    }

    internal Visibility StartTimeVisibilityForPreview => StartTimePanel.Visibility;

    internal string StartTimeTextForPreview => StartTimeText.Text;

    internal void UpdateRunningStartForExternalChange(TimeEntry entry)
    {
        if (entry.Id != _entryId ||
            _updateRunningStart is null ||
            _startTimeDirty ||
            StartTimeText.IsKeyboardFocusWithin)
        {
            return;
        }

        _runningStartUtc = entry.StartUtc;
        SetStartTimeText(entry.StartUtc);
    }

    internal void EnableRunningActions(
        Func<Guid, Guid, Guid?, string?, Task<TimeEntry>> ripEntry,
        Func<Task> stopTimer)
    {
        _ripEntry = ripEntry;
        _stopTimer = stopTimer;
        UpdateRunningActionsVisibility();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        DescriptionText.SetTagDefinitions(await _store.GetTagsAsync(_projectId));
        if (_allowProjectSelection)
        {
            _loadingTasks = true;
            try
            {
                ProjectCombo.ItemsSource = await _store.GetProjectOptionsAsync();
                ProjectCombo.SelectedValue =
                    _projectId == SystemEntityIds.UnassignedProjectId
                        ? null
                        : _projectId;
            }
            finally
            {
                _loadingTasks = false;
            }
        }

        await ReloadTasksAsync(InitialTaskId);
        InitializeTaskSearch();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 18;
        Top = area.Bottom - ActualHeight - 18;
        TaskCombo.Focus();
    }

    private async void OnDeactivated(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (_suppressAutoClose || _closingAfterSave)
        {
            return;
        }

        _closingAfterSave = true;
        if (await TryPersistAsync())
        {
            Close();
        }
        else
        {
            _closingAfterSave = false;
            Activate();
        }
    }

    private void TaskCombo_DropDownOpened(object sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _suppressAutoClose = true;
    }

    private void TaskCombo_DropDownClosed(object sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _suppressAutoClose = false;
        Activate();
    }

    private void ProjectCombo_DropDownOpened(object sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _suppressAutoClose = true;
    }

    private void ProjectCombo_DropDownClosed(object sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _suppressAutoClose = false;
        Activate();
    }

    private async void ProjectCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loadingTasks || !_allowProjectSelection)
        {
            return;
        }

        var pendingTaskText = TaskCombo.Text;
        _projectId = ProjectCombo.SelectedValue is Guid projectId
            ? projectId
            : SystemEntityIds.UnassignedProjectId;
        DescriptionText.SetTagDefinitions(await _store.GetTagsAsync(_projectId));
        await ReloadTasksAsync(selectedTaskId: null, pendingTaskText);
        await TryPersistAsync();
    }

    private async void TaskCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loadingTasks || _updatingTaskSearch)
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

        await TryPersistAsync();
    }

    private async void RipButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RipAsync();
    }

    private async void StopTimerButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await StopTimerAsync();
    }

    private void StartTimeText_TextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_settingStartTimeText && _updateRunningStart is not null)
        {
            _startTimeDirty = true;
        }
    }

    private async void StartTimeText_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_startTimeDirty)
        {
            await TryPersistAsync();
        }
    }

    private async void EntryDetailsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if ((e.Key != Key.Enter && e.Key != Key.Return) ||
            Keyboard.Modifiers != ModifierKeys.None ||
            TaskCombo.IsDropDownOpen ||
            ProjectCombo.IsDropDownOpen)
        {
            return;
        }

        e.Handled = true;
        await ApplyAndCloseAsync();
    }

    private async Task ApplyAndCloseAsync()
    {
        if (_closingAfterSave)
        {
            return;
        }

        _closingAfterSave = true;
        if (await TryPersistAsync())
        {
            Close();
        }
        else
        {
            _closingAfterSave = false;
        }
    }

    private async Task RipAsync()
    {
        if (_ripEntry is null || _closingAfterSave)
        {
            return;
        }

        _suppressAutoClose = true;
        RipButton.IsEnabled = false;
        await _saveGate.WaitAsync();
        try
        {
            if (!await TryPersistStartTimeAsync())
            {
                return;
            }

            var taskId = await ResolveTaskAsync();
            var description = string.IsNullOrWhiteSpace(DescriptionText.Text)
                ? null
                : DescriptionText.Text.Trim();
            var newEntry = await _ripEntry(
                _entryId,
                _projectId,
                taskId,
                description);
            _entryId = newEntry.Id;
            _runningStartUtc = newEntry.StartUtc;
            SetStartTimeText(newEntry.StartUtc);
            _saved =
                _projectId != SystemEntityIds.UnassignedProjectId &&
                (taskId is not null || description is not null);
            SaveStatusText.Text = "New entry started · further changes apply only to it";
            DetailsSaved?.Invoke(
                this,
                new EntryDetailsSavedEventArgs(
                    _entryId,
                    _projectId,
                    taskId,
                    description));
            DescriptionText.Focus();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not rip timer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _saveGate.Release();
            RipButton.IsEnabled = true;
            _suppressAutoClose = false;
            Activate();
        }
    }

    private async Task StopTimerAsync()
    {
        if (_stopTimer is null || _closingAfterSave)
        {
            return;
        }

        _suppressAutoClose = true;
        RipButton.IsEnabled = false;
        StopTimerButton.IsEnabled = false;
        try
        {
            if (!await TryPersistAsync())
            {
                return;
            }

            _closingAfterSave = true;
            await _stopTimer();
            if (IsLoaded)
            {
                Close();
            }
        }
        catch (Exception exception)
        {
            _closingAfterSave = false;
            MessageBox.Show(
                this,
                exception.Message,
                "Could not stop timer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (IsLoaded)
            {
                RipButton.IsEnabled = true;
                StopTimerButton.IsEnabled = true;
                _suppressAutoClose = false;
            }
        }
    }

    private void UpdateRunningActionsVisibility()
    {
        RipButton.Visibility = _ripEntry is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        StopTimerButton.Visibility = _stopTimer is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        RunningActionsPanel.Visibility =
            _ripEntry is null && _stopTimer is null
                ? Visibility.Collapsed
                : Visibility.Visible;
        RunningFooter.Visibility =
            _updateRunningStart is null &&
            _ripEntry is null &&
            _stopTimer is null
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private async Task ReloadTasksAsync(
        Guid? selectedTaskId,
        string? pendingTaskText = null)
    {
        _loadingTasks = true;
        _updatingTaskSearch = true;
        try
        {
            var tasks = await _store.GetTasksAsync(_projectId);
            _taskSearchView = new ListCollectionView(tasks.ToList());
            TaskCombo.ItemsSource = _taskSearchView;
            TaskCombo.SelectedValue = selectedTaskId;
            if (selectedTaskId is null)
            {
                TaskCombo.SelectedIndex = -1;
                TaskCombo.Text = pendingTaskText ?? string.Empty;
            }

            ApplyTaskSearch(TaskCombo.Text, openDropDown: false);
        }
        finally
        {
            _updatingTaskSearch = false;
            _loadingTasks = false;
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

    private async Task<Guid?> ResolveTaskAsync()
    {
        var taskName = TaskCombo.Text?.Trim();
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return TaskCombo.SelectedValue is Guid selectedTaskId
                ? selectedTaskId
                : null;
        }

        if (TaskCombo.SelectedItem is SavedTask selectedTask &&
            string.Equals(taskName, selectedTask.Name, StringComparison.Ordinal))
        {
            return selectedTask.Id;
        }

        var task = await _store.GetOrAddTaskAsync(_projectId, taskName);
        await ReloadTasksAsync(task.Id);
        TaskCombo.Text = task.Name;
        return task.Id;
    }

    private async Task<bool> TryPersistAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            if (!await TryPersistStartTimeAsync())
            {
                return false;
            }

            var taskId = await ResolveTaskAsync();
            var description = string.IsNullOrWhiteSpace(DescriptionText.Text)
                ? null
                : DescriptionText.Text.Trim();
            await _store.UpdateEntryAssignmentAsync(
                _entryId,
                _projectId,
                taskId,
                description,
                DateTimeOffset.UtcNow);
            _saved =
                _projectId != SystemEntityIds.UnassignedProjectId &&
                (taskId is not null || !string.IsNullOrWhiteSpace(description));
            SaveStatusText.Text = _saved ? "Saved automatically · click away when finished" : "Click away to finish later";
            DetailsSaved?.Invoke(
                this,
                new EntryDetailsSavedEventArgs(
                    _entryId,
                    _projectId,
                    taskId,
                    description));
            return true;
        }
        catch (Exception exception)
        {
            _suppressAutoClose = true;
            try
            {
                MessageBox.Show(this, exception.Message, "Could not save details", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _suppressAutoClose = false;
            }

            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<bool> TryPersistStartTimeAsync()
    {
        if (!_startTimeDirty)
        {
            return true;
        }

        if (_updateRunningStart is null || _runningStartUtc is not { } currentStartUtc)
        {
            _startTimeDirty = false;
            return true;
        }

        if (!RunningStartTimeText.TryResolve(
                StartTimeText.Text,
                currentStartUtc,
                DateTimeOffset.UtcNow,
                TimeZoneInfo.Local,
                out var startUtc))
        {
            SaveStatusText.Text = "Enter a valid Start time that is not in the future";
            return false;
        }

        var updated = await _updateRunningStart(_entryId, startUtc);
        _runningStartUtc = updated.StartUtc;
        SetStartTimeText(updated.StartUtc);
        return true;
    }

    private void SetStartTimeText(DateTimeOffset startUtc)
    {
        _settingStartTimeText = true;
        try
        {
            StartTimeText.Text = TimeOfDayText.Format(
                TimeZoneInfo.ConvertTime(startUtc, TimeZoneInfo.Local).TimeOfDay);
            _startTimeDirty = false;
        }
        finally
        {
            _settingStartTimeText = false;
        }
    }
}

public sealed class EntryDetailsSavedEventArgs(
    Guid entryId,
    Guid projectId,
    Guid? taskId,
    string? description) : EventArgs
{
    public Guid EntryId { get; } = entryId;
    public Guid ProjectId { get; } = projectId;
    public Guid? TaskId { get; } = taskId;
    public string? Description { get; } = description;
}
