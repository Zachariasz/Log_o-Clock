using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using ProjectTimeTracker.Core;
using ProjectTimeTracker.Infrastructure;
using ProjectTimeTracker.Windows.Controls;
using ProjectTimeTracker.Windows.Converters;
using ProjectTimeTracker.Windows.Services;
using ProjectTimeTracker.Windows.ViewModels;
using ProjectTimeTracker.Windows.Views;
using MessageBox = ProjectTimeTracker.Windows.Views.ThemedMessageBox;

namespace ProjectTimeTracker.Windows;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static readonly DependencyProperty HistoryTextWrappingProperty = DependencyProperty.RegisterAttached(
        "HistoryTextWrapping",
        typeof(bool),
        typeof(MainWindow),
        new PropertyMetadata(false));

    public static readonly DependencyProperty DateRangeShortcutActiveProperty = DependencyProperty.RegisterAttached(
        "DateRangeShortcutActive",
        typeof(bool),
        typeof(MainWindow),
        new PropertyMetadata(false));

    private const string HistoryViewSettingKey = "history.view.columns.v1";
    private const string HistoryColumnsSubmenuTag = "HistoryColumnsSubmenu";
    private const string ReportViewSettingKey = "reports.view.columns.v1";
    private bool _ruleGridDefaultColumnsApplied;
    private const string ReportColumnsSubmenuTag = "ReportColumnsSubmenu";
    private const double SidebarNavigationReservedHeight = 160d;
    private static readonly ReportColumnDefinition[] ReportColumnDefinitions =
    [
        new("Task", 220),
        new("Time", 82),
        new("Time + idle", 96),
        new("Calls", 82),
        new("Value", 112),
        new("Logs", 72),
    ];
    private static readonly string[] ReportClientChartColors =
    [
        "#6EA8FF",
        "#52C7A4",
        "#9D7BFF",
        "#F1B35A",
        "#E873A4",
        "#63C3E8",
        "#B6CE5A",
        "#FF7356",
    ];

    private readonly ITrackerStore _store;
    private readonly AppController _controller;
    private readonly IAutostartService _autostart;
    private readonly ITrelloSyncService _trelloSync;
    private readonly IGoogleSheetsSyncService _googleSheetsSync;
    private readonly UpdateCheckService _updateCheck;
    private readonly ProfileCatalog _profileCatalog;
    private readonly Func<Guid, Guid?, Task<bool>> _requestProfileSwitch;
    private readonly SemaphoreSlim _timerActionGate = new(1, 1);
    private TrackerProfile _activeProfile;
    private bool _loading;
    private bool _refreshPending;
    private bool _loaded;
    private bool _updatingTagFilters;
    private bool _updatingHistoryFilters;
    private bool _updatingReportFilters;
    private bool _updatingReportSelection;
    private bool _updatingRuleFilter;
    private bool _updatingSoftwareFilter;
    private bool _updatingTaskFilter;
    private bool _updatingTargetFilter;
    private bool _updatingAutomaticRecognitionControls;
    private TextBox? _timerTaskEditor;
    private ListCollectionView? _timerTaskSearchView;
    private string _timerTaskSearchText = string.Empty;
    private bool _updatingTimerTaskSearch;
    private bool _updatingTimerCall;
    private bool _settingTimerStartTimeText;
    private bool _timerStartTimeDirty;
    private int _timerProjectChangeVersion;
    private IReadOnlyList<ProjectOption> _projectOptions = [];
    private IReadOnlyList<Client> _activeClients = [];
    private IReadOnlyList<Project> _activeProjects = [];
    private IReadOnlyList<SavedTask> _activeTasks = [];
    private IReadOnlyList<Client> _reportClients = [];
    private IReadOnlyList<Project> _reportProjects = [];
    private IReadOnlyList<SavedTask> _reportTasks = [];
    private IReadOnlyList<TagDefinition> _tagDefinitions = [];
    private IReadOnlyList<TimeEntryRow> _historyRows = [];
    private IReadOnlyList<TaskRow> _taskRows = [];
    private IReadOnlyList<TaskRow> _frozenTaskRows = [];
    private IReadOnlyList<TrelloMappingRow> _trelloMappingRows = [];
    private IReadOnlyList<RuleRow> _ruleRows = [];
    private IReadOnlyList<RuleRow> _frozenRuleRows = [];
    private IReadOnlyList<SoftwareRow> _softwareRows = [];
    private IReadOnlyList<SoftwareRow> _frozenSoftwareRows = [];
    private IReadOnlyList<CustomTargetRow> _customTargetRows = [];
    private IReadOnlyList<ITargetManagementRow> _targetManagementRows = [];
    private IReadOnlyList<ITargetManagementRow> _frozenTargetManagementRows = [];
    private IReadOnlyList<ProjectTargetRow> _allTargetRows = [];
    private IReadOnlyList<ProjectTargetRow> _sidebarTargetRows = [];
    private Guid? _reportTargetProjectId;
    private Guid? _historyProjectFilterId;
    private Guid? _historyTaskFilterId;
    private bool _historyUnassignedOnly;
    private string? _historySortMemberPath;
    private ListSortDirection? _historySortDirection;
    private Guid? _ruleProjectFilterId;
    private Guid? _softwareProjectFilterId;
    private Guid? _taskProjectFilterId;
    private Guid? _targetProjectFilterId;
    private bool _targetGlobalOnly;
    private bool _preserveHistoryFiltersOnNextTabEntry;
    private DateTime? _historyContextAddDate;
    private bool _updatingHistoryView;
    private HwndSource? _windowSource;
    private HistoryViewState _defaultHistoryView = new([]);
    private HistoryViewState _savedHistoryView = new([]);
    private ReportViewState _reportView = CreateDefaultReportView();
    private ReportViewState _savedReportView = CreateDefaultReportView();
    private double _sidebarTargetsPanelPreferredHeight =
        SidebarTargetsPanelSettings.DefaultHeight;

    private sealed record HistoryColumnState(
        string Key,
        int DisplayIndex,
        DataGridLengthUnitType WidthUnit,
        double WidthValue,
        bool IsVisible);

    private sealed record HistoryViewState(
        IReadOnlyList<HistoryColumnState> Columns,
        bool WrapText = false);

    private sealed record ReportColumnDefinition(string Key, double Width);

    private sealed record ReportColumnState(string Key, bool IsVisible, double Width = 0);

    private sealed record ReportViewState(IReadOnlyList<ReportColumnState> Columns);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    private sealed class TimerTaskSearchComparer(string query) : IComparer
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

    public sealed record HistoryGroupLayout(
        Thickness DateMargin,
        Thickness DurationMargin,
        Visibility Visibility);

    public MainWindow(
        ITrackerStore store,
        AppController controller,
        IAutostartService autostart,
        ITrelloSyncService trelloSync,
        IGoogleSheetsSyncService googleSheetsSync,
        UpdateCheckService updateCheck,
        ProfileCatalog profileCatalog,
        TrackerProfile activeProfile,
        Func<Guid, Guid?, Task<bool>> requestProfileSwitch)
    {
        InitializeComponent();
        _store = store;
        _controller = controller;
        _autostart = autostart;
        _trelloSync = trelloSync;
        _googleSheetsSync = googleSheetsSync;
        _updateCheck = updateCheck;
        _profileCatalog = profileCatalog;
        _activeProfile = activeProfile;
        _requestProfileSwitch = requestProfileSwitch;
        UpdateProfileLabel();
        Loaded += OnLoaded;
        SourceInitialized += MainWindow_SourceInitialized;
        _controller.TimerTick += Controller_TimerTick;
        _controller.RunningEntryChanged += Controller_RunningEntryChanged;
        _controller.DataChanged += Controller_DataChanged;
        _controller.IdleProtectionChanged += Controller_IdleProtectionChanged;
        _controller.AutomaticRecognitionSettingsChanged += Controller_AutomaticRecognitionSettingsChanged;
        _trelloSync.SyncCompleted += TrelloSync_SyncCompleted;
        _googleSheetsSync.SyncCompleted += GoogleSheetsSync_SyncCompleted;
        _updateCheck.StateChanged += UpdateCheck_StateChanged;
        _defaultHistoryView = CaptureHistoryView();
        _savedHistoryView = _defaultHistoryView;
        BuildHistoryColumnsMenu();
        BuildReportColumnsMenu();
        HistoryGrid.ColumnReordered += HistoryGrid_ColumnReordered;
        HistoryGrid.Sorting += HistoryGrid_Sorting;
        HistoryGrid.PreviewMouseLeftButtonUp += HistoryGrid_PreviewMouseLeftButtonUp;
        HistoryGrid.SizeChanged += (_, _) => UpdateHistoryGroupLayout();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowMessageHook);
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        _controller.IdleProtectionChanged -= Controller_IdleProtectionChanged;
        _controller.AutomaticRecognitionSettingsChanged -= Controller_AutomaticRecognitionSettingsChanged;
        _trelloSync.SyncCompleted -= TrelloSync_SyncCompleted;
        _googleSheetsSync.SyncCompleted -= GoogleSheetsSync_SyncCompleted;
        _updateCheck.StateChanged -= UpdateCheck_StateChanged;
        base.OnClosed(e);
    }

    private IntPtr WindowMessageHook(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        _ = wParam;
        if (message == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            ApplyWorkingAreaMaximizeBounds(windowHandle, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ApplyWorkingAreaMaximizeBounds(IntPtr windowHandle, IntPtr minMaxInfoPointer)
    {
        var monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitorHandle == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(minMaxInfoPointer);
        minMaxInfo.MaxPosition = new NativePoint
        {
            X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left,
            Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top,
        };
        minMaxInfo.MaxSize = new NativePoint
        {
            X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left,
            Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top,
        };
        Marshal.StructureToPtr(minMaxInfo, minMaxInfoPointer, false);
    }

    public bool AllowClose { get; set; }
    internal bool IsReadyForPreview => _loaded && !_loading;

    public static bool GetHistoryTextWrapping(DependencyObject element) =>
        (bool)element.GetValue(HistoryTextWrappingProperty);

    public static void SetHistoryTextWrapping(DependencyObject element, bool value) =>
        element.SetValue(HistoryTextWrappingProperty, value);

    public static bool GetDateRangeShortcutActive(DependencyObject element) =>
        (bool)element.GetValue(DateRangeShortcutActiveProperty);

    public static void SetDateRangeShortcutActive(DependencyObject element, bool value) =>
        element.SetValue(DateRangeShortcutActiveProperty, value);

    internal void VerifyEnglishInterfaceCultureForPreview()
    {
        var date = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Unspecified);
        var offset = TimeZoneInfo.Local.GetUtcOffset(date);
        var startLocal = new DateTimeOffset(date, offset);
        var entry = new TimeEntryView(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            "User client",
            "User project",
            "User task",
            "User-created description remains untouched",
            startLocal.ToUniversalTime(),
            startLocal.AddHours(1).ToUniversalTime(),
            0,
            false,
            TrackingSource.Manual);
        var row = new TimeEntryRow(entry, startLocal.AddHours(1), []);
        if (!string.Equals(row.Day, "Wednesday, 15 July 2026", StringComparison.Ordinal) ||
            !string.Equals(row.Start, "12:00", StringComparison.Ordinal) ||
            !string.Equals(row.Description, entry.Description, StringComparison.Ordinal) ||
            !string.Equals(Language.IetfLanguageTag, AppTextCulture.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                HistoryRangePicker.CalendarForPreview.Language.IetfLanguageTag,
                AppTextCulture.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "English interface culture was not applied to History dates, times, calendars, or preserved user content.");
        }
    }

    internal Task ShowFirstReportTaskInHistoryForPreviewAsync()
    {
        var task = (ReportGrid.ItemsSource as IEnumerable<ProjectReportSummaryRow>)?
            .SelectMany(project => project.Tasks)
            .FirstOrDefault();
        return task is null
            ? Task.CompletedTask
            : ShowInHistoryAsync(task.ProjectId, task.TaskId, task.IsUnassigned);
    }

    internal EntryEditorWindow CreateHistoryGroupEntryEditorForPreview(DateTime expectedDate)
    {
        var view = CollectionViewSource.GetDefaultView(HistoryGrid.ItemsSource);
        var group = view.Groups?
            .OfType<CollectionViewGroup>()
            .FirstOrDefault(candidate => GetHistoryGroupDate(candidate) == expectedDate.Date)
            ?? throw new InvalidOperationException(
                "The expected History day group is not visible.");
        var clickedDate = GetHistoryGroupDate(group)
            ?? throw new InvalidOperationException(
                "The History day group did not resolve to a local date.");
        return new EntryEditorWindow(
            _store,
            initialLocalDate: clickedDate)
        {
            Owner = this,
        };
    }

    internal TargetSettingsWindow CreateTargetSettingsWindowForPreview() =>
        new(_projectOptions) { Owner = this };

    internal void VerifyReportTaskDateSortingForPreview(
        Guid projectId,
        Guid expectedNewestTaskId,
        Guid expectedOlderTaskId)
    {
        var project = (ReportGrid.ItemsSource as IEnumerable<ProjectReportSummaryRow>)?
            .SingleOrDefault(row => row.ProjectId == projectId)
            ?? throw new InvalidOperationException(
                "The report task sorting smoke project is missing.");
        if (project.Tasks.Count < 2 ||
            project.Tasks[0].TaskId != expectedNewestTaskId ||
            project.Tasks[1].TaskId != expectedOlderTaskId ||
            project.Tasks[0].LatestActivityUtc <= project.Tasks[1].LatestActivityUtc ||
            project.Tasks[0].TotalSeconds >= project.Tasks[1].TotalSeconds)
        {
            throw new InvalidOperationException(
                "Report tasks are not sorted by latest activity descending.");
        }
    }

    internal void VerifySingleReportObjectSelectionForPreview(
        Guid firstProjectId,
        Guid secondProjectId)
    {
        ReportGrid.UpdateLayout();
        var projects = (ReportGrid.ItemsSource as IEnumerable<ProjectReportSummaryRow>)?.ToArray()
            ?? throw new InvalidOperationException("The report has no projects to verify selection.");
        var firstProject = projects.SingleOrDefault(project => project.ProjectId == firstProjectId)
            ?? throw new InvalidOperationException("The first selection smoke project is missing.");
        var secondProject = projects.SingleOrDefault(project => project.ProjectId == secondProjectId)
            ?? throw new InvalidOperationException("The second selection smoke project is missing.");
        ExpandReportProjectTasksForPreview(firstProjectId);
        ExpandReportProjectTasksForPreview(secondProjectId);
        var taskGrids = FindVisualDescendants<DataGrid>(ReportGrid).ToArray();
        var firstTaskGrid = taskGrids.SingleOrDefault(grid =>
            (grid.DataContext as ProjectReportSummaryRow)?.ProjectId == firstProjectId)
            ?? throw new InvalidOperationException("The first project task table is missing.");
        var secondTaskGrid = taskGrids.SingleOrDefault(grid =>
            (grid.DataContext as ProjectReportSummaryRow)?.ProjectId == secondProjectId)
            ?? throw new InvalidOperationException("The second project task table is missing.");
        var firstTask = firstTaskGrid.Items.OfType<ReportTaskSummaryRow>().FirstOrDefault()
            ?? throw new InvalidOperationException("The first project has no report task to select.");
        var secondTask = secondTaskGrid.Items.OfType<ReportTaskSummaryRow>().FirstOrDefault()
            ?? throw new InvalidOperationException("The second project has no report task to select.");

        ReportGrid.SelectedItem = firstProject;
        if (ReportGrid.ItemContainerGenerator.ContainerFromItem(firstProject) is not ListBoxItem firstProjectContainer ||
            firstProjectContainer.Template.FindName("ReportProjectContainerSurface", firstProjectContainer) is not Border { Background: SolidColorBrush brush } ||
            brush.Color.A != 0)
        {
            throw new InvalidOperationException(
                "Selecting a Report project must not add a selection background; task rows retain the only visible selection state.");
        }
        AssertSingleReportObjectSelection(firstProject, taskGrids, "project selection");

        firstTaskGrid.SelectedItem = firstTask;
        AssertSingleReportObjectSelection(null, taskGrids, "first task selection", firstTaskGrid);

        secondTaskGrid.SelectedItem = secondTask;
        AssertSingleReportObjectSelection(null, taskGrids, "second task selection", secondTaskGrid);

        ReportGrid.SelectedItem = secondProject;
        AssertSingleReportObjectSelection(secondProject, taskGrids, "returning to a project selection");
        ReportGrid.UnselectAll();
        CollapseAllReportProjectTasksForPreview();
    }

    private void VerifyReportTaskGroupsForPreview()
    {
        ReportGrid.UpdateLayout();
        var projects = (ReportGrid.ItemsSource as IEnumerable<ProjectReportSummaryRow>)?.ToArray()
            ?? throw new InvalidOperationException("The report has no project groups to verify.");
        var expanders = FindVisualDescendants<Expander>(ReportGrid)
            .Where(expander => expander.DataContext is ProjectReportSummaryRow)
            .ToArray();
        if (expanders.Length != projects.Length || expanders.Any(expander => expander.IsExpanded))
        {
            throw new InvalidOperationException(
                "Reports project task groups must start collapsed after a refresh.");
        }

        var project = projects[0];
        var expander = expanders.Single(candidate =>
            (candidate.DataContext as ProjectReportSummaryRow)?.ProjectId == project.ProjectId);
        var footer = FindVisualDescendants<Grid>(ReportGrid).SingleOrDefault(grid =>
            string.Equals(grid.Tag as string, "ReportSummaryFooter", StringComparison.Ordinal) &&
            ReferenceEquals(grid.DataContext, project));
        var header = FindVisualDescendants<Grid>(ReportGrid).SingleOrDefault(grid =>
            string.Equals(grid.Tag as string, "ReportSummaryHeader", StringComparison.Ordinal) &&
            ReferenceEquals(grid.DataContext, project));
        if (header is null || footer is null || !header.IsVisible || !footer.IsVisible ||
            header.Children.OfType<TextBlock>().Select(textBlock => textBlock.Text).SequenceEqual(
                ["Task", "Time", "Time + idle", "Calls", "Value", "Logs"]) == false)
        {
            throw new InvalidOperationException(
                "A collapsed Reports project group must retain visible metric names and its total.");
        }

        var toggle = expander.Template.FindName("HeaderToggle", expander) as ToggleButton;
        if (toggle is null || toggle.IsChecked != false)
        {
            throw new InvalidOperationException(
                "The collapsed Reports project group is missing its task-details chevron.");
        }

        toggle.IsChecked = true;
        ReportGrid.UpdateLayout();
        var taskGrid = FindVisualDescendants<DataGrid>(expander).SingleOrDefault(grid =>
            ReferenceEquals(grid.DataContext, project));
        if (!expander.IsExpanded || taskGrid is null || !taskGrid.IsVisible)
        {
            throw new InvalidOperationException(
                "Expanding a Reports project group did not reveal its task table.");
        }

        toggle.IsChecked = false;
        ReportGrid.UpdateLayout();
        if (expander.IsExpanded || taskGrid.IsVisible || !header.IsVisible || !footer.IsVisible)
        {
            throw new InvalidOperationException(
                "Collapsing a Reports project group did not hide only its task table.");
        }
    }

    private void ExpandReportProjectTasksForPreview(Guid projectId)
    {
        var expander = FindVisualDescendants<Expander>(ReportGrid).SingleOrDefault(candidate =>
            (candidate.DataContext as ProjectReportSummaryRow)?.ProjectId == projectId)
            ?? throw new InvalidOperationException("The report project task group is missing.");
        expander.IsExpanded = true;
        ReportGrid.UpdateLayout();
    }

    private void ExpandAllReportProjectTasksForPreview()
    {
        foreach (var expander in FindVisualDescendants<Expander>(ReportGrid)
                     .Where(candidate => candidate.DataContext is ProjectReportSummaryRow))
        {
            expander.IsExpanded = true;
        }

        ReportGrid.UpdateLayout();
    }

    private void CollapseAllReportProjectTasksForPreview()
    {
        foreach (var expander in FindVisualDescendants<Expander>(ReportGrid)
                     .Where(candidate => candidate.DataContext is ProjectReportSummaryRow))
        {
            expander.IsExpanded = false;
        }

        ReportGrid.UpdateLayout();
    }

    private void AssertSingleReportObjectSelection(
        ProjectReportSummaryRow? expectedProject,
        IReadOnlyCollection<DataGrid> taskGrids,
        string operation,
        DataGrid? expectedTaskGrid = null)
    {
        var selectedTaskGrids = taskGrids
            .Where(grid => grid.SelectedItem is ReportTaskSummaryRow)
            .ToArray();
        if ((expectedProject is null && ReportGrid.SelectedItem is not null) ||
            (expectedProject is not null && !ReferenceEquals(ReportGrid.SelectedItem, expectedProject)) ||
            (expectedProject is null && selectedTaskGrids.Length != 1) ||
            (expectedProject is not null && selectedTaskGrids.Length != 0) ||
            (expectedTaskGrid is not null && !ReferenceEquals(selectedTaskGrids.SingleOrDefault(), expectedTaskGrid)))
        {
            throw new InvalidOperationException(
                $"Reports did not retain exactly one selected object after {operation}.");
        }
    }

    internal static void VerifyReportChartDurationFormattingForPreview()
    {
        const long durationSeconds = 5_430;
        var project = new ProjectReportSummaryRow(
            Guid.NewGuid(),
            "Preview client",
            "Preview project",
            "#339CFF",
            durationSeconds,
            durationSeconds,
            durationSeconds,
            0,
            1,
            "—",
            100,
            []);
        if (!string.Equals(
                FormatReportChartDuration(durationSeconds),
                "1:30 h",
                StringComparison.Ordinal) ||
            !string.Equals(
                project.LegendDetail,
                "1:30 h · 100%",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Report chart durations are not formatted as sexagesimal hours and minutes.");
        }
    }

    internal void VerifyReportClientChartForPreview(
        IReadOnlyDictionary<string, long> expectedClients)
    {
        var rows = ReportClientLegendItems.ItemsSource?
            .OfType<ClientReportSummaryRow>()
            .ToArray() ?? [];
        var tabs = ReportChartTabs.Items.OfType<TabItem>().ToArray();
        var expectedTotalSeconds = expectedClients.Values.Sum();
        if (tabs.Length != 2 ||
            !string.Equals(tabs[0].Header as string, "Projects", StringComparison.Ordinal) ||
            !string.Equals(tabs[1].Header as string, "Clients", StringComparison.Ordinal) ||
            rows.Length != expectedClients.Count ||
            expectedClients.Any(expected =>
                rows.All(row =>
                    !string.Equals(row.Client, expected.Key, StringComparison.Ordinal) ||
                    row.TotalSeconds != expected.Value)) ||
            !string.Equals(
                ReportClientDonutTotalHours.Text,
                FormatReportChartDuration(expectedTotalSeconds),
                StringComparison.Ordinal) ||
            ReportClientDonutImage.Source is not DrawingImage ||
            !ReportClientChartRangeText.Text.StartsWith(
                "Current month",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Reports is missing its switchable current-month client chart or its client totals are incorrect.");
        }

        ReportChartTabs.SelectedIndex = 1;
        ReportChartTabs.UpdateLayout();
        if (ReportChartTabs.SelectedIndex != 1 ||
            !tabs[1].IsSelected ||
            tabs[0].IsSelected)
        {
            throw new InvalidOperationException(
                "The Reports chart did not switch from Projects to Clients.");
        }
    }

    internal async Task VerifyExcludedSoftwareReviewSettingForPreviewAsync()
    {
        RecentEntryResumeMinutesText.Text = "3";
        await ApplyRecentEntryResumeMinutesAsync();
        if (_controller.RecentEntryResumeMaximumGapMinutes != 3 ||
            RecentEntryResumeValidationText.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException(
                "The Settings recent-entry resume time did not apply.");
        }

        RecentEntryResumeMinutesText.Text = "2";
        await ApplyRecentEntryResumeMinutesAsync();
        if (_controller.RecentEntryResumeMaximumGapMinutes != 2)
        {
            throw new InvalidOperationException(
                "The Settings recent-entry resume time could not be changed again.");
        }

        ExcludedSoftwareReviewMinutesText.Text = "7";
        await ApplyExcludedSoftwareReviewMinutesAsync();
        if (_controller.ExcludedSoftwareReviewMinimumMinutes != 7 ||
            ExcludedSoftwareReviewValidationText.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException(
                "The Settings excluded-software review time did not apply.");
        }

        ExcludedSoftwareReviewMinutesText.Text = "5";
        await ApplyExcludedSoftwareReviewMinutesAsync();
        if (_controller.ExcludedSoftwareReviewMinimumMinutes != 5)
        {
            throw new InvalidOperationException(
                "The Settings excluded-software review time could not be changed again.");
        }

        AccumulatedAwayReviewMinutesText.Text = "7";
        await ApplyAccumulatedAwayReviewMinutesAsync();
        if (_controller.AccumulatedAwayReviewMinimumMinutes != 7 ||
            AccumulatedAwayReviewValidationText.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException(
                "The Settings accumulated-away review time did not apply.");
        }

        AccumulatedAwayReviewMinutesText.Text = "5";
        await ApplyAccumulatedAwayReviewMinutesAsync();
        if (_controller.AccumulatedAwayReviewMinimumMinutes != 5)
        {
            throw new InvalidOperationException(
                "The Settings accumulated-away review time could not be changed again.");
        }
    }

    internal async Task VerifyIdleProtectionSettingsForPreviewAsync()
    {
        for (var attempt = 0;
             attempt < 50 && !_controller.IdleProtectionState.IsInitialized;
             attempt++)
        {
            await Task.Delay(100);
        }

        if (!_controller.IdleProtectionState.IsInitialized)
        {
            throw new InvalidOperationException(
                "The Windows idle-protection monitor did not publish its availability state.");
        }

        await _controller.SetCallsIdleProtectionEnabledAsync(false);
        await _controller.SetVideoIdleProtectionEnabledAsync(false);
        if (await _store.GetSettingAsync(IdleProtectionSettings.CallsEnabledKey) != "false" ||
            await _store.GetSettingAsync(IdleProtectionSettings.VideoEnabledKey) != "false")
        {
            throw new InvalidOperationException(
                "The idle-protection switches were not persisted when disabled.");
        }

        await _controller.SetCallsIdleProtectionEnabledAsync(true);
        await _controller.SetVideoIdleProtectionEnabledAsync(true);
        CallsIdleProtectionCheck.IsChecked = true;
        VideoIdleProtectionCheck.IsChecked = true;
        if (await _store.GetSettingAsync(IdleProtectionSettings.CallsEnabledKey) != "true" ||
            await _store.GetSettingAsync(IdleProtectionSettings.VideoEnabledKey) != "true")
        {
            throw new InvalidOperationException(
                "The idle-protection switches were not persisted when enabled.");
        }

        var now = DateTimeOffset.UtcNow;
        UpdateIdleProtectionStatus(new IdleProtectionState(
            IdleProtectionReason.CommunicationAudio |
            IdleProtectionReason.ForegroundAudio |
            IdleProtectionReason.VideoPlayback,
            CallsAvailable: true,
            VideoAvailable: true,
            IsInitialized: true,
            now));
        if (!IdleProtectionStatusText.Text.Contains("Call active", StringComparison.Ordinal) ||
            !IdleProtectionStatusText.Text.Contains("Foreground audio active", StringComparison.Ordinal) ||
            !IdleProtectionStatusText.Text.Contains("Video playing", StringComparison.Ordinal) ||
            !IdleProtectionPrivacyText.Text.Contains(
                "microphone activity is never accessed",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The live idle-protection status or output-only privacy statement is incomplete.");
        }

        UpdateIdleProtectionStatus(_controller.IdleProtectionState);
    }

    internal async Task VerifyBreakReminderSettingsForPreviewAsync()
    {
        BreakReminderMinutesText.Text = "90";
        await ApplyBreakReminderMinutesAsync();
        if (_controller.BreakReminderIntervalMinutes != 90 ||
            await _store.GetSettingAsync(BreakReminderSettings.IntervalMinutesKey) != "90" ||
            BreakReminderValidationText.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException(
                "The Settings break reminder interval did not apply and persist.");
        }

        BreakReminderScreenCenter.IsChecked = true;
        await Task.Delay(100);
        if (_controller.BreakReminderPlacement != BreakReminderPlacement.ScreenCenter ||
            await _store.GetSettingAsync(BreakReminderSettings.PlacementKey) !=
                BreakReminderPlacement.ScreenCenter.ToString())
        {
            throw new InvalidOperationException(
                "The Settings break reminder position did not apply and persist.");
        }

        BreakReminderBathroomMessageCheck.IsChecked = false;
        await Task.Delay(100);
        if (BreakReminderSettings.ParseEnabledMessageIds(
                await _store.GetSettingAsync(BreakReminderSettings.EnabledMessageIdsKey))
            .Contains("bathroom"))
        {
            throw new InvalidOperationException(
                "The Settings break reminder message list did not persist a disabled message.");
        }

        BreakReminderBathroomMessageCheck.IsChecked = true;
        await Task.Delay(100);

        BreakReminderMinutesText.Text =
            BreakReminderSettings.DefaultIntervalMinutes.ToString(CultureInfo.CurrentCulture);
        await ApplyBreakReminderMinutesAsync();
        BreakReminderBottomRight.IsChecked = true;
        await Task.Delay(100);
    }

    internal void VerifyTrelloUiForPreview()
    {
        var requiredControls = new FrameworkElement[]
        {
            TrelloConnectionText, TrelloSyncStatusText,
            TrelloConnectButton, TrelloSyncButton, TrelloDisconnectButton, TrelloMappingsGrid,
        };
        if (requiredControls.Any(control => string.IsNullOrWhiteSpace(control.Name)))
        {
            throw new InvalidOperationException("The Settings screen is missing a required Trello control.");
        }

        var taskMenuHeaders = TasksGrid.ContextMenu?.Items
            .OfType<MenuItem>()
            .Select(item => item.Header?.ToString())
            .ToArray() ?? [];
        var mappingMenuHeaders = TrelloMappingsGrid.ContextMenu?.Items
            .OfType<MenuItem>()
            .Select(item => item.Header?.ToString())
            .ToArray() ?? [];
        if (!taskMenuHeaders.Contains("Open in Trello", StringComparer.Ordinal) ||
            !mappingMenuHeaders.Any(header => header?.StartsWith("Add mapping", StringComparison.Ordinal) == true) ||
            !mappingMenuHeaders.Any(header => header?.StartsWith("Edit", StringComparison.Ordinal) == true) ||
            !mappingMenuHeaders.Contains("Remove", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The Trello task or mapping context-menu workflow is incomplete.");
        }

        if (TrelloMappingsGrid.Columns.Count != 4 ||
            TrelloMappingsGrid.Columns.Any(column => column.Visibility != Visibility.Visible))
        {
            throw new InvalidOperationException(
                "The Trello mappings list must retain board, project, client, and list columns.");
        }
    }

    internal void VerifyBrandingForPreview()
    {
        if (!string.Equals(Title, "Log O'clock", StringComparison.Ordinal) ||
            !string.Equals(AboutProductText.Text, "Log O'clock", StringComparison.Ordinal) ||
            !string.Equals(AboutAuthorText.Text, "Created by Zachariasz Jędrzejczyk", StringComparison.Ordinal) ||
            AutostartCheck.Content?.ToString()?.Contains("Log O'clock", StringComparison.Ordinal) != true)
        {
            throw new InvalidOperationException("The Log O'clock product name or author attribution is incomplete.");
        }
    }

    internal void VerifyUpdateNoticeForPreview()
    {
        if (UpdateBellButton.Visibility != Visibility.Visible ||
            FloatingUpdateBellButton.Visibility != Visibility.Visible ||
            OpenUpdateReleaseButton.Visibility != Visibility.Visible ||
            !UpdateStatusText.Text.Contains("999.0.0", StringComparison.Ordinal) ||
            ToolTipService.GetInitialShowDelay(UpdateBellButton) != 1000 ||
            ToolTipService.GetInitialShowDelay(FloatingUpdateBellButton) != 1000)
        {
            throw new InvalidOperationException(
                "The available-update bell or Application update card is incomplete.");
        }

        var mainTabIndex = MainTabs.SelectedIndex;
        var settingsCategoryIndex = SettingsCategoryTabs.SelectedIndex;
        UpdateBell_Click(UpdateBellButton, new RoutedEventArgs());
        if (MainTabs.SelectedIndex != 3 || SettingsCategoryTabs.SelectedIndex != 4)
        {
            throw new InvalidOperationException(
                "The available-update bell did not open Settings > Application.");
        }

        MainTabs.SelectedIndex = mainTabIndex;
        SettingsCategoryTabs.SelectedIndex = settingsCategoryIndex;
    }

    internal void VerifySettingsCategoriesForPreview()
    {
        string[] expectedHeaders =
        [
            "Tracking",
            "Idle & sessions",
            "Targets",
            "Integrations",
            "Application",
        ];
        var tabs = SettingsCategoryTabs.Items.OfType<TabItem>().ToArray();
        if (tabs.Length != expectedHeaders.Length ||
            tabs.Where((tab, index) =>
                    !string.Equals(tab.Header as string, expectedHeaders[index], StringComparison.Ordinal))
                .Any() ||
            tabs.Any(tab => tab.Content is not ScrollViewer))
        {
            throw new InvalidOperationException(
                "Settings must retain its Tracking, Idle & sessions, Targets, Integrations, and Application categories.");
        }
    }

    internal async Task VerifyAutomaticRecognitionControlsForPreviewAsync()
    {
        if (AutomaticRecognitionToggle.Parent is not StackPanel titleIdentity ||
            titleIdentity.Children.IndexOf(AutomaticRecognitionToggle) !=
            titleIdentity.Children.IndexOf(ProfileButton) + 1 ||
            AutomaticRecognitionToggle.Content is not null ||
            AutomaticRecognitionToggle.Width != 44 ||
            AutomaticRecognitionToggle.Height != 32 ||
            !string.Equals(
                AutomationProperties.GetName(AutomaticRecognitionToggle),
                "Full automatic project tracking",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The full automatic toggle is not the unlabeled title-bar control beside the profile selector.");
        }

        AutomaticRecognitionToggle.ApplyTemplate();
        var track = AutomaticRecognitionToggle.Template.FindName(
            "Track",
            AutomaticRecognitionToggle) as Border
            ?? throw new InvalidOperationException("The automatic-mode title-bar toggle has no track.");

        try
        {
            await _controller.SetAutomaticRecognitionGraceMinutesAsync(3);
            await _controller.SetAutomaticRecognitionEnabledAsync(true);
            UpdateLayout();
            if (AutomaticRecognitionToggle.IsChecked != true ||
                !ReferenceEquals(track.Background, FindResource("SuccessBrush")) ||
                await _store.GetSettingAsync(AutomaticRecognitionSettings.EnabledKey) != "true" ||
                await _store.GetSettingAsync(AutomaticRecognitionSettings.GraceMinutesKey) != "3" ||
                AutomaticRecognitionToggle.ToolTip is not string tooltip ||
                !tooltip.Contains("3 minutes", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The title-bar automatic-mode toggle did not apply its green state, tooltip, or persisted settings.");
            }

            AutomaticRecognitionGraceMinutesText.Text = "4";
            await ApplyAutomaticRecognitionGraceMinutesAsync();
            if (_controller.AutomaticRecognitionGraceMinutes != 4 ||
                AutomaticRecognitionGraceValidationText.Visibility != Visibility.Collapsed)
            {
                throw new InvalidOperationException(
                    "The automatic recognition grace-period field did not apply a valid value.");
            }
        }
        finally
        {
            await _controller.SetAutomaticRecognitionEnabledAsync(false);
            await _controller.SetAutomaticRecognitionGraceMinutesAsync(
                AutomaticRecognitionSettings.DefaultGraceMinutes);
            UpdateLayout();
        }

        if (AutomaticRecognitionToggle.IsChecked != false ||
            !ReferenceEquals(track.Background, FindResource("StatePressedBrush")))
        {
            throw new InvalidOperationException(
                "The automatic-mode title-bar toggle did not restore its standard gray disabled state.");
        }
    }

    internal void VerifyHistoryDefaultMonthForPreview(int year, int month)
    {
        var expectedStart = new DateTime(year, month, 1);
        var expectedEnd = expectedStart.AddMonths(1).AddDays(-1);
        if (HistoryRangePicker.StartDate != expectedStart || HistoryRangePicker.EndDate != expectedEnd)
        {
            throw new InvalidOperationException(
                $"History opened {HistoryRangePicker.StartDate:d}–{HistoryRangePicker.EndDate:d} instead of the latest populated month {expectedStart:MMMM yyyy}.");
        }
    }

    internal void VerifyHistoryGlobalProjectSortingForPreview(
        IReadOnlyList<Guid> expectedProjectEntryIds,
        IReadOnlyList<Guid> expectedClientEntryIds)
    {
        try
        {
            VerifyHistoryColumnSortingForPreview(
                HistoryProjectColumn,
                expectedProjectEntryIds,
                "project");
            VerifyHistoryColumnSortingForPreview(
                HistoryClientColumn,
                expectedClientEntryIds,
                "client");

            ClearHistorySort();
            var restoredView = CollectionViewSource.GetDefaultView(HistoryGrid.ItemsSource);
            if (HistoryClearSortingButton.Visibility != Visibility.Collapsed ||
                restoredView.GroupDescriptions.Count != 1)
            {
                throw new InvalidOperationException(
                    "Clearing History sorting did not restore the day-grouped view.");
            }
        }
        finally
        {
            ClearHistorySort();
        }
    }

    private void VerifyHistoryColumnSortingForPreview(
        DataGridColumn column,
        IReadOnlyList<Guid> expectedEntryIds,
        string columnName)
    {
        var eventArgs = new DataGridSortingEventArgs(column);
        HistoryGrid_Sorting(HistoryGrid, eventArgs);
        var view = CollectionViewSource.GetDefaultView(HistoryGrid.ItemsSource);
        var actualEntryIds = view
            .Cast<TimeEntryRow>()
            .Select(row => row.Entry.Id)
            .ToArray();
        if (!eventArgs.Handled ||
            view.GroupDescriptions.Count != 0 ||
            !actualEntryIds.SequenceEqual(expectedEntryIds))
        {
            throw new InvalidOperationException(
                $"History {columnName} sorting did not order the complete visible result set.");
        }
    }

    internal void VerifyObjectInteractionContractForPreview()
    {
        var requiredControls = new FrameworkElement[]
        {
            TimerTaskCombo, TimerProjectCombo, TimerDescriptionText, TimerCallCheck,
            TimerStartTimePanel, TimerStartTimeText, ElapsedText, StartStopButton,
            MinimizeWindowButton, MaximizeWindowButton, CloseWindowButton,
            HistoryRangePicker, HistoryProjectCombo, HistoryTaskCombo, HistoryTagCombo,
            HistoryDescriptionFilterText, HistoryGrid,
            HistorySaveViewButton, HistoryClearSortingButton, HistoryColumnsButton,
            HistoryThisMonthButton, HistoryThisWeekButton, HistoryTodayButton,
            ClientsGrid, ProjectsGrid, TargetProjectCombo, CustomTargetsGrid, TaskProjectCombo, TasksGrid, TagsGrid, SoftwareProjectCombo, SoftwareGrid, RuleProjectCombo, RulesGrid,
            ReportRangePicker, ReportClientCombo, ReportProjectCombo, ReportTaskCombo,
            ReportThisMonthButton, ReportThisWeekButton, ReportTodayButton,
            ReportTagCombo, ReportPaidCombo, ReportChartTabs,
            ReportDonutImage, ReportClientDonutImage, ReportInclusiveDonutImage,
            ReportInclusiveLegendItems, ReportGrid, ReportTargetsList,
            ReportSaveViewButton, ReportColumnsButton,
            SidebarTargetsPanel, SidebarTargetsResizeThumb, TargetsGrid, FloatingTargetsGrid,
            SettingsCategoryTabs, RecognitionCheck, AutomaticRecognitionToggle,
            AutomaticRecognitionGraceMinutesText, AutomaticRecognitionGraceValidationText,
            SessionBehaviorCombo, SessionBehaviorDescriptionText,
            RecentEntryResumeMinutesText, RecentEntryResumeValidationText,
            BreakReminderMinutesText, BreakReminderValidationText,
            BreakReminderBottomRight, BreakReminderScreenCenter, BreakReminderMessagesPanel,
            BreakReminderBathroomMessageCheck, BreakReminderBreakMessageCheck,
            BreakReminderCoffeeMessageCheck, BreakReminderTeaMessageCheck,
            BreakReminderSnackMessageCheck, BreakReminderStandUpMessageCheck,
            BreakReminderLaundryMessageCheck, BreakReminderDinnerMessageCheck,
            BreakReminderEpisodeMessageCheck,
            CallsIdleProtectionCheck, VideoIdleProtectionCheck,
            IdleProtectionStatusText, IdleProtectionStatusDot, IdleProtectionPrivacyText,
            ExcludedSoftwareReviewMinutesText, ExcludedSoftwareReviewValidationText,
            AccumulatedAwayReviewMinutesText, AccumulatedAwayReviewValidationText,
            ShortIdleReportingMinutesText, ShortIdleReportingValidationText,
            TargetReviewNotificationCheck, TargetReviewSchedulePanel,
            TargetReviewMonday, TargetReviewFirstWeek,
            AutostartCheck, UpdateChecksEnabledCheck, UpdateInstalledVersionText, UpdateStatusText,
            CheckForUpdatesButton, OpenUpdateReleaseButton, UpdateBellButton, FloatingUpdateBellButton,
            DatabasePathText,
            TrelloConnectionText, TrelloSyncStatusText,
            TrelloConnectButton, TrelloSyncButton, TrelloDisconnectButton, TrelloMappingsGrid,
        };
        if (requiredControls.Any(control => string.IsNullOrWhiteSpace(control.Name)))
        {
            throw new InvalidOperationException("The redesigned shell is missing a required named feature control.");
        }

        VerifySettingsCategoriesForPreview();
        VerifyTrelloUiForPreview();

        foreach (var titleBarButton in new[]
                 {
                     MinimizeWindowButton,
                     MaximizeWindowButton,
                     CloseWindowButton,
                 })
        {
            titleBarButton.ApplyTemplate();
            if (titleBarButton.Template.FindName("StateLayer", titleBarButton) is null ||
                titleBarButton.Template.FindName("FocusRing", titleBarButton) is not null ||
                Math.Abs(titleBarButton.Width - 31d) > 0.01 ||
                Math.Abs(titleBarButton.Height - 31d) > 0.01)
            {
                throw new InvalidOperationException(
                    "Top-bar window controls must retain compact sizing, hover and pressed feedback, and no persistent focus outline.");
            }
        }

        VerifyInactiveSurfaceClearsTimerEditorFocusForPreview();

        if (HistoryRangePicker.StartDate is null || HistoryRangePicker.EndDate is null ||
            ReportRangePicker.StartDate is null || ReportRangePicker.EndDate is null)
        {
            throw new InvalidOperationException("History or Reports is missing its unified working date range.");
        }

        if (TryFindResource("HistoryGroupDurationConverter") is not HistoryGroupDurationConverter durationConverter)
        {
            throw new InvalidOperationException("History day groups are missing their duration summary.");
        }

        if (TryFindResource("DailyTargetCompleteBrush") is not SolidColorBrush dailyCompleteBrush ||
            dailyCompleteBrush.Color != Color.FromRgb(0x25, 0x7D, 0x57))
        {
            throw new InvalidOperationException("Reached daily targets are missing the requested #257D57 fill.");
        }

        var targetProject = new Project(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Daily completion preview",
            "#339CFF",
            DailyTargetHours: 1,
            WeeklyTargetHours: 10,
            MonthlyTargetHours: 40);
        var reachedTarget = new ProjectTargetRow(targetProject, "Preview client", 3600, 0, 0);
        var pendingTarget = new ProjectTargetRow(targetProject, "Preview client", 3599, 0, 0);
        if (!reachedTarget.IsDailyReached || pendingTarget.IsDailyReached)
        {
            throw new InvalidOperationException("Daily target completion does not activate at the exact target boundary.");
        }

        if (TryFindResource("SidebarTargetRowTemplate") is not DataTemplate targetTemplate)
        {
            throw new InvalidOperationException("The shared sidebar and Reports target template is missing.");
        }

        VerifySidebarTargetProgressRingForPreview();

        var completionTrigger = targetTemplate.Triggers
            .OfType<DataTrigger>()
            .FirstOrDefault(trigger =>
                trigger.Binding is Binding binding &&
                string.Equals(
                    binding.Path?.Path,
                    nameof(ProjectTargetRow.IsDailyReached),
                    StringComparison.Ordinal));
        var completionSetter = completionTrigger?.Setters
            .OfType<Setter>()
            .FirstOrDefault(setter =>
                string.Equals(setter.TargetName, "DailyTargetLine", StringComparison.Ordinal) &&
                setter.Property == Border.BackgroundProperty);
        var completionFill = completionSetter?.Value as SolidColorBrush;
        if (completionFill?.Color != dailyCompleteBrush.Color)
        {
            throw new InvalidOperationException("The shared target template does not apply the daily completion fill.");
        }

        var historyView = CollectionViewSource.GetDefaultView(HistoryGrid.ItemsSource);
        foreach (var group in historyView.Groups?.OfType<CollectionViewGroup>() ?? [])
        {
            var expectedSeconds = group.Items
                .OfType<TimeEntryRow>()
                .Sum(row => row.Entry.NetDurationSeconds(row.NowUtc));
            var expected = FormatDuration(TimeSpan.FromSeconds(expectedSeconds));
            var actual = durationConverter.Convert(
                group,
                typeof(string),
                parameter: null!,
                CultureInfo.InvariantCulture) as string;
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A History day-group duration does not match its visible entries.");
            }
        }

        var activeClientIds = _activeClients.Select(client => client.Id).ToHashSet();
        var activeProjectIds = _activeProjects.Select(project => project.Id).ToHashSet();
        var activeTaskIds = _activeTasks.Select(task => task.Id).ToHashSet();
        var activeTags = _tagDefinitions.Select(tag => tag.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (HistoryProjectCombo.Items.OfType<ProjectFilterOption>()
                .Any(option => option.ProjectId is { } id && !activeProjectIds.Contains(id)) ||
            ReportProjectCombo.Items.OfType<ProjectFilterOption>()
                .Any(option => option.ProjectId is { } id && !activeProjectIds.Contains(id)) ||
            TargetProjectCombo.Items.OfType<TargetProjectFilterOption>()
                .Any(option => option.ProjectId is { } id && !activeProjectIds.Contains(id)) ||
            TaskProjectCombo.Items.OfType<ProjectFilterOption>()
                .Any(option => option.ProjectId is { } id && !activeProjectIds.Contains(id)) ||
            HistoryTaskCombo.Items.OfType<TaskFilterOption>()
                .Any(option => option.TaskId is { } id && !activeTaskIds.Contains(id)) ||
            ReportTaskCombo.Items.OfType<TaskFilterOption>()
                .Any(option => option.TaskId is { } id && !activeTaskIds.Contains(id)) ||
            ReportClientCombo.Items.OfType<ClientFilterOption>()
                .Any(option => option.ClientId is { } id && !activeClientIds.Contains(id)) ||
            HistoryTagCombo.Items.OfType<TagOption>()
                .Any(option => option.Value is { } tag && !activeTags.Contains(tag)) ||
            ReportTagCombo.Items.OfType<TagOption>()
                .Any(option => option.Value is { } tag && !activeTags.Contains(tag)))
        {
            throw new InvalidOperationException("Removed objects are still present in a selectable filter.");
        }

        if (ReportGrid.ItemTemplate is null || ReportGrid.ItemContainerStyle is null)
        {
            throw new InvalidOperationException("Report project groups are not using the section-header and task-summary layout.");
        }

        if (ReportColumnsButton.Content is not System.Windows.Shapes.Path ||
            GetReportColumnsSubmenu()?.Items.Count != ReportColumnDefinitions.Length ||
            ReportColumnsButton.ContextMenu?.Items.OfType<MenuItem>()
                .All(item => !string.Equals(item.Header as string, "Restore default", StringComparison.Ordinal)) != false)
        {
            throw new InvalidOperationException(
                "Reports is missing its column visibility and restore controls.");
        }

        var ruleView = CollectionViewSource.GetDefaultView(RulesGrid.ItemsSource);
        if (ruleView.GroupDescriptions.Count != 1 ||
            ruleView.GroupDescriptions[0] is not PropertyGroupDescription ruleGrouping ||
            !string.Equals(ruleGrouping.PropertyName, nameof(RuleRow.ProjectGroup), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Window rules are not grouped by project.");
        }

        if (RulesGrid.Columns.Count != 2 ||
            Math.Abs(RulesGrid.Columns[0].MinWidth - 360d) > 0.01 ||
            RulesGrid.Columns[1].MinWidth > 0.01)
        {
            throw new InvalidOperationException(
                "Window Rules must use equal flexible widths without forcing Application beyond the viewport.");
        }

        if (TagsGrid.ContextMenu?.Items.OfType<MenuItem>()
                .All(item => !string.Equals(item.Header as string, "Remove", StringComparison.Ordinal)) != false)
        {
            throw new InvalidOperationException("Tags is missing its Remove context-menu action.");
        }

        if (TagsGrid.ContextMenu?.Items.OfType<MenuItem>()
                .All(item => !string.Equals(item.Header as string, "Add tag…", StringComparison.Ordinal)) != false ||
            TagsGrid.Columns.All(column => !string.Equals(column.Header as string, "Project", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Tags is missing project/global scope management controls.");
        }

        if (SoftwareGrid.ContextMenu?.Items.OfType<MenuItem>()
                .All(item => !string.Equals(item.Header as string, "Edit…", StringComparison.Ordinal)) != false)
        {
            throw new InvalidOperationException("Software is missing its edit action.");
        }

        if (SoftwareGrid.ContextMenu?.Items.OfType<MenuItem>()
                .All(item => !string.Equals(item.Header as string, "Remove from list", StringComparison.Ordinal)) != false)
        {
            throw new InvalidOperationException("Software is missing its Remove from list action.");
        }

        if (new[] { ProjectsGrid, TasksGrid, TagsGrid, RulesGrid }
            .Any(grid => grid.SelectionMode != DataGridSelectionMode.Extended))
        {
            throw new InvalidOperationException("Projects, tasks, tags, and window rules must support extended multi-selection.");
        }

        foreach (var grid in new[] { ProjectsGrid, TasksGrid, TagsGrid, RulesGrid })
        {
            if (grid.ContextMenu?.Items.OfType<MenuItem>()
                    .All(item => !string.Equals(item.Header as string, "Edit…", StringComparison.Ordinal)) != false)
            {
                throw new InvalidOperationException($"{grid.Name} is missing its bulk Edit context-menu action.");
            }
        }

        foreach (var list in new ItemsControl[]
                  {
                      HistoryGrid, ClientsGrid, ProjectsGrid, CustomTargetsGrid, TasksGrid, TagsGrid, SoftwareGrid, RulesGrid,
                     ReportGrid, ReportTargetsList, TargetsGrid, FloatingTargetsGrid,
                 })
        {
            if (!SmoothScrollBehavior.GetIsEnabled(list) ||
                VirtualizingPanel.GetScrollUnit(list) != ScrollUnit.Pixel ||
                !VirtualizingPanel.GetIsVirtualizing(list) ||
                !VirtualizingPanel.GetIsVirtualizingWhenGrouping(list))
            {
                throw new InvalidOperationException($"{list.Name} is not configured for virtualized smooth pixel scrolling.");
            }

            if (ScrollViewer.GetHorizontalScrollBarVisibility(list) != ScrollBarVisibility.Auto)
            {
                throw new InvalidOperationException($"{list.Name} is not configured for horizontal overflow scrolling.");
            }
        }

        var positiveWheelWord = (nint)(120L << 16);
        var negativeWheelWord = (nint)(unchecked((ushort)-120) << 16);
        if (SmoothScrollBehavior.GetHorizontalWheelDeltaForPreview(positiveWheelWord) != 120 ||
            SmoothScrollBehavior.GetHorizontalWheelDeltaForPreview(negativeWheelWord) != -120)
        {
            throw new InvalidOperationException("Windows horizontal wheel direction is not decoded correctly.");
        }

        if (!SmoothScrollBehavior.HasHorizontalWheelHookForPreview(HistoryGrid))
        {
            throw new InvalidOperationException("The horizontal touchpad message bridge is not attached to the live app window.");
        }

        var horizontalPreviewColumn = HistoryGrid.Columns[^1];
        var originalPreviewWidth = horizontalPreviewColumn.Width;
        try
        {
            horizontalPreviewColumn.Width = new DataGridLength(1200);
            HistoryGrid.UpdateLayout();
            if (HistoryGrid.Template.FindName("DG_ScrollViewer", HistoryGrid) is not ScrollViewer horizontalPreview)
            {
                throw new InvalidOperationException("History is missing its horizontal scrolling surface.");
            }

            horizontalPreview.ScrollToLeftEnd();
            horizontalPreview.UpdateLayout();
            var movedHorizontally = SmoothScrollBehavior.ScrollHorizontalForPreview(horizontalPreview, 120);
            horizontalPreview.UpdateLayout();
            if (horizontalPreview.ScrollableWidth <= 0 ||
                !movedHorizontally ||
                horizontalPreview.HorizontalOffset <= 0)
            {
                throw new InvalidOperationException("Horizontal touchpad input does not move a wide list by pixel offset.");
            }

            horizontalPreview.ScrollToRightEnd();
            horizontalPreview.UpdateLayout();
            if (SmoothScrollBehavior.ScrollHorizontalForPreview(horizontalPreview, 120) ||
                !SmoothScrollBehavior.ScrollHorizontalForPreview(horizontalPreview, -120))
            {
                throw new InvalidOperationException("Horizontal scrolling does not hand off correctly at a list edge.");
            }
        }
        finally
        {
            horizontalPreviewColumn.Width = originalPreviewWidth;
            HistoryGrid.UpdateLayout();
        }

        if (Grid.GetRow(SidebarTargetsPanel) != 2 ||
            SidebarTargetsPanel.VerticalAlignment != VerticalAlignment.Bottom ||
            TargetsGrid.ItemTemplate is null ||
            FloatingTargetsGrid.ItemTemplate is null ||
            ReportTargetsList.ItemTemplate is null ||
             !ReferenceEquals(TargetsGrid.ItemTemplate, FloatingTargetsGrid.ItemTemplate) ||
             !ReferenceEquals(TargetsGrid.ItemTemplate, ReportTargetsList.ItemTemplate) ||
             !ReferenceEquals(TargetsGrid.ItemsSource, FloatingTargetsGrid.ItemsSource) ||
             ReferenceEquals(TargetsGrid.ItemsSource, ReportTargetsList.ItemsSource))
         {
             throw new InvalidOperationException(
                 "Targets must share the sidebar source while Reports keeps its independent monthly-only view.");
         }

        VerifySidebarTargetSelectionStyleForPreview();

        VerifyRowOrEmptyMenu(HistoryGrid.ContextMenu, "EntryOnly", "History");
        if (HistoryGrid.ContextMenu?.Items.OfType<MenuItem>()
                .All(item => !string.Equals(item.Header as string, "Continue", StringComparison.Ordinal)) != false)
        {
            throw new InvalidOperationException("History is missing its Continue context-menu action.");
        }

        VerifyRowOrEmptyMenu(ClientsGrid.ContextMenu, "ClientOnly", "Clients");
        VerifyRowOrEmptyMenu(ProjectsGrid.ContextMenu, "ProjectOnly", "Projects");
        VerifyProjectFreezeContextMenuForPreview();
        VerifyRowOrEmptyMenu(CustomTargetsGrid.ContextMenu, "CustomTargetOnly", "Targets");
        VerifyTargetContextMenuLabelsForPreview();
        VerifyRowOrEmptyMenu(TasksGrid.ContextMenu, "TaskOnly", "Tasks");
        VerifyRowOrEmptyMenu(SoftwareGrid.ContextMenu, "SoftwareOnly", "Software");
        VerifyRowOrEmptyMenu(RulesGrid.ContextMenu, "RuleOnly", "Window rules");

        if (ClientsGrid.Parent is not Grid { Children.Count: 1 })
        {
            throw new InvalidOperationException("Clients still has controls below or beside its object list.");
        }

        if (TagsGrid.Parent is not Grid tagsHost ||
            tagsHost.Children.Count != 2 ||
            Grid.GetRow(TagsGrid) != 0 ||
            Grid.GetRow(FreezedTagsExpander) != 1)
        {
            throw new InvalidOperationException("Tags is missing its folded frozen-project section.");
        }

        if (ProjectsGrid.Parent is not Grid projectHost ||
            projectHost.Children.Count != 2 ||
            Grid.GetRow(ProjectsGrid) != 0 ||
            Grid.GetRow(FreezedProjectsExpander) != 1)
        {
            throw new InvalidOperationException("Projects is missing its folded frozen-project section.");
        }

        if (TasksGrid.Parent is not Grid taskHost ||
            taskHost.Children.Count != 3 ||
            Grid.GetRow(TaskProjectCombo.Parent as UIElement ?? TaskProjectCombo) != 0 ||
            Grid.GetRow(TasksGrid) != 1 ||
            Grid.GetRow(FreezedTasksExpander) != 2)
        {
            throw new InvalidOperationException("Tasks is missing its project filter above the object list.");
        }

        if (CustomTargetsGrid.Parent is not Grid targetHost ||
            targetHost.Children.Count != 3 ||
            Grid.GetRow(TargetProjectCombo.Parent as UIElement ?? TargetProjectCombo) != 0 ||
            Grid.GetRow(CustomTargetsGrid) != 1 ||
            Grid.GetRow(FreezedTargetsExpander) != 2)
        {
            throw new InvalidOperationException("Targets is missing its project filter above the object list.");
        }

        if (SoftwareGrid.Parent is not Grid softwareHost ||
            softwareHost.Children.Count != 3 ||
            Grid.GetRow(SoftwareProjectCombo.Parent as UIElement ?? SoftwareProjectCombo) != 0 ||
            Grid.GetRow(SoftwareGrid) != 1 ||
            Grid.GetRow(FreezedSoftwareExpander) != 2)
        {
            throw new InvalidOperationException("Software is missing its project filter above the object list.");
        }

        if (RulesGrid.Parent is not Grid rulesHost ||
            rulesHost.Children.Count != 3 ||
            Grid.GetRow(RuleProjectCombo.Parent as UIElement ?? RuleProjectCombo) != 0 ||
            Grid.GetRow(RulesGrid) != 1 ||
            Grid.GetRow(FreezedRulesExpander) != 2)
        {
            throw new InvalidOperationException("Window rules are missing their project filter above the grouped list.");
        }

        if (HistoryGrid.Parent is not Grid historyHost ||
            Grid.GetRow(HistoryGrid) != historyHost.RowDefinitions.Count - 1 ||
            historyHost.Children.Cast<UIElement>()
                .Any(child => !ReferenceEquals(child, HistoryGrid) && Grid.GetRow(child) >= Grid.GetRow(HistoryGrid)))
        {
            throw new InvalidOperationException("History still has a bottom object-action bar.");
        }
    }

    internal async Task VerifySidebarTargetsPanelResizeForPreviewAsync()
    {
        if (SidebarTargetsResizeThumb.Cursor != Cursors.SizeNS ||
            !string.Equals(
                AutomationProperties.GetName(SidebarTargetsResizeThumb),
                "Resize sidebar targets panel",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The sidebar target panel is missing its accessible top-edge resize handle.");
        }

        _sidebarTargetsPanelPreferredHeight = 200;
        ApplySidebarTargetsPanelHeight();
        var initialHeight = SidebarTargetsPanel.Height;
        ResizeSidebarTargetsPanel(-48);
        var expandedHeight = SidebarTargetsPanel.Height;
        ResizeSidebarTargetsPanel(72);
        var squeezedHeight = SidebarTargetsPanel.Height;
        if (expandedHeight <= initialHeight ||
            squeezedHeight >= expandedHeight ||
            squeezedHeight < SidebarTargetsPanelSettings.MinimumHeight)
        {
            throw new InvalidOperationException(
                "Dragging the sidebar target panel edge does not expand and squeeze its height.");
        }

        await SaveSidebarTargetsPanelHeightAsync();
        var stored = await _store.GetSettingAsync(SidebarTargetsPanelSettings.HeightKey);
        if (SidebarTargetsPanelSettings.ParseHeight(stored) !=
            (int)Math.Round(_sidebarTargetsPanelPreferredHeight))
        {
            throw new InvalidOperationException(
                "The resized sidebar target panel height was not persisted.");
        }

        _sidebarTargetsPanelPreferredHeight = SidebarTargetsPanelSettings.DefaultHeight;
        ApplySidebarTargetsPanelHeight();
        await SaveSidebarTargetsPanelHeightAsync();
    }

    private void VerifyTargetContextMenuLabelsForPreview()
    {
        var targetMenuHeaders = CustomTargetsGrid.ContextMenu?.Items
            .OfType<MenuItem>()
            .Select(item => item.Header as string)
            .ToArray() ?? [];
        if (!targetMenuHeaders.Contains("Add target...", StringComparer.Ordinal) ||
            !targetMenuHeaders.Contains("Edit...", StringComparer.Ordinal) ||
            !targetMenuHeaders.Contains("Lower debt...", StringComparer.Ordinal) ||
            !targetMenuHeaders.Contains("Cancel debt", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Targets context-menu labels are missing or contain corrupted punctuation.");
        }
    }

    internal void VerifySidebarTargetSelectionStyleForPreview()
    {
        var sidebarTargetTemplate = TargetsGrid.ItemContainerStyle?.Setters
            .OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == Control.TemplateProperty)
            ?.Value as ControlTemplate;
        if (sidebarTargetTemplate is null ||
            sidebarTargetTemplate.Triggers.OfType<Trigger>().Any(trigger =>
                trigger.Property == ListBoxItem.IsSelectedProperty ||
                trigger.Property == ListBoxItem.IsMouseOverProperty) ||
            !ReferenceEquals(TargetsGrid.ItemContainerStyle, FloatingTargetsGrid.ItemContainerStyle) ||
            ReferenceEquals(TargetsGrid.ItemContainerStyle, ReportTargetsList.ItemContainerStyle))
        {
            throw new InvalidOperationException(
                "Sidebar targets still expose a hover or persistent selection indicator, or changed the Reports target list style.");
        }

        var reportTargetStyle = ReportTargetsList.ItemContainerStyle;
        var reportTargetTemplate = reportTargetStyle?.Setters
            .OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == Control.TemplateProperty)
            ?.Value as ControlTemplate;
        var reportFocusVisual = reportTargetStyle?.Setters
            .OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == Control.FocusVisualStyleProperty)
            ?.Value;
        if (reportTargetTemplate is null ||
            reportFocusVisual is not null ||
            reportTargetTemplate.Triggers.OfType<Trigger>().Any(trigger =>
                trigger.Property == ListBoxItem.IsSelectedProperty ||
                trigger.Property == ListBoxItem.IsMouseOverProperty ||
                trigger.Property == ListBoxItem.IsKeyboardFocusWithinProperty))
        {
            throw new InvalidOperationException(
                "Reports targets still expose a hover, selection, or keyboard-focus indicator.");
        }
    }

    internal void VerifySidebarTargetProgressRingForPreview()
    {
        if (TryFindResource("TargetDailyBrush") is not SolidColorBrush targetDailyBrush ||
            targetDailyBrush.Color != Color.FromRgb(0xFB, 0x6A, 0x22) ||
            TryFindResource("TargetWeeklyBrush") is not SolidColorBrush targetWeeklyBrush ||
            targetWeeklyBrush.Color != Color.FromRgb(0xAD, 0x7B, 0xF9) ||
            TryFindResource("TargetMonthlyBrush") is not SolidColorBrush targetMonthlyBrush ||
            targetMonthlyBrush.Color != Color.FromRgb(0x40, 0xC9, 0x77) ||
            TryFindResource("SidebarTargetRowTemplate") is not DataTemplate targetTemplate ||
            targetTemplate.LoadContent() is not FrameworkElement targetPreview ||
            FindVisualDescendants<TargetProgressRing>(targetPreview).SingleOrDefault() is not { } targetRing ||
            FindVisualDescendants<TextBlock>(targetPreview).SingleOrDefault(text =>
                string.Equals(text.Name, "DailyTargetMarker", StringComparison.Ordinal)) is not
                { Foreground: SolidColorBrush dailyMarker } ||
            FindVisualDescendants<TextBlock>(targetPreview).SingleOrDefault(text =>
                string.Equals(text.Name, "WeeklyTargetMarker", StringComparison.Ordinal)) is not
                { Foreground: SolidColorBrush weeklyMarker } ||
            FindVisualDescendants<TextBlock>(targetPreview).SingleOrDefault(text =>
                string.Equals(text.Name, "MonthlyTargetMarker", StringComparison.Ordinal)) is not
                { Foreground: SolidColorBrush monthlyMarker } ||
            dailyMarker.Color != targetDailyBrush.Color ||
            weeklyMarker.Color != targetWeeklyBrush.Color ||
            monthlyMarker.Color != targetMonthlyBrush.Color ||
            targetRing.DailyBrush is not SolidColorBrush ringDaily ||
            targetRing.WeeklyBrush is not SolidColorBrush ringWeekly ||
            targetRing.MonthlyBrush is not SolidColorBrush ringMonthly ||
            ringDaily.Color != targetDailyBrush.Color ||
            ringWeekly.Color != targetWeeklyBrush.Color ||
            ringMonthly.Color != targetMonthlyBrush.Color)
        {
            throw new InvalidOperationException(
                "The sidebar target ring and D/W/M letters do not share orange, purple, and bright-green period colors.");
        }

        var targetProject = new Project(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Progress ring preview",
            "#339CFF",
            DailyTargetHours: 1,
            WeeklyTargetHours: 10,
            MonthlyTargetHours: 40);
        var progressPreview = new ProjectTargetRow(
            targetProject,
            "Preview client",
            DailySeconds: 1800,
            WeeklySeconds: 3600,
            MonthlySeconds: 7200);
        targetRing.DailyProgress = progressPreview.DailyProgress;
        targetRing.WeeklyProgress = progressPreview.WeeklyProgress;
        targetRing.MonthlyProgress = progressPreview.MonthlyProgress;
        if (Math.Abs(progressPreview.DailyProgress - 0.5d) > 0.0001d ||
            Math.Abs(progressPreview.WeeklyProgress - 0.1d) > 0.0001d ||
            Math.Abs(progressPreview.MonthlyProgress - 0.05d) > 0.0001d ||
            !targetRing.OrderedPeriodsForPreview.SequenceEqual(
                [TargetProgressPeriod.Daily, TargetProgressPeriod.Weekly, TargetProgressPeriod.Monthly]) ||
            !string.Equals(TargetsGrid.Tag as string, "SidebarTargetRing", StringComparison.Ordinal) ||
            !string.Equals(FloatingTargetsGrid.Tag as string, "SidebarTargetRing", StringComparison.Ordinal) ||
            ReportTargetsList.Tag is not null)
        {
            throw new InvalidOperationException(
                "Target progress is not layered longest-first with the shortest arc on top in sidebars only.");
        }
    }

    internal void VerifyUnifiedTargetViewsForPreview(
        Guid projectId,
        Guid otherProjectId,
        Guid scopedMonthlyTargetId,
        Guid globalMonthlyTargetId,
        Guid scopedOneTimeTargetId,
        Guid expiredOneTimeTargetId)
    {
        VerifyTargetContextMenuLabelsForPreview();
        var sidebarTargets = TargetsGrid.ItemsSource?.OfType<ProjectTargetRow>().ToArray() ?? [];
        var managementTargets = CustomTargetsGrid.ItemsSource?.OfType<ITargetManagementRow>().ToArray() ?? [];
        var scopedSidebarTarget = sidebarTargets
            .SingleOrDefault(row => row.ScopedProjectId == projectId);
        var otherSidebarTarget = sidebarTargets
            .SingleOrDefault(row => row.ScopedProjectId == otherProjectId);
        var globalSidebarTarget = sidebarTargets
            .SingleOrDefault(row => row.IsGlobalAggregate);
        if (!ReferenceEquals(TargetsGrid.ItemsSource, FloatingTargetsGrid.ItemsSource) ||
            ReferenceEquals(TargetsGrid.ItemsSource, ReportTargetsList.ItemsSource) ||
            sidebarTargets.Length != 3 ||
            sidebarTargets.Any(row => row.CustomTarget is not null) ||
            scopedSidebarTarget is not
            {
                DailyTargetHours: 1,
                WeeklyTargetHours: 10,
                MonthlyTargetHours: 52,
                OneTimeTargetHours: 4,
            } ||
            otherSidebarTarget is not { MonthlyTargetHours: 20 } ||
            globalSidebarTarget is not
            {
                MonthlyTargetHours: 160,
                ScopedProjectId: null,
            } ||
            managementTargets.OfType<CustomTargetRow>()
                .Count(row => row.Target.ProjectId == projectId &&
                    row.Target.Id != scopedMonthlyTargetId &&
                    row.Target.Id != scopedOneTimeTargetId) < 3 ||
            !managementTargets.OfType<CustomTargetRow>()
                .Any(row => row.Target.Id == scopedMonthlyTargetId) ||
            !managementTargets.OfType<CustomTargetRow>()
                .Any(row => row.Target.Id == globalMonthlyTargetId) ||
            !managementTargets.OfType<CustomTargetRow>()
                .Any(row => row.Target.Id == scopedOneTimeTargetId) ||
            managementTargets.OfType<CustomTargetRow>()
                .Any(row => row.Target.Id == expiredOneTimeTargetId) ||
            CustomTargetsGrid.Columns.Any(column =>
                string.Equals(column.Header?.ToString(), "Date", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The Targets tab does not expose every record or the sidebars do not aggregate them once per project.");
        }

        var projectOption = TargetProjectCombo.Items
            .OfType<TargetProjectFilterOption>()
            .FirstOrDefault(option => option.ProjectId == projectId && !option.IsGlobal);
        var globalOption = TargetProjectCombo.Items
            .OfType<TargetProjectFilterOption>()
            .FirstOrDefault(option => option.IsGlobal);
        if (projectOption is null ||
            globalOption is null ||
            !projectOption.DisplayName.Contains(" \u00B7 ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Targets project filter is missing its project/client or global choices.");
        }

        _updatingTargetFilter = true;
        try
        {
            TargetProjectCombo.SelectedItem = projectOption;
            _targetProjectFilterId = projectId;
            _targetGlobalOnly = false;
        }
        finally
        {
            _updatingTargetFilter = false;
        }

        ApplyTargetFilter();
        var projectTargets = CustomTargetsGrid.Items.OfType<ITargetManagementRow>().ToArray();
        if (projectTargets.Length == 0 ||
            projectTargets.Any(row => GetTargetProjectId(row) != projectId))
        {
            throw new InvalidOperationException(
                "The Targets project filter did not isolate the selected project.");
        }

        _updatingTargetFilter = true;
        try
        {
            TargetProjectCombo.SelectedItem = globalOption;
            _targetProjectFilterId = null;
            _targetGlobalOnly = true;
        }
        finally
        {
            _updatingTargetFilter = false;
        }

        ApplyTargetFilter();
        var globalTargets = CustomTargetsGrid.Items.OfType<ITargetManagementRow>().ToArray();
        if (!globalTargets.OfType<CustomTargetRow>()
                .Any(row => row.Target.Id == globalMonthlyTargetId) ||
            globalTargets.Any(row => GetTargetProjectId(row) is not null))
        {
            throw new InvalidOperationException(
                "The Targets project filter did not isolate global targets.");
        }

        ResetTargetProjectFilter();
        if (TargetProjectCombo.SelectedIndex != 0 ||
            CustomTargetsGrid.Items.OfType<ITargetManagementRow>().Count() != managementTargets.Length)
        {
            throw new InvalidOperationException(
                "The Targets project filter did not reset to All projects.");
        }

        var reportProject = ReportGrid.ItemsSource?
            .OfType<ProjectReportSummaryRow>()
            .SingleOrDefault(row => row.ProjectId == projectId)
            ?? throw new InvalidOperationException(
                "The unified-target smoke project is missing from Reports.");
        ReportGrid.SelectedItem = reportProject;
        UpdateReportTargetsList();
        var reportTargets = ReportTargetsList.ItemsSource?.OfType<ProjectTargetRow>().ToArray() ?? [];
        if (reportTargets.Length != 4 ||
            reportTargets.Any(row => !row.HasMonthlyTarget ||
                row.DailyTargetHours is not null ||
                row.WeeklyTargetHours is not null ||
                row.OneTimeTargetHours is not null) ||
            reportTargets.Any(row => row.CustomTarget is null) ||
            !reportTargets.Any(row => row.ScopedProjectId == projectId &&
                row.CustomTarget?.Id != scopedMonthlyTargetId) ||
            !reportTargets.Any(row => row.ScopedProjectId == otherProjectId) ||
            !reportTargets.Any(row => row.CustomTarget?.Id == scopedMonthlyTargetId) ||
            !reportTargets.Any(row => row.CustomTarget?.Id == globalMonthlyTargetId &&
                row.ScopedProjectId is null) ||
            reportTargets.Any(row => row.CustomTarget?.Id == scopedOneTimeTargetId))
        {
            throw new InvalidOperationException(
                "Reports does not show every configured, scoped, and global monthly target.");
        }

        ReportTargetsList.SelectedItem = reportTargets[0];
        if (!ReferenceEquals(ReportTargetsList.SelectedItem, reportTargets[0]))
        {
            throw new InvalidOperationException(
                "Reports target selection no longer remains available to edit and context-menu commands.");
        }
    }

    internal async Task VerifyHistoryViewForPreviewAsync()
    {
        var columnsSubmenu = GetHistoryColumnsSubmenu();
        var headerMenu = HistoryGrid.FindResource("HistoryColumnHeaderMenu") as ContextMenu;
        if (HistoryGrid.RowStyle is null ||
            HistoryGrid.CellStyle is null ||
            HistoryColumnsButton.Content is not System.Windows.Shapes.Path ||
            HistoryColumnsButton.ContextMenu?.Items.Count != 5 ||
            columnsSubmenu?.Items.Count != HistoryGrid.Columns.Count ||
            !string.Equals(HistoryWrapTextMenuItem.Header as string, "Wrap text", StringComparison.Ordinal) ||
            !HistoryWrapTextMenuItem.IsCheckable ||
            HistoryColumnsButton.ContextMenu.Items.OfType<MenuItem>()
                .All(item => !string.Equals(item.Header as string, "Restore default", StringComparison.Ordinal)) ||
            headerMenu?.Items.OfType<MenuItem>()
                .All(item => !string.Equals(item.Header as string, "Hide column", StringComparison.Ordinal)) != false ||
            HistorySaveViewButton.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException(
                "History is missing passive selection styling, its settings menu, wrap-text option, column submenu, header Hide action, or initially hidden save action.");
        }

        ApplyHistoryView(_defaultHistoryView);
        HistoryStatusColumn.DisplayIndex = 0;
        HistoryStatusColumn.Width = new DataGridLength(144);
        HideHistoryColumn(HistorySoftwareColumn);
        SetHistoryTextWrapping(true);
        if (HistorySoftwareColumn.Visibility != Visibility.Collapsed ||
            HistoryStatusColumn.DisplayIndex != 0 ||
            Math.Abs(HistoryStatusColumn.Width.Value - 144) > 0.01 ||
            !GetHistoryTextWrapping(HistoryGrid) ||
            !HistoryWrapTextMenuItem.IsChecked ||
            !double.IsNaN(HistoryGrid.RowHeight) ||
            HistorySaveViewButton.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException(
                "Changing the History layout or enabling text wrapping did not reveal Save Current View.");
        }

        await SaveHistoryViewAsync();
        var savedJson = await _store.GetSettingAsync(HistoryViewSettingKey);
        if (string.IsNullOrWhiteSpace(savedJson) ||
            HistorySaveViewButton.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException(
                "The hidden History column view was not saved.");
        }

        RestoreDefaultHistoryView();
        if (HistorySoftwareColumn.Visibility != Visibility.Visible ||
            HistoryStatusColumn.DisplayIndex == 0 ||
            Math.Abs(HistoryStatusColumn.Width.Value - 90) > 0.01 ||
            GetHistoryTextWrapping(HistoryGrid) ||
            HistoryWrapTextMenuItem.IsChecked ||
            double.IsNaN(HistoryGrid.RowHeight) ||
            HistorySaveViewButton.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException(
                "Restore default did not restore the compact default History layout as an unsaved view.");
        }

        await LoadHistoryViewAsync();
        if (HistorySoftwareColumn.Visibility != Visibility.Collapsed ||
            HistoryStatusColumn.DisplayIndex != 0 ||
            Math.Abs(HistoryStatusColumn.Width.Value - 144) > 0.01 ||
            !GetHistoryTextWrapping(HistoryGrid) ||
            !HistoryWrapTextMenuItem.IsChecked ||
            !double.IsNaN(HistoryGrid.RowHeight) ||
            HistorySaveViewButton.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException(
                "The saved History layout and text-wrapping preference were not restored.");
        }

        ApplyHistoryView(_defaultHistoryView);
        await SaveHistoryViewAsync();
    }

    internal async Task VerifyReportViewForPreviewAsync()
    {
        if (!ReportGrid.Items.Cast<object>().Any())
        {
            var client = await _store.AddClientAsync(
                $"Report view client {Guid.NewGuid():N}",
                "#687582");
            var project = await _store.AddProjectAsync(
                client.Id,
                $"Report view project {Guid.NewGuid():N}",
                "#339CFF");
            var task = await _store.AddTaskAsync(project.Id, "Resizable report task");
            var reportEndUtc = _controller.UtcNow;
            var currentMonth = TrackingPeriodCalculator.CurrentMonth(
                reportEndUtc,
                TimeZoneInfo.Local);
            var reportStartUtc = reportEndUtc.AddMinutes(-30);
            if (reportStartUtc < currentMonth.StartUtc)
            {
                reportStartUtc = currentMonth.StartUtc;
                reportEndUtc = reportStartUtc.AddMinutes(30);
            }

            await _store.AddManualEntryAsync(
                project.Id,
                task.Id,
                "Reports saved-view smoke test",
                reportStartUtc,
                reportEndUtc);
            await RefreshReportAsync();
        }

        if (ReportVisualsGrid.Parent is not Grid reportLayout ||
            reportLayout.RowDefinitions.Count != 4 ||
            Grid.GetRow(ReportVisualsGrid) != reportLayout.RowDefinitions.Count - 1 ||
            reportLayout.Children.Cast<UIElement>()
                .Any(child => Grid.GetRow(child) >= reportLayout.RowDefinitions.Count))
        {
            throw new InvalidOperationException(
                "Reports still reserves a bottom row for the removed selected/value/unpaid summary.");
        }

        ReportGrid.UpdateLayout();
        VerifyReportTaskGroupsForPreview();
        ExpandAllReportProjectTasksForPreview();
        var taskGrids = FindVisualDescendants<DataGrid>(ReportGrid).ToArray();
        var summaryGrids = FindVisualDescendants<Grid>(ReportGrid)
            .Where(IsReportSummaryGrid)
            .ToArray();
        var columnsSubmenu = GetReportColumnsSubmenu();
        var headerMenu = taskGrids.FirstOrDefault()?
            .FindResource("ReportColumnHeaderMenu") as ContextMenu;
        if (taskGrids.Length == 0 ||
            summaryGrids.Length == 0 ||
            taskGrids.Any(grid => grid.Columns.All(column =>
                !string.Equals(column.Header as string, "Calls", StringComparison.Ordinal))) ||
            summaryGrids.Any(grid => grid.ColumnDefinitions.Count != ReportColumnDefinitions.Length) ||
            ReportColumnsButton.Content is not System.Windows.Shapes.Path ||
            ReportColumnsButton.ContextMenu?.Items.Count != 3 ||
            columnsSubmenu?.Items.Count != ReportColumnDefinitions.Length ||
            headerMenu?.Items.OfType<MenuItem>()
                .All(item => !string.Equals(item.Header as string, "Hide column", StringComparison.Ordinal)) != false ||
            ReportSaveViewButton.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException(
                "Reports is missing its settings menu, column submenu, header Hide action, or summary footer.");
        }

        _ = TrySetReportView(CreateDefaultReportView());
        var resizedTimeColumn = taskGrids[0].Columns.Single(column =>
            string.Equals(column.Header as string, "Time", StringComparison.Ordinal));
        resizedTimeColumn.Width = new DataGridLength(137);
        SynchronizeReportColumnWidthsFromGrid(taskGrids[0]);
        SetReportColumnVisibility("Value", isVisible: false);
        if (taskGrids.Any(grid => grid.Columns.Single(column =>
                    string.Equals(column.Header as string, "Value", StringComparison.Ordinal)).Visibility !=
                Visibility.Collapsed) ||
            taskGrids.Any(grid => Math.Abs(grid.Columns.Single(column =>
                    string.Equals(column.Header as string, "Time", StringComparison.Ordinal)).Width.Value - 137) > 0.01) ||
            summaryGrids.Any(grid => grid.ColumnDefinitions[4].Width.Value != 0) ||
            summaryGrids.Any(grid => Math.Abs(grid.ColumnDefinitions[1].Width.Value - 137) > 0.01) ||
            ReportSaveViewButton.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException(
                "Changing a Reports column width or visibility did not synchronize every project table and its total row.");
        }

        await SaveReportViewAsync();
        var savedJson = await _store.GetSettingAsync(ReportViewSettingKey);
        if (string.IsNullOrWhiteSpace(savedJson) ||
            ReportSaveViewButton.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("The hidden Reports column view was not saved.");
        }

        _ = TrySetReportView(CreateDefaultReportView());
        if (taskGrids.Any(grid => grid.Columns.Any(column =>
                    column.Visibility != Visibility.Visible)) ||
            taskGrids.Any(grid => Math.Abs(grid.Columns.Single(column =>
                    string.Equals(column.Header as string, "Time", StringComparison.Ordinal)).Width.Value - 82) > 0.01) ||
            summaryGrids.Any(grid => grid.ColumnDefinitions.Any(column => column.Width.Value <= 0)) ||
            summaryGrids.Any(grid => Math.Abs(grid.ColumnDefinitions[1].Width.Value - 82) > 0.01) ||
            ReportSaveViewButton.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException(
                "Restore default did not restore the Reports columns and widths as an unsaved view.");
        }

        await LoadReportViewAsync();
        if (taskGrids.Any(grid => grid.Columns.Single(column =>
                    string.Equals(column.Header as string, "Value", StringComparison.Ordinal)).Visibility !=
                Visibility.Collapsed) ||
            taskGrids.Any(grid => Math.Abs(grid.Columns.Single(column =>
                    string.Equals(column.Header as string, "Time", StringComparison.Ordinal)).Width.Value - 137) > 0.01) ||
            summaryGrids.Any(grid => Math.Abs(grid.ColumnDefinitions[1].Width.Value - 137) > 0.01) ||
            ReportSaveViewButton.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("The saved Reports column view and widths were not restored.");
        }

        _ = TrySetReportView(CreateDefaultReportView());
        await SaveReportViewAsync();
        CollapseAllReportProjectTasksForPreview();
    }

    internal void VerifyHistoryOverlapForPreview(
        IReadOnlyCollection<Guid> expectedOverlapIds,
        Guid expectedNonOverlapId)
    {
        var expected = expectedOverlapIds.ToHashSet();
        var markedRows = _historyRows
            .Where(row => expected.Contains(row.Entry.Id))
            .ToArray();
        if (markedRows.Length > 0)
        {
            HistoryGrid.SelectedItem = markedRows[0];
            HistoryGrid.ScrollIntoView(markedRows[0]);
        }

        HistoryGrid.UpdateLayout();
        var selectedContainer = markedRows.Length == 0
            ? null
            : HistoryGrid.ItemContainerGenerator.ContainerFromItem(markedRows[0]) as DataGridRow;
        var selectedCells = selectedContainer is null
            ? []
            : FindVisualDescendants<DataGridCell>(selectedContainer)
                .Where(cell => cell.IsSelected)
                .ToArray();
        var overlapGeometry = TryFindResource("Icon.TimeOverlap") as Geometry;
        var renderedIcons = overlapGeometry is null
            ? []
            : FindVisualDescendants<System.Windows.Shapes.Path>(HistoryGrid)
                .Where(path => ReferenceEquals(path.Data, overlapGeometry))
                .ToArray();
        var dayDurationTotals = FindVisualDescendants<TextBlock>(HistoryGrid)
            .Where(textBlock => string.Equals(
                textBlock.Tag as string,
                "HistoryDayDurationTotal",
                StringComparison.Ordinal))
            .ToArray();
        if (expected.Count < 2 ||
            markedRows.Length != expected.Count ||
            markedRows.Any(row => !row.HasTimeOverlap) ||
            HistoryGrid.SelectedItems.Count != 1 ||
            selectedContainer is null ||
            !IsTransparentBrush(selectedContainer.Background) ||
            selectedCells.Any(cell => !IsTransparentBrush(cell.Background)) ||
            _historyRows.FirstOrDefault(row => row.Entry.Id == expectedNonOverlapId) is not
                { HasTimeOverlap: false } ||
            dayDurationTotals.Length == 0 ||
            dayDurationTotals.Any(total => total.FontSize < 14d) ||
            overlapGeometry is null ||
            renderedIcons.Length == 0 ||
            renderedIcons.Any(path =>
                path.Width > 10d ||
                path.Height > 10d ||
                FindVisualAncestor<DataGridCell>(path)?.Column != HistoryDurationColumn))
        {
            throw new InvalidOperationException(
                "History did not render its passive selection, larger day total, or compact overlap indicator correctly.");
        }
    }

    private static bool IsTransparentBrush(Brush? brush) =>
        brush is null ||
        brush.Opacity <= 0d ||
        brush is SolidColorBrush { Color.A: 0 };

    internal void VerifyRemovedFilterOptionsForPreview(
        IReadOnlyCollection<Guid> removedClientIds,
        IReadOnlyCollection<Guid> removedProjectIds,
        IReadOnlyCollection<Guid> removedTaskIds,
        IReadOnlyCollection<Guid> removedRuleIds,
        IReadOnlyCollection<string> removedTags)
    {
        var clients = removedClientIds.ToHashSet();
        var projects = removedProjectIds.ToHashSet();
        var tasks = removedTaskIds.ToHashSet();
        var rules = removedRuleIds.ToHashSet();
        var tags = removedTags.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ReportClientCombo.Items.OfType<ClientFilterOption>()
                .Any(option => option.ClientId is { } id && clients.Contains(id)) ||
            HistoryProjectCombo.Items.OfType<ProjectFilterOption>()
                .Any(option => option.ProjectId is { } id && projects.Contains(id)) ||
            ReportProjectCombo.Items.OfType<ProjectFilterOption>()
                .Any(option => option.ProjectId is { } id && projects.Contains(id)) ||
            TargetProjectCombo.Items.OfType<TargetProjectFilterOption>()
                .Any(option => option.ProjectId is { } id && projects.Contains(id)) ||
            TaskProjectCombo.Items.OfType<ProjectFilterOption>()
                .Any(option => option.ProjectId is { } id && projects.Contains(id)) ||
            HistoryTaskCombo.Items.OfType<TaskFilterOption>()
                .Any(option => option.TaskId is { } id && tasks.Contains(id)) ||
            ReportTaskCombo.Items.OfType<TaskFilterOption>()
                .Any(option => option.TaskId is { } id && tasks.Contains(id)) ||
            HistoryTagCombo.Items.OfType<TagOption>()
                .Any(option => option.Value is { } tag && tags.Contains(tag)) ||
            ReportTagCombo.Items.OfType<TagOption>()
                .Any(option => option.Value is { } tag && tags.Contains(tag)) ||
            RulesGrid.Items.OfType<RuleRow>().Any(row => rules.Contains(row.Rule.Id)))
        {
            throw new InvalidOperationException("A removed client, project, task, tag, or rule remains selectable.");
        }

        if (!removedProjectIds.Any(projectId => _historyRows.Any(row => row.Entry.ProjectId == projectId)) &&
            !removedTaskIds.Any(taskId => _historyRows.Any(row => row.Entry.TaskId == taskId)))
        {
            throw new InvalidOperationException("The removed-filter smoke data is missing its retained historical entries.");
        }
    }

    internal void VerifyRuleGroupingForPreview(Guid projectId)
    {
        if (_ruleRows.Count == 0)
        {
            throw new InvalidOperationException("The Window Rules grouping smoke check has no rules.");
        }

        VerifyVisibleRuleGroups();
        ApplyRuleGridDefaultColumnWidths(RulesGrid);
        RulesGrid.UpdateLayout();
        if (RulesGrid.Columns.Count != 2 ||
            Math.Abs(RulesGrid.Columns[0].ActualWidth - RulesGrid.Columns[1].ActualWidth) > 1d)
        {
            throw new InvalidOperationException(
                "The Window Rules columns do not share the available width equally.");
        }

        var option = RuleProjectCombo.Items
            .OfType<ProjectFilterOption>()
            .FirstOrDefault(item => item.ProjectId == projectId)
            ?? throw new InvalidOperationException("The requested project is missing from the Window Rules filter.");

        _updatingRuleFilter = true;
        try
        {
            RuleProjectCombo.SelectedItem = option;
            _ruleProjectFilterId = projectId;
        }
        finally
        {
            _updatingRuleFilter = false;
        }

        ApplyRuleFilter();
        if (!RulesGrid.Items.OfType<RuleRow>().Any() ||
            RulesGrid.Items.OfType<RuleRow>().Any(row => row.ProjectId != projectId))
        {
            throw new InvalidOperationException("The Window Rules project filter did not isolate the selected project.");
        }

        VerifyVisibleRuleGroups();
        _updatingRuleFilter = true;
        try
        {
            RuleProjectCombo.SelectedIndex = 0;
            _ruleProjectFilterId = null;
        }
        finally
        {
            _updatingRuleFilter = false;
        }

        ApplyRuleFilter();
    }

    internal void VerifyTaskProjectFilterForPreview(Guid projectId, Guid expectedTaskId)
    {
        var option = TaskProjectCombo.Items
            .OfType<ProjectFilterOption>()
            .FirstOrDefault(item => item.ProjectId == projectId)
            ?? throw new InvalidOperationException(
                "The requested project is missing from the Tasks project filter.");
        var softwareOption = SoftwareProjectCombo.Items
            .OfType<ProjectFilterOption>()
            .FirstOrDefault(item => item.ProjectId == projectId)
            ?? throw new InvalidOperationException(
                "The requested project is missing from the Software project filter.");
        if (!string.Equals(option.DisplayName, softwareOption.DisplayName, StringComparison.Ordinal) ||
            !option.DisplayName.Contains(" \u00B7 ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Software project filter does not use the same middle-dot project/client separator as Tasks.");
        }

        _updatingTaskFilter = true;
        try
        {
            TaskProjectCombo.SelectedItem = option;
            _taskProjectFilterId = projectId;
        }
        finally
        {
            _updatingTaskFilter = false;
        }

        ApplyTaskFilter();
        var visible = TasksGrid.Items.OfType<TaskRow>().ToArray();
        if (!visible.Any(row => row.Task.Id == expectedTaskId) ||
            visible.Any(row => row.ProjectId != projectId))
        {
            throw new InvalidOperationException(
                "The Tasks project filter did not isolate the selected project's tasks.");
        }

        ResetTaskProjectFilter();
        if (TaskProjectCombo.SelectedIndex != 0 ||
            TasksGrid.Items.OfType<TaskRow>().Count() != _taskRows.Count)
        {
            throw new InvalidOperationException(
                "The Tasks project filter did not reset to All projects.");
        }
    }

    internal void VerifySoftwareForPreview(
        Guid entryId,
        Guid softwareId,
        Guid projectId,
        string expectedLabel,
        IReadOnlyCollection<string>? expectedTags = null,
        bool expectedExcluded = false)
    {
        var software = SoftwareGrid.Items.OfType<SoftwareRow>().FirstOrDefault(row =>
            row.Software.Id == softwareId &&
            row.ProjectId == projectId &&
            string.Equals(row.Label, expectedLabel, StringComparison.Ordinal) &&
            row.Setting.IsExcluded == expectedExcluded);
        if (software is null)
        {
            throw new InvalidOperationException(
                "The Software tab did not show the expected process label and tracking behavior.");
        }

        if (expectedTags is not null &&
            !expectedTags.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(
                software.Setting.Tags.Select(tag => tag.Name)))
        {
            throw new InvalidOperationException("The Software tab did not preserve its correlated tags.");
        }

        var entry = _historyRows.FirstOrDefault(row => row.Entry.Id == entryId);
        if (!expectedExcluded &&
            (entry is null ||
             !entry.Entry.SoftwareLabels.Contains(expectedLabel, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("History did not resolve the renamed software label for the existing entry.");
        }
    }

    private void VerifyVisibleRuleGroups()
    {
        var view = CollectionViewSource.GetDefaultView(RulesGrid.ItemsSource);
        var groups = view.Groups?.OfType<CollectionViewGroup>().ToArray() ?? [];
        if (groups.Length == 0 ||
            groups.Any(group =>
                group.Items.OfType<RuleRow>().Any(row =>
                    !string.Equals(row.ProjectGroup, group.Name as string, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("Window Rules rows are not contained by their matching project group.");
        }
    }

    private static void VerifyRowOrEmptyMenu(ContextMenu? menu, string rowTag, string label)
    {
        if (menu is null)
        {
            throw new InvalidOperationException($"{label} does not have a context menu.");
        }

        var emptyActions = menu.Items.OfType<FrameworkElement>()
            .Where(item => string.Equals(item.Tag as string, "EmptyOnly", StringComparison.Ordinal))
            .ToArray();
        var rowActions = menu.Items.OfType<FrameworkElement>()
            .Where(item => string.Equals(item.Tag as string, rowTag, StringComparison.Ordinal))
            .ToArray();
        if (emptyActions.Length == 0 || rowActions.Length == 0)
        {
            throw new InvalidOperationException($"{label} is missing its empty-space Add or row action menu.");
        }

        ConfigureRowOrEmptyContextMenu(menu, hasRow: false, rowTag);
        if (emptyActions.Any(item => item.Visibility != Visibility.Visible) ||
            rowActions.Any(item => item.Visibility != Visibility.Collapsed))
        {
            throw new InvalidOperationException($"{label} does not show only Add actions for empty-space right-click.");
        }

        ConfigureRowOrEmptyContextMenu(menu, hasRow: true, rowTag);
        if (emptyActions.Any(item => item.Visibility != Visibility.Collapsed) ||
            rowActions.Any(item => item.Visibility != Visibility.Visible))
        {
            throw new InvalidOperationException($"{label} does not show only object actions for row right-click.");
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }

        DragMove();
    }

    private void ProfileButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var menu = CreateProfileContextMenu();
        ProfileButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private ContextMenu CreateProfileContextMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ProfileButton,
            Placement = PlacementMode.Bottom,
        };
        foreach (var profile in _profileCatalog.Profiles)
        {
            var profileId = profile.Id;
            var item = new MenuItem
            {
                Header = profile.Name,
                Tag = "ProfileChoice",
                IsCheckable = true,
                IsChecked = profile.Id == _activeProfile.Id,
                StaysOpenOnClick = false,
            };
            item.Click += async (_, _) => await SelectProfileAsync(profileId);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var addItem = new MenuItem
        {
            Header = "Add profile…",
            Tag = "ProfileAdd",
        };
        addItem.Click += AddProfile_Click;
        menu.Items.Add(addItem);

        var renameItem = new MenuItem
        {
            Header = "Rename current profile…",
            Tag = "ProfileRename",
        };
        renameItem.Click += RenameProfile_Click;
        menu.Items.Add(renameItem);

        var removeItem = new MenuItem
        {
            Header = "Remove current profile…",
            Tag = "ProfileRemove",
            IsEnabled = !_activeProfile.UsesRootDirectory &&
                        _profileCatalog.Profiles.Count > 1,
            ToolTip = _activeProfile.UsesRootDirectory
                ? "The built-in profile contains the original app data and cannot be removed."
                : null,
        };
        removeItem.Click += RemoveProfile_Click;
        menu.Items.Add(removeItem);
        return menu;
    }

    private async Task SelectProfileAsync(Guid profileId)
    {
        if (profileId != _activeProfile.Id)
        {
            _ = await _requestProfileSwitch(profileId, null);
        }
    }

    private async void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new TextInputDialog(
            "New profile",
            "Profile name — creates a separate blank workspace")
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var profile = _profileCatalog.Add(dialog.Value);
            _ = await _requestProfileSwitch(profile.Id, null);
        }
        catch (Exception exception)
        {
            ShowError("Could not add profile", exception);
        }
    }

    private async void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new TextInputDialog(
            "Rename profile",
            "Profile name",
            _activeProfile.Name)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _activeProfile = _profileCatalog.Rename(_activeProfile.Id, dialog.Value);
            UpdateProfileLabel();
            await _googleSheetsSync.SetProfileNameAsync(_activeProfile.Name);
        }
        catch (Exception exception)
        {
            ShowError("Could not rename profile", exception);
        }
    }

    private async void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_activeProfile.UsesRootDirectory)
        {
            return;
        }

        var destination = _profileCatalog.Profiles
            .First(profile => profile.UsesRootDirectory);
        var result = MessageBox.Show(
            this,
            $"Remove profile “{_activeProfile.Name}” and switch to {destination.Name}?\n\n" +
            "Its data will be moved to the Profiles\\Removed recovery folder.",
            "Remove profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result == MessageBoxResult.Yes)
        {
            _ = await _requestProfileSwitch(destination.Id, _activeProfile.Id);
        }
    }

    private void UpdateProfileLabel()
    {
        ProfileNameText.Text = _activeProfile.Name;
        ProfileButton.ToolTip =
            $"Current profile: {_activeProfile.Name}\nSwitch or manage local profiles";
    }

    internal void VerifyProfilesForPreview()
    {
        var menu = CreateProfileContextMenu();
        var profileChoices = menu.Items
            .OfType<MenuItem>()
            .Where(item => string.Equals(item.Tag as string, "ProfileChoice", StringComparison.Ordinal))
            .ToArray();
        if (!string.Equals(ProfileNameText.Text, _activeProfile.Name, StringComparison.Ordinal) ||
            profileChoices.Length != _profileCatalog.Profiles.Count ||
            profileChoices.Count(item => item.IsChecked) != 1 ||
            menu.Items.OfType<MenuItem>().All(item =>
                !string.Equals(item.Tag as string, "ProfileAdd", StringComparison.Ordinal)) ||
            menu.Items.OfType<MenuItem>().All(item =>
                !string.Equals(item.Tag as string, "ProfileRename", StringComparison.Ordinal)) ||
            menu.Items.OfType<MenuItem>().All(item =>
                !string.Equals(item.Tag as string, "ProfileRemove", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The title-bar profile selector is missing its active profile or management actions.");
        }
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        _ = e;
        ClearEditorFocusWhenClickingOutsideInput(e.OriginalSource as DependencyObject);
    }

    private void ClearEditorFocusWhenClickingOutsideInput(DependencyObject? source)
    {
        if (!IsWithinControl(source, TimerTaskCombo))
        {
            TimerTaskCombo.IsDropDownOpen = false;
        }

        if (!IsWithinControl(source, TimerProjectCombo))
        {
            TimerProjectCombo.IsDropDownOpen = false;
        }

        if (IsWithinEditableInput(source))
        {
            return;
        }

        // A click on a static surface normally leaves WPF keyboard focus in the
        // prior editor. Clear it explicitly so the field stops looking active,
        // while preserving its draft text for a later Start or save action.
        Keyboard.ClearFocus();
    }

    private static bool IsWithinControl(DependencyObject? source, DependencyObject control)
    {
        foreach (var current in EnumerateInputAncestors(source))
        {
            if (ReferenceEquals(current, control))
            {
                return true;
            }

            if (control is ComboBox comboBox &&
                current is ComboBoxItem item &&
                ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), comboBox))
            {
                // ComboBox dropdowns live in a separate Popup visual tree.
                // Resolve the owning items control explicitly so a click on a
                // popup row is not mistaken for an outside click and closed
                // before Selector can commit it.
                return true;
            }
        }

        return false;
    }

    private static bool IsWithinEditableInput(DependencyObject? source)
    {
        foreach (var current in EnumerateInputAncestors(source))
        {
            if (current is TextBoxBase or ComboBox or ComboBoxItem or PasswordBox)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<DependencyObject> EnumerateInputAncestors(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetVisualOrLogicalParent(current))
        {
            yield return current;
        }
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject element) =>
        element switch
        {
            Visual visual => VisualTreeHelper.GetParent(visual),
            FrameworkContentElement content => content.Parent,
            _ => LogicalTreeHelper.GetParent(element),
        };

    internal void VerifyInactiveSurfaceClearsTimerEditorFocusForPreview()
    {
        var originalDescription = TimerDescriptionText.Text;
        var originalTopmost = Topmost;
        try
        {
            TimerDescriptionText.Text = "Unfocused draft";
            // Smoke runs are launched from a non-foreground host. Temporarily
            // raising the preview window lets WPF establish real keyboard focus
            // so this remains a behavioral assertion rather than a template-only check.
            Topmost = true;
            Activate();
            Focus();
            _ = Keyboard.Focus(TimerDescriptionText);
            if (!TimerDescriptionText.IsKeyboardFocusWithin)
            {
                throw new InvalidOperationException("The timer description editor could not receive keyboard focus.");
            }

            ClearEditorFocusWhenClickingOutsideInput(ElapsedText);
            if (TimerDescriptionText.IsKeyboardFocusWithin ||
                !string.Equals(TimerDescriptionText.Text, "Unfocused draft", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Clicking an inactive app surface did not end timer-bar editing while preserving the draft.");
            }

            if (!HistoryProjectCombo.Focus() || !HistoryProjectCombo.IsKeyboardFocusWithin)
            {
                throw new InvalidOperationException("The History project filter could not receive keyboard focus.");
            }

            ClearEditorFocusWhenClickingOutsideInput(new ComboBoxItem());
            if (!HistoryProjectCombo.IsKeyboardFocusWithin)
            {
                throw new InvalidOperationException(
                    "Clicking a dropdown row cleared focus before its selection could be committed.");
            }
        }
        finally
        {
            TimerDescriptionText.Text = originalDescription;
            Topmost = originalTopmost;
        }
    }

    internal async Task VerifyTimerBarInteractionsForPreviewAsync()
    {
        var token = Guid.NewGuid().ToString("N");
        var client = await _store.AddClientAsync($"Timer chooser client {token}", "#766F80");
        var firstProject = await _store.AddProjectAsync(
            client.Id,
            $"First timer project {token}",
            "#7B8495");
        var secondProject = await _store.AddProjectAsync(
            client.Id,
            $"Second timer project {token}",
            "#0D8F68");
        _ = await _store.AddTaskAsync(firstProject.Id, "Blocking");
        var secondTask = await _store.AddTaskAsync(secondProject.Id, "Animation");
        await RefreshAllAsync();

        TimerProjectCombo.SelectedValue = firstProject.Id;
        for (var attempt = 0;
             attempt < 50 && TimerProjectCombo.SelectedValue as Guid? != firstProject.Id;
             attempt++)
        {
            await Task.Delay(20);
        }

        await _controller.StartTimerAsync(
            firstProject.Id,
            TrackingSource.Manual,
            showDetails: false);
        TimerProjectCombo.SelectedValue = secondProject.Id;
        for (var attempt = 0;
             attempt < 100 &&
             (_controller.RunningEntry?.ProjectId != secondProject.Id || _loading);
             attempt++)
        {
            await Task.Delay(20);
        }

        if (_controller.RunningEntry?.ProjectId != secondProject.Id ||
            TimerProjectCombo.SelectedValue as Guid? != secondProject.Id ||
            !TimerProjectCombo.IsEnabled)
        {
            throw new InvalidOperationException(
                "The timer project chooser did not commit a new project to the running entry.");
        }

        TimerTaskCombo.SelectedValue = secondTask.Id;
        for (var attempt = 0;
             attempt < 100 &&
             (_controller.RunningEntry?.TaskId != secondTask.Id || _loading);
             attempt++)
        {
            await Task.Delay(20);
        }

        if (_controller.RunningEntry?.TaskId != secondTask.Id ||
            TimerTaskCombo.SelectedValue as Guid? != secondTask.Id ||
            !string.Equals(TimerTaskCombo.Text, secondTask.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The timer task chooser did not commit its selected task or update the editor text.");
        }

        var secondProjectOption = _projectOptions.Single(option =>
            option.ProjectId == secondProject.Id);
        TimerProjectCombo.IsDropDownOpen = true;
        TimerProjectCombo.UpdateLayout();
        ComboBoxItem? projectItem = null;
        for (var attempt = 0; attempt < 20 && projectItem is null; attempt++)
        {
            projectItem = TimerProjectCombo.ItemContainerGenerator
                .ContainerFromItem(secondProjectOption) as ComboBoxItem;
            if (projectItem is null)
            {
                await Task.Delay(20);
            }
        }

        TimerTaskCombo.IsDropDownOpen = true;
        TimerTaskCombo.UpdateLayout();
        ComboBoxItem? taskItem = null;
        for (var attempt = 0; attempt < 20 && taskItem is null; attempt++)
        {
            taskItem = TimerTaskCombo.ItemContainerGenerator
                .ContainerFromItem(secondTask) as ComboBoxItem;
            if (taskItem is null)
            {
                await Task.Delay(20);
            }
        }

        if (projectItem is null ||
            taskItem is null ||
            !IsWithinControl(projectItem, TimerProjectCombo) ||
            !IsWithinControl(taskItem, TimerTaskCombo))
        {
            throw new InvalidOperationException(
                "Timer dropdown rows are still treated as outside clicks.");
        }

        TimerProjectCombo.IsDropDownOpen = false;
        TimerTaskCombo.IsDropDownOpen = false;
        InitializeTimerTaskSearch();
        var editor = _timerTaskEditor
            ?? throw new InvalidOperationException("The timer task editor is unavailable.");
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
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            await Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (!string.Equals(editor.Text, "A", StringComparison.Ordinal) ||
                editor.SelectionLength != 0 ||
                editor.CaretIndex != 1 ||
                !TimerTaskCombo.IsDropDownOpen)
            {
                throw new InvalidOperationException(
                    "Opening task search after the first character changed the editor state. " +
                    $"Text='{editor.Text}', selection={editor.SelectionStart}:{editor.SelectionLength}, " +
                    $"caret={editor.CaretIndex}, open={TimerTaskCombo.IsDropDownOpen}, " +
                    $"focus={editor.IsKeyboardFocusWithin}, matches={_timerTaskSearchView?.Count ?? -1}, " +
                    $"search='{_timerTaskSearchText}'.");
            }

            editor.SelectedText = "n";
            if (!string.Equals(editor.Text, "An", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The second typed task character replaced the first character.");
            }
        }
        finally
        {
            TimerTaskCombo.IsDropDownOpen = false;
            Topmost = originalTopmost;
            await _controller.StopForShutdownAsync();
        }
    }

    internal async Task<DateTimeOffset?> SetTimerStartTimeForPreviewAsync(string value)
    {
        TimerStartTimeText.Text = value;
        await _timerActionGate.WaitAsync();
        try
        {
            await PersistTimerStartAsync();
            return _controller.RunningEntry?.StartUtc;
        }
        finally
        {
            _timerActionGate.Release();
        }
    }

    internal Visibility TimerStartTimeVisibilityForPreview =>
        TimerStartTimePanel.Visibility;

    internal string TimerStartTimeTextForPreview =>
        TimerStartTimeText.Text;

    internal async Task VerifyHistoryFiltersForPreviewAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var client = await _store.AddClientAsync($"History filter client {suffix}", "#766F80");
        var firstProject = await _store.AddProjectAsync(client.Id, $"History filter alpha {suffix}", "#339CFF");
        var secondProject = await _store.AddProjectAsync(client.Id, $"History filter beta {suffix}", "#40C977");
        var firstTask = await _store.AddTaskAsync(firstProject.Id, $"History task alpha {suffix}");
        var secondTask = await _store.AddTaskAsync(firstProject.Id, $"History task beta {suffix}");
        var otherTask = await _store.AddTaskAsync(secondProject.Id, $"History task other {suffix}");
        var firstTag = $"history-filter-alpha-{suffix}";
        var secondTag = $"history-filter-beta-{suffix}";
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);

        await _store.AddManualEntryAsync(
            firstProject.Id,
            firstTask.Id,
            $"First filter entry #{firstTag}",
            now.AddMinutes(-30),
            now.AddMinutes(-25));
        await _store.AddManualEntryAsync(
            firstProject.Id,
            secondTask.Id,
            $"Second filter entry #{secondTag}",
            now.AddMinutes(-20),
            now.AddMinutes(-15));
        await _store.AddManualEntryAsync(
            secondProject.Id,
            otherTask.Id,
            $"Other project entry #{secondTag}",
            now.AddMinutes(-10),
            now.AddMinutes(-5));

        await RefreshAllAsync();

        HistoryProjectCombo.SelectedItem = HistoryProjectCombo.Items
            .OfType<ProjectFilterOption>()
            .Single(option => option.ProjectId == firstProject.Id);
        await WaitForHistoryFilterAsync(rows =>
            rows.Count == 2 && rows.All(row => row.Entry.ProjectId == firstProject.Id));

        HistoryTaskCombo.SelectedItem = HistoryTaskCombo.Items
            .OfType<TaskFilterOption>()
            .Single(option => option.TaskId == firstTask.Id);
        await WaitForHistoryFilterAsync(rows =>
            rows.Count == 1 && rows[0].Entry.TaskId == firstTask.Id);

        HistoryProjectCombo.SelectedItem = HistoryProjectCombo.Items
            .OfType<ProjectFilterOption>()
            .Single(option => option.ProjectId is null);
        await WaitForHistoryFilterAsync(rows => rows.Count >= 3);

        HistoryTagCombo.SelectedItem = HistoryTagCombo.Items
            .OfType<TagOption>()
            .Single(option => string.Equals(option.Value, secondTag, StringComparison.OrdinalIgnoreCase));
        await WaitForHistoryFilterAsync(rows =>
            rows.Count == 2 && rows.All(row =>
                row.TagList.Contains(secondTag, StringComparer.OrdinalIgnoreCase)));
    }

    private async Task WaitForHistoryFilterAsync(Func<IReadOnlyList<TimeEntryRow>, bool> predicate)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var rows = HistoryGrid.Items.OfType<TimeEntryRow>().ToArray();
            if (predicate(rows))
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException(
            "A History project, task, or tag selection did not update the visible entries.");
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ToggleMaximized();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }

    private void MainWindow_StateChanged(object sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        WindowShell.CornerRadius = new CornerRadius(0);
        WindowShell.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        ApplyResponsiveLayout(ActualWidth);
    }

    private void NavigationToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NavigationPopup.IsOpen = !NavigationPopup.IsOpen;
    }

    private void FloatingNavigation_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Button { Tag: string indexText } && int.TryParse(indexText, out var index))
        {
            MainTabs.SelectedIndex = index;
            NavigationPopup.IsOpen = false;
            MainTabs.Focus();
        }
    }

    private void UpdateBell_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        MainTabs.SelectedIndex = 3;
        SettingsCategoryTabs.SelectedIndex = 4;
        NavigationPopup.IsOpen = false;
        SettingsCategoryTabs.Focus();
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (MainTabs is null || TitleSidebarColumn is null || TimerSidebarColumn is null || SidebarTargetsPanel is null)
        {
            return;
        }

        var sidebarWidth = width >= 1180 ? 275d : width >= 1040 ? 240d : 0d;
        TitleSidebarColumn.Width = new GridLength(sidebarWidth);
        TimerSidebarColumn.Width = new GridLength(sidebarWidth);
        ProfileNameText.MaxWidth = sidebarWidth >= 275d ? 52d : 18d;
        SidebarTargetsPanel.Width = sidebarWidth;
        SidebarTargetsPanel.Visibility = sidebarWidth == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplySidebarTargetsPanelHeight();
        NavigationToggleButton.Visibility = sidebarWidth == 0 ? Visibility.Visible : Visibility.Collapsed;

        MainTabs.ApplyTemplate();
        if (MainTabs.Template.FindName("NavigationColumn", MainTabs) is ColumnDefinition navigationColumn)
        {
            navigationColumn.Width = new GridLength(sidebarWidth);
        }

        if (MainTabs.Template.FindName("NavigationSurface", MainTabs) is FrameworkElement navigationSurface)
        {
            navigationSurface.Visibility = sidebarWidth == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        if (sidebarWidth > 0)
        {
            NavigationPopup.IsOpen = false;
        }

        var mainPaneWidth = width - sidebarWidth;
        var compactComposer = mainPaneWidth < 900;
        TimerTaskColumn.Width = new GridLength(compactComposer ? 180 : 220);
        TimerProjectColumn.Width = new GridLength(compactComposer ? 220 : 270);
        ApplyResponsiveReportLayout(mainPaneWidth);
    }

    private double GetMaximumSidebarTargetsPanelHeight()
    {
        var availableHeight = MainTabs?.ActualHeight ?? 0;
        if (!double.IsFinite(availableHeight) || availableHeight <= 0)
        {
            return SidebarTargetsPanelSettings.MaximumHeight;
        }

        return Math.Clamp(
            availableHeight - SidebarNavigationReservedHeight,
            SidebarTargetsPanelSettings.MinimumHeight,
            SidebarTargetsPanelSettings.MaximumHeight);
    }

    private void ApplySidebarTargetsPanelHeight()
    {
        if (SidebarTargetsPanel is null)
        {
            return;
        }

        SidebarTargetsPanel.Height = Math.Clamp(
            _sidebarTargetsPanelPreferredHeight,
            SidebarTargetsPanelSettings.MinimumHeight,
            GetMaximumSidebarTargetsPanelHeight());
    }

    private void ResizeSidebarTargetsPanel(double verticalChange)
    {
        var currentHeight = double.IsFinite(SidebarTargetsPanel.Height)
            ? SidebarTargetsPanel.Height
            : SidebarTargetsPanel.ActualHeight;
        _sidebarTargetsPanelPreferredHeight = Math.Clamp(
            currentHeight - verticalChange,
            SidebarTargetsPanelSettings.MinimumHeight,
            GetMaximumSidebarTargetsPanelHeight());
        ApplySidebarTargetsPanelHeight();
    }

    private void SidebarTargetsResizeThumb_DragStarted(
        object sender,
        DragStartedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (SidebarTargetsPanel.ActualHeight > 0)
        {
            _sidebarTargetsPanelPreferredHeight = SidebarTargetsPanel.ActualHeight;
        }
    }

    private void SidebarTargetsResizeThumb_DragDelta(
        object sender,
        DragDeltaEventArgs e)
    {
        _ = sender;
        ResizeSidebarTargetsPanel(e.VerticalChange);
        e.Handled = true;
    }

    private async void SidebarTargetsResizeThumb_DragCompleted(
        object sender,
        DragCompletedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            await SaveSidebarTargetsPanelHeightAsync();
        }
        catch (Exception exception)
        {
            ShowError("Could not save the targets panel size", exception);
        }
    }

    private Task SaveSidebarTargetsPanelHeightAsync()
    {
        var height = (int)Math.Round(_sidebarTargetsPanelPreferredHeight);
        _sidebarTargetsPanelPreferredHeight = height;
        ApplySidebarTargetsPanelHeight();
        return _store.SetSettingAsync(
            SidebarTargetsPanelSettings.HeightKey,
            height.ToString(CultureInfo.InvariantCulture));
    }

    private void ApplyResponsiveReportLayout(double mainPaneWidth)
    {
        if (ReportVisualsGrid is null || ReportGrid is null || ReportSummaryColumn is null)
        {
            return;
        }

        var stacked = mainPaneWidth < 900;
        if (stacked)
        {
            ReportVisualsGrid.MinHeight = 712;
            ReportTableColumn.Width = new GridLength(1, GridUnitType.Star);
            ReportGapColumn.Width = new GridLength(0);
            ReportChartColumn.Width = new GridLength(0);
            ReportVisualsTopRow.Height = new GridLength(280);
            ReportVisualsGapRow.Height = new GridLength(12);
            ReportVisualsBottomRow.Height = new GridLength(420);
            Grid.SetColumn(ReportGrid, 0);
            Grid.SetRow(ReportGrid, 0);
            Grid.SetColumn(ReportSummaryColumn, 0);
            Grid.SetRow(ReportSummaryColumn, 2);
        }
        else
        {
            ReportVisualsGrid.MinHeight = 420;
            ReportTableColumn.Width = new GridLength(1, GridUnitType.Star);
            ReportGapColumn.Width = new GridLength(12);
            ReportChartColumn.Width = new GridLength(360);
            ReportVisualsTopRow.Height = new GridLength(1, GridUnitType.Star);
            ReportVisualsGapRow.Height = new GridLength(0);
            ReportVisualsBottomRow.Height = new GridLength(0);
            Grid.SetColumn(ReportGrid, 0);
            Grid.SetRow(ReportGrid, 0);
            Grid.SetColumn(ReportSummaryColumn, 2);
            Grid.SetRow(ReportSummaryColumn, 0);
        }
    }

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    public async Task RefreshAllAsync()
    {
        if (!_loaded)
        {
            return;
        }

        if (_loading)
        {
            _refreshPending = true;
            return;
        }

        _loading = true;
        try
        {
            var selectedProject = TimerProjectCombo.SelectedValue as Guid?;
            var selectedTimerTask = TimerTaskCombo.SelectedValue as Guid?;
            var pendingTimerTaskText = selectedTimerTask is null ? TimerTaskCombo.Text : null;
            _projectOptions = await _store.GetProjectOptionsAsync();
            TimerProjectCombo.ItemsSource = _projectOptions;
            TimerProjectCombo.SelectedValue = _controller.RunningEntry?.ProjectId ?? selectedProject ?? _projectOptions.FirstOrDefault()?.ProjectId;

            var clients = await _store.GetClientsAsync();
            var projects = await _store.GetProjectsAsync();
            var projectWork = (await _store.GetProjectWorkSummariesAsync(_controller.UtcNow))
                .ToDictionary(summary => summary.ProjectId);
            var tasks = await _store.GetTasksAsync();
            var reportClients = await _store.GetClientsAsync(includeArchived: true);
            var reportProjects = await _store.GetProjectsAsync(includeArchived: true);
            var reportTasks = await _store.GetTasksAsync(includeArchived: true);
            var taskWork = (await _store.GetTaskWorkSummariesAsync(_controller.UtcNow))
                .ToDictionary(summary => summary.TaskId);
            var rules = await _store.GetRulesAsync(includeFrozen: true);
            var customTargets = await _store.GetCustomTargetsAsync();
            var tagSummaries = await _store.GetTagSummariesAsync();
            var software = await _store.GetProjectSoftwareAsync(includeFrozen: true);
            var clientNames = clients.ToDictionary(client => client.Id, client => client.Name);
            var projectNames = projects.ToDictionary(project => project.Id, project => project.Name);
            var projectsById = projects.ToDictionary(project => project.Id);
            var frozenProjectIds = projects
                .Where(project => project.IsFrozen)
                .Select(project => project.Id)
                .ToHashSet();

            _activeClients = clients;
            _activeProjects = projects;
            _activeTasks = tasks;
            _reportClients = reportClients;
            _reportProjects = reportProjects;
            _reportTasks = reportTasks;
            _tagDefinitions = tagSummaries.Select(summary => summary.Tag).ToArray();
            ApplyTimerTagDefinitions();
            UpdateHistoryFilterOptions();
            UpdateReportFilterOptions();

            ClientsGrid.ItemsSource = clients
                .Select(client => new ClientRow(
                    client,
                    projects
                        .Where(project => project.ClientId == client.Id && !project.IsFrozen)
                        .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(project => new ClientProjectRow(project))
                        .ToArray()))
                .ToArray();
            var projectRows = projects
                .Select(project => new ProjectRow(
                    project,
                    clientNames.GetValueOrDefault(project.ClientId, "Archived client"),
                    projectWork.GetValueOrDefault(project.Id)))
                .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Client, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ProjectsGrid.ItemsSource = projectRows.Where(row => !row.Project.IsFrozen).ToArray();
            FrozenProjectsList.ItemsSource = projectRows.Where(row => row.Project.IsFrozen).ToArray();
            _customTargetRows = await BuildCustomTargetRowsAsync(
                customTargets,
                projectsById,
                clientNames);
            var taskRows = tasks
                .Select(task =>
                {
                    var project = projectsById.GetValueOrDefault(task.ProjectId);
                    return new TaskRow(
                        task,
                        project?.Name ?? "Archived project",
                        project is null
                            ? "Archived client"
                            : clientNames.GetValueOrDefault(project.ClientId, "Archived client"),
                        taskWork.GetValueOrDefault(task.Id));
                })
                .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Project, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Client, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _taskRows = taskRows.Where(row => !frozenProjectIds.Contains(row.Task.ProjectId)).ToArray();
            _frozenTaskRows = taskRows.Where(row => frozenProjectIds.Contains(row.Task.ProjectId)).ToArray();
            FrozenTasksList.ItemsSource = _frozenTaskRows;
            UpdateTaskFilterOptions();
            ApplyTaskFilter();
            var tagRows = tagSummaries
                .Select(summary => new TagRow(summary, _projectOptions))
                .ToArray();
            TagsGrid.ItemsSource = tagRows
                .Where(row => row.Summary.Tag.IsGlobal ||
                              row.Summary.Tag.AssignedProjectIds.Any(projectId => !frozenProjectIds.Contains(projectId)))
                .ToArray();
            FrozenTagsList.ItemsSource = tagRows
                .Where(row => !row.Summary.Tag.IsGlobal &&
                              row.Summary.Tag.AssignedProjectIds.Count > 0 &&
                              row.Summary.Tag.AssignedProjectIds.All(frozenProjectIds.Contains))
                .ToArray();
            var softwareRows = software.Select(item => new SoftwareRow(item)).ToArray();
            _softwareRows = softwareRows
                .Where(row => row.IsGlobal || !frozenProjectIds.Contains(row.ProjectId))
                .ToArray();
            _frozenSoftwareRows = softwareRows
                .Where(row => !row.IsGlobal && frozenProjectIds.Contains(row.ProjectId))
                .ToArray();
            FrozenSoftwareList.ItemsSource = _frozenSoftwareRows;
            UpdateSoftwareFilterOptions();
            ApplySoftwareFilter();
            var ruleRows = rules
                .Select(rule =>
                {
                    var project = projectsById[rule.ProjectId];
                    return new RuleRow(
                        rule,
                        project.Name,
                        clientNames[project.ClientId]);
                })
                .OrderBy(row => row.Project, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Client, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.TitlePattern, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _ruleRows = ruleRows.Where(row => !frozenProjectIds.Contains(row.Rule.ProjectId)).ToArray();
            _frozenRuleRows = ruleRows.Where(row => frozenProjectIds.Contains(row.Rule.ProjectId)).ToArray();
            FrozenRulesList.ItemsSource = _frozenRuleRows;
            UpdateRuleFilterOptions();
            ApplyRuleFilter();

            var runningTaskId = _controller.RunningEntry?.TaskId;
            await ReloadTimerTasksAsync(
                runningTaskId ?? selectedTimerTask,
                runningTaskId is null ? pendingTimerTaskText : null);
            UpdateTimerUi();
            await RefreshHistoryAsync();
            await RefreshReportAsync();
            await RefreshTrelloAsync();
            await RefreshGoogleSheetsAsync();
        }
        finally
        {
            _loading = false;
            if (_refreshPending)
            {
                _refreshPending = false;
                await RefreshAllAsync();
            }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var today = DateTime.Today;
        await SetDefaultHistoryRangeAsync(today);
        ReportRangePicker.SetRange(new DateTime(today.Year, today.Month, 1), today, notify: false);
        RecognitionCheck.IsChecked = _controller.RecognitionEnabled;
        UpdateAutomaticRecognitionControls();
        SessionBehaviorCombo.SelectedIndex =
            _controller.SessionTrackingBehavior == SessionTrackingBehavior.StopTimer ? 0 : 1;
        UpdateSessionBehaviorDescription();
        CallsIdleProtectionCheck.IsChecked = _controller.CallsIdleProtectionEnabled;
        VideoIdleProtectionCheck.IsChecked = _controller.VideoIdleProtectionEnabled;
        UpdateIdleProtectionStatus(_controller.IdleProtectionState);
        RecentEntryResumeMinutesText.Text =
            _controller.RecentEntryResumeMaximumGapMinutes.ToString(CultureInfo.CurrentCulture);
        BreakReminderMinutesText.Text =
            _controller.BreakReminderIntervalMinutes.ToString(CultureInfo.CurrentCulture);
        SetBreakReminderPlacementControls(_controller.BreakReminderPlacement);
        SetBreakReminderMessageControls(_controller.BreakReminderEnabledMessageIds);
        ExcludedSoftwareReviewMinutesText.Text =
            _controller.ExcludedSoftwareReviewMinimumMinutes.ToString(CultureInfo.CurrentCulture);
        AccumulatedAwayReviewMinutesText.Text =
            _controller.AccumulatedAwayReviewMinimumMinutes.ToString(CultureInfo.CurrentCulture);
        ShortIdleReportingMinutesText.Text =
            _controller.ShortIdleReportingMaximumMinutes.ToString(CultureInfo.CurrentCulture);
        SetTargetReviewScheduleControls(_controller.TargetReviewSchedule);
        AutostartCheck.IsChecked = _autostart.IsEnabled;
        UpdateUpdateControls(_updateCheck.State);
        DatabasePathText.Text = Path.GetDirectoryName(_store.DatabasePath) ?? _store.DatabasePath;
        _sidebarTargetsPanelPreferredHeight = SidebarTargetsPanelSettings.ParseHeight(
            await _store.GetSettingAsync(SidebarTargetsPanelSettings.HeightKey));
        ApplyResponsiveLayout(ActualWidth);
        await LoadHistoryViewAsync();
        await LoadReportViewAsync();
        InitializeTimerTaskSearch();
        _loaded = true;
        await RefreshAllAsync();
    }

    private void TrelloSync_SyncCompleted(object? sender, TrelloSyncResult result)
    {
        _ = sender;
        _ = result;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            _controller.NotifyDataChanged();
            await RefreshTrelloAsync();
        });
    }

    private void GoogleSheetsSync_SyncCompleted(object? sender, GoogleSheetsSyncResult result)
    {
        _ = sender;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (!string.IsNullOrWhiteSpace(result.SharedProfileName) &&
                !string.Equals(_activeProfile.Name, result.SharedProfileName, StringComparison.Ordinal))
            {
                try
                {
                    _activeProfile = _profileCatalog.Rename(_activeProfile.Id, result.SharedProfileName);
                    UpdateProfileLabel();
                }
                catch (InvalidOperationException)
                {
                    // A local profile with that name can coexist; the shared name remains visible in Sheets.
                }
            }
            if (result.DataChanged)
            {
                await _controller.ReloadSynchronizedProfileSettingsAsync();
            }
            UpdateRemoteTimerStatus();
            await RefreshGoogleSheetsAsync();
        });
    }

    private void UpdateCheck_StateChanged(object? sender, UpdateCheckState state)
    {
        _ = sender;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => UpdateUpdateControls(state));
            return;
        }

        UpdateUpdateControls(state);
    }

    private void UpdateUpdateControls(UpdateCheckState state)
    {
        UpdateChecksEnabledCheck.IsChecked = state.AutomaticChecksEnabled;
        UpdateInstalledVersionText.Text = $"Installed version {state.InstalledVersion.ToString(3)}";
        OpenUpdateReleaseButton.Visibility = state.IsUpdateAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        var bellVisibility = state.IsUpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        UpdateBellButton.Visibility = bellVisibility;
        FloatingUpdateBellButton.Visibility = bellVisibility;

        if (state.Status == UpdateCheckStatus.UpdateAvailable && state.LatestVersion is not null)
        {
            UpdateStatusText.Text = $"Version {state.LatestVersion.ToString(3)} is available on GitHub.";
            UpdateStatusText.Foreground = (Brush)FindResource("WarningBrush");
            return;
        }

        if (state.IsUpdateAvailable && state.LatestVersion is not null)
        {
            UpdateStatusText.Text = $"Version {state.LatestVersion.ToString(3)} is available on GitHub. {state.ErrorMessage}";
            UpdateStatusText.Foreground = (Brush)FindResource("WarningBrush");
            return;
        }

        UpdateStatusText.Foreground = (Brush)FindResource("ContentSecondaryBrush");
        UpdateStatusText.Text = state.Status switch
        {
            UpdateCheckStatus.UpToDate => $"You’re up to date. Last checked {FormatUpdateCheckTime(state.LastSuccessfulCheckUtc)}.",
            UpdateCheckStatus.NoRelease => $"No published GitHub release is available yet. Last checked {FormatUpdateCheckTime(state.LastSuccessfulCheckUtc)}.",
            UpdateCheckStatus.Failed => state.ErrorMessage ?? "Could not check for updates.",
            _ when !state.AutomaticChecksEnabled => "Automatic update checks are off. You can still check manually.",
            _ => "Updates have not been checked yet.",
        };
    }

    private static string FormatUpdateCheckTime(DateTimeOffset? timestamp) =>
        timestamp is null
            ? "never"
            : TimeZoneInfo.ConvertTime(timestamp.Value, TimeZoneInfo.Local)
                .ToString("dd MMM yyyy HH:mm", CultureInfo.CurrentCulture);

    private async void UpdateChecksEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loaded || _loading)
        {
            return;
        }

        try
        {
            await _updateCheck.SetAutomaticChecksEnabledAsync(UpdateChecksEnabledCheck.IsChecked == true);
        }
        catch (Exception exception)
        {
            UpdateUpdateControls(_updateCheck.State);
            ShowError("Could not update update-check preference", exception);
        }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CheckForUpdatesButton.IsEnabled = false;
        try
        {
            await _updateCheck.CheckManuallyAsync();
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private void OpenUpdateRelease_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var releasePageUri = _updateCheck.State.ReleasePageUri;
        if (_updateCheck.State.IsUpdateAvailable && releasePageUri is not null)
        {
            _ = Process.Start(new ProcessStartInfo(releasePageUri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private async Task RefreshGoogleSheetsAsync()
    {
        var connection = await _googleSheetsSync.GetConnectionAsync();
        var connected = connection is not null;
        GoogleSheetsConnectButton.Content = connected ? "Reconnect" : "Connect";
        GoogleSheetsSyncButton.IsEnabled = connected && connection?.StoreExportsInGoogleSheets == true;
        GoogleSheetsOpenButton.IsEnabled = !string.IsNullOrWhiteSpace(connection?.SpreadsheetUrl);
        GoogleSheetsDisconnectButton.IsEnabled = connected;
        GoogleSheetsCloudExportCheck.IsEnabled = connected;
        GoogleSheetsCloudExportCheck.IsChecked = connection?.StoreExportsInGoogleSheets == true;
        GoogleSheetsDeviceNameText.IsEnabled = connected;
        GoogleSheetsTimeZoneCombo.IsEnabled = connected;
        GoogleSheetsDeviceNameText.Text = connection?.DeviceName ?? Environment.MachineName;
        if (GoogleSheetsTimeZoneCombo.ItemsSource is null)
        {
            GoogleSheetsTimeZoneCombo.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
        }
        GoogleSheetsTimeZoneCombo.SelectedValue = connection?.PinnedTimeZoneId ?? TimeZoneInfo.Local.Id;
        var conflicts = connected
            ? await _googleSheetsSync.GetConflictsAsync()
            : [];
        GoogleSheetsReviewConflictsButton.IsEnabled = conflicts.Count > 0;
        GoogleSheetsConflictStatusText.Text = conflicts.Count == 0
            ? "No conflicts need review."
            : $"{conflicts.Count} conflict{(conflicts.Count == 1 ? string.Empty : "s")} need review; other data keeps synchronizing.";
        GoogleSheetsConflictStatusText.Foreground = (Brush)FindResource(
            conflicts.Count == 0 ? "MutedBrush" : "WarningBrush");
        if (connection is null)
        {
            GoogleSheetsConnectionText.Text = "Not connected";
            GoogleSheetsSyncStatusText.Text = "This profile currently stays on this computer.";
            GoogleSheetsSyncStatusText.Foreground = (Brush)FindResource("MutedBrush");
            return;
        }

        GoogleSheetsConnectionText.Text = $"Connected as {connection.DisplayName} ({connection.Email})";
        if (connection.RequiresReconnect)
        {
            GoogleSheetsSyncStatusText.Text = "Authorization needs attention. Reconnect this profile.";
            GoogleSheetsSyncStatusText.Foreground = (Brush)FindResource("DangerBrush");
        }
        else if (!string.IsNullOrWhiteSpace(connection.LastError))
        {
            GoogleSheetsSyncStatusText.Text = $"Last sync failed: {connection.LastError}";
            GoogleSheetsSyncStatusText.Foreground = (Brush)FindResource("DangerBrush");
        }
        else if (connection.LastSuccessfulSyncUtc is { } lastSync)
        {
            GoogleSheetsSyncStatusText.Text =
                $"Last synchronized {lastSync.ToLocalTime():g}. Local tracking remains available offline.";
            GoogleSheetsSyncStatusText.Foreground = (Brush)FindResource("MutedBrush");
        }
        else
        {
            GoogleSheetsSyncStatusText.Text = "Connected. The first background reconciliation is pending.";
            GoogleSheetsSyncStatusText.Foreground = (Brush)FindResource("MutedBrush");
        }
    }

    private async void GoogleSheetsConnect_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new GoogleSheetsConnectionWindow(_googleSheetsSync) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _ = await _googleSheetsSync.SyncNowAsync();
        }
        catch (Exception exception)
        {
            ShowError("Google Sheets connected, but the first sync failed", exception);
        }

        await RefreshGoogleSheetsAsync();
    }

    private async void GoogleSheetsSync_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        GoogleSheetsSyncButton.IsEnabled = false;
        GoogleSheetsSyncStatusText.Text = "Reconciling the shared profile…";
        GoogleSheetsSyncStatusText.Text = "Reconciling the shared profile…";
        try
        {
            var result = await _googleSheetsSync.SyncNowAsync();
            await RefreshGoogleSheetsAsync();
            GoogleSheetsSyncStatusText.Text =
                $"Synchronized {result.EntryCount} entries; uploaded {result.UploadedCount}, imported {result.ImportedCount}, conflicts {result.ConflictCount}.";
        }
        catch (Exception exception)
        {
            ShowError("Could not synchronize Google Sheets", exception);
            await RefreshGoogleSheetsAsync();
        }
    }

    private async void GoogleSheetsCloudExportCheck_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loading || !GoogleSheetsCloudExportCheck.IsEnabled)
        {
            return;
        }

        try
        {
            await _googleSheetsSync.SetCloudExportEnabledAsync(
                GoogleSheetsCloudExportCheck.IsChecked == true);
            await RefreshGoogleSheetsAsync();
        }
        catch (Exception exception)
        {
            ShowError("Could not change log export storage", exception);
            await RefreshGoogleSheetsAsync();
        }
    }

    private async void GoogleSheetsDisconnect_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (MessageBox.Show(
                this,
                "Disconnect Google Sheets from this profile? The spreadsheet remains in Google Drive and local daily exports resume.",
                "Disconnect Google Sheets",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _googleSheetsSync.DisconnectAsync();
            await RefreshGoogleSheetsAsync();
        }
        catch (Exception exception)
        {
            ShowError("Could not disconnect Google Sheets", exception);
        }
    }

    private async void GoogleSheetsOpen_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var connection = await _googleSheetsSync.GetConnectionAsync();
        if (!string.IsNullOrWhiteSpace(connection?.SpreadsheetUrl))
        {
            _ = Process.Start(new ProcessStartInfo(connection.SpreadsheetUrl) { UseShellExecute = true });
        }
    }

    private async void GoogleSheetsSaveDeviceName_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            await _googleSheetsSync.SetDeviceNameAsync(GoogleSheetsDeviceNameText.Text);
            await RefreshGoogleSheetsAsync();
        }
        catch (Exception exception)
        {
            ShowError("Could not save the computer name", exception);
        }
    }

    private async void GoogleSheetsApplyTimeZone_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (GoogleSheetsTimeZoneCombo.SelectedValue is not string timeZoneId)
        {
            return;
        }
        if (MessageBox.Show(
                this,
                "Change the shared daily worksheet time zone? Daily tabs will be rebuilt, but the stored UTC timestamps will not change.",
                "Change worksheet time zone",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            await RefreshGoogleSheetsAsync();
            return;
        }
        try
        {
            await _googleSheetsSync.SetPinnedTimeZoneAsync(timeZoneId);
            _ = await _googleSheetsSync.SyncNowAsync();
        }
        catch (Exception exception)
        {
            ShowError("Could not change the worksheet time zone", exception);
            await RefreshGoogleSheetsAsync();
        }
    }

    private void GoogleSheetsReviewConflicts_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new SyncConflictReviewWindow(_googleSheetsSync) { Owner = this };
        _ = dialog.ShowDialog();
        _ = RefreshGoogleSheetsAsync();
    }

    private void BuildHistoryColumnsMenu()
    {
        if (GetHistoryColumnsSubmenu() is not { } columnsSubmenu)
        {
            return;
        }

        columnsSubmenu.Items.Clear();
        foreach (var column in HistoryGrid.Columns.OrderBy(column => column.DisplayIndex))
        {
            var item = new MenuItem
            {
                Header = GetHistoryColumnKey(column),
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
                Tag = column,
            };
            item.Click += HistoryColumnMenuItem_Click;
            columnsSubmenu.Items.Add(item);
        }

        UpdateHistoryColumnsMenu();
    }

    private HistoryViewState CaptureHistoryView() =>
        new(HistoryGrid.Columns
            .Select((column, index) => new HistoryColumnState(
                GetHistoryColumnKey(column),
                column.DisplayIndex >= 0 ? column.DisplayIndex : index,
                column.Width.UnitType,
                column.Width.Value,
                column.Visibility == Visibility.Visible))
            .OrderBy(column => column.Key, StringComparer.Ordinal)
            .ToArray(),
            GetHistoryTextWrapping(HistoryGrid));

    private bool ApplyHistoryView(HistoryViewState view)
    {
        var columnsByKey = HistoryGrid.Columns.ToDictionary(
            GetHistoryColumnKey,
            StringComparer.Ordinal);
        if (view.Columns.Count != columnsByKey.Count ||
            view.Columns.Select(column => column.Key).Distinct(StringComparer.Ordinal).Count() != columnsByKey.Count ||
            view.Columns.Any(column =>
                !columnsByKey.ContainsKey(column.Key) ||
                column.DisplayIndex < 0 ||
                column.DisplayIndex >= columnsByKey.Count ||
                !Enum.IsDefined(column.WidthUnit) ||
                !double.IsFinite(column.WidthValue) ||
                column.WidthValue <= 0) ||
            view.Columns.Select(column => column.DisplayIndex).Distinct().Count() != columnsByKey.Count ||
            view.Columns.All(column => !column.IsVisible))
        {
            return false;
        }

        _updatingHistoryView = true;
        try
        {
            foreach (var state in view.Columns)
            {
                var column = columnsByKey[state.Key];
                column.Width = new DataGridLength(state.WidthValue, state.WidthUnit);
                column.Visibility = state.IsVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            foreach (var state in view.Columns.OrderBy(column => column.DisplayIndex))
            {
                columnsByKey[state.Key].DisplayIndex = state.DisplayIndex;
            }

            SetHistoryTextWrapping(view.WrapText);
        }
        finally
        {
            _updatingHistoryView = false;
        }

        HistoryGrid.UpdateLayout();
        UpdateHistoryColumnsMenu();
        UpdateHistoryGroupLayout();
        UpdateHistoryViewDirtyState();
        return true;
    }

    private async Task LoadHistoryViewAsync()
    {
        var savedJson = await _store.GetSettingAsync(HistoryViewSettingKey);
        if (string.IsNullOrWhiteSpace(savedJson))
        {
            ApplyHistoryView(_defaultHistoryView);
            _savedHistoryView = CaptureHistoryView();
            UpdateHistoryViewDirtyState();
            return;
        }

        try
        {
            var saved = System.Text.Json.JsonSerializer.Deserialize<HistoryViewState>(savedJson);
            if (saved is null || !ApplyHistoryView(saved))
            {
                throw new InvalidDataException("The saved History view is invalid.");
            }

            _savedHistoryView = CaptureHistoryView();
        }
        catch (System.Text.Json.JsonException)
        {
            ApplyHistoryView(_defaultHistoryView);
            _savedHistoryView = CaptureHistoryView();
        }
        catch (InvalidDataException)
        {
            ApplyHistoryView(_defaultHistoryView);
            _savedHistoryView = CaptureHistoryView();
        }

        UpdateHistoryViewDirtyState();
    }

    private async Task SaveHistoryViewAsync()
    {
        var current = CaptureHistoryView();
        HistorySaveViewButton.IsEnabled = false;
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(current);
            await _store.SetSettingAsync(HistoryViewSettingKey, json);
            _savedHistoryView = current;
            UpdateHistoryViewDirtyState();
        }
        finally
        {
            HistorySaveViewButton.IsEnabled = true;
        }
    }

    private static string GetHistoryColumnKey(DataGridColumn column) =>
        column.Header as string
        ?? throw new InvalidOperationException("Every History column needs a text header.");

    private static bool HistoryViewsEqual(HistoryViewState left, HistoryViewState right)
    {
        if (left.Columns.Count != right.Columns.Count)
        {
            return false;
        }

        var rightByKey = right.Columns.ToDictionary(column => column.Key, StringComparer.Ordinal);
        return left.Columns.All(column =>
            rightByKey.TryGetValue(column.Key, out var other) &&
            column.DisplayIndex == other.DisplayIndex &&
            column.WidthUnit == other.WidthUnit &&
            Math.Abs(column.WidthValue - other.WidthValue) < 0.01 &&
            column.IsVisible == other.IsVisible) &&
            left.WrapText == right.WrapText;
    }

    private void UpdateHistoryViewDirtyState()
    {
        if (_updatingHistoryView)
        {
            return;
        }

        HistorySaveViewButton.Visibility =
            HistoryViewsEqual(CaptureHistoryView(), _savedHistoryView)
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void UpdateHistoryColumnsMenu()
    {
        HistoryWrapTextMenuItem.IsChecked = GetHistoryTextWrapping(HistoryGrid);
        if (GetHistoryColumnsSubmenu() is not { } columnsSubmenu)
        {
            return;
        }

        var visibleCount = HistoryGrid.Columns.Count(column =>
            column.Visibility == Visibility.Visible);
        foreach (var item in columnsSubmenu.Items.OfType<MenuItem>())
        {
            if (item.Tag is not DataGridColumn column)
            {
                continue;
            }

            var isVisible = column.Visibility == Visibility.Visible;
            item.IsChecked = isVisible;
            item.IsEnabled = !isVisible || visibleCount > 1;
        }
    }

    private void SetHistoryColumnVisibility(DataGridColumn column, bool isVisible)
    {
        if (!isVisible &&
            column.Visibility == Visibility.Visible &&
            HistoryGrid.Columns.Count(candidate =>
                candidate.Visibility == Visibility.Visible) <= 1)
        {
            return;
        }

        column.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateHistoryColumnsMenu();
        UpdateHistoryGroupLayout();
        UpdateHistoryViewDirtyState();
    }

    private MenuItem? GetHistoryColumnsSubmenu() =>
        HistoryColumnsButton.ContextMenu?.Items
            .OfType<MenuItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                HistoryColumnsSubmenuTag,
                StringComparison.Ordinal));

    private void HideHistoryColumn(DataGridColumn column) =>
        SetHistoryColumnVisibility(column, isVisible: false);

    private void SetHistoryTextWrapping(bool isEnabled)
    {
        SetHistoryTextWrapping(HistoryGrid, isEnabled);
        HistoryGrid.RowHeight = isEnabled ? double.NaN : 42d;
        HistoryWrapTextMenuItem.IsChecked = isEnabled;
        HistoryGrid.UpdateLayout();
        UpdateHistoryViewDirtyState();
    }

    private void RestoreDefaultHistoryView() =>
        _ = ApplyHistoryView(_defaultHistoryView);

    private void UpdateHistoryGroupLayout()
    {
        var visibleColumns = HistoryGrid.Columns
            .Where(column => column.Visibility == Visibility.Visible)
            .OrderBy(column => column.DisplayIndex)
            .ToArray();
        var offset = 0d;
        var durationOffset = 0d;
        var dateOffset = 0d;
        var dateOffsetSet = false;
        foreach (var column in visibleColumns)
        {
            if (ReferenceEquals(column, HistoryDurationColumn))
            {
                durationOffset = offset;
            }
            else if (!dateOffsetSet)
            {
                dateOffset = offset;
                dateOffsetSet = true;
            }

            offset += column.ActualWidth > 0
                ? column.ActualWidth
                : column.Width.Value;
        }

        HistoryGrid.Tag = new HistoryGroupLayout(
            new Thickness(dateOffset + 8, 0, 0, 0),
            new Thickness(durationOffset + 8, 0, 0, 0),
            HistoryDurationColumn.Visibility);
    }

    private void HistoryColumnMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is MenuItem { Tag: DataGridColumn column } item)
        {
            SetHistoryColumnVisibility(column, item.IsChecked);
        }
    }

    private void HistoryWrapTextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is MenuItem item)
        {
            SetHistoryTextWrapping(item.IsChecked);
        }
    }

    private void HistoryColumns_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        UpdateHistoryColumnsMenu();
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void HistoryHideColumn_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not MenuItem { DataContext: DataGridColumn column })
        {
            return;
        }

        HideHistoryColumn(column);
    }

    private async void HistorySaveView_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            await SaveHistoryViewAsync();
        }
        catch (Exception exception)
        {
            ShowError("Could not save History view", exception);
        }
    }

    private void HistoryRestoreDefaultView_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        RestoreDefaultHistoryView();
    }

    private void HistoryGrid_ColumnReordered(object? sender, DataGridColumnEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_updatingHistoryView)
        {
            return;
        }

        UpdateHistoryColumnsMenu();
        UpdateHistoryGroupLayout();
        UpdateHistoryViewDirtyState();
    }

    private void HistoryGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                UpdateHistoryGroupLayout();
                UpdateHistoryViewDirtyState();
            }));
    }

    private static ReportViewState CreateDefaultReportView() =>
        new(ReportColumnDefinitions
            .Select(column => new ReportColumnState(column.Key, IsVisible: true, column.Width))
            .ToArray());

    private void BuildReportColumnsMenu()
    {
        if (GetReportColumnsSubmenu() is not { } columnsSubmenu)
        {
            return;
        }

        columnsSubmenu.Items.Clear();
        foreach (var definition in ReportColumnDefinitions)
        {
            var item = new MenuItem
            {
                Header = definition.Key,
                IsCheckable = true,
                StaysOpenOnClick = true,
                Tag = definition.Key,
            };
            item.Click += ReportColumnMenuItem_Click;
            columnsSubmenu.Items.Add(item);
        }

        UpdateReportColumnsMenu();
    }

    private async Task LoadReportViewAsync()
    {
        var savedJson = await _store.GetSettingAsync(ReportViewSettingKey);
        if (string.IsNullOrWhiteSpace(savedJson))
        {
            _reportView = CreateDefaultReportView();
            _savedReportView = _reportView;
            ApplyReportViewToVisibleElements();
            UpdateReportViewDirtyState();
            return;
        }

        try
        {
            var saved = System.Text.Json.JsonSerializer.Deserialize<ReportViewState>(savedJson);
            if (saved is null || !TrySetReportView(saved))
            {
                throw new InvalidDataException("The saved Reports view is invalid.");
            }

            _savedReportView = _reportView;
        }
        catch (System.Text.Json.JsonException)
        {
            _reportView = CreateDefaultReportView();
            _savedReportView = _reportView;
            ApplyReportViewToVisibleElements();
        }
        catch (InvalidDataException)
        {
            _reportView = CreateDefaultReportView();
            _savedReportView = _reportView;
            ApplyReportViewToVisibleElements();
        }

        UpdateReportColumnsMenu();
        UpdateReportViewDirtyState();
    }

    private bool TrySetReportView(ReportViewState view)
    {
        var expected = ReportColumnDefinitions
            .Select(column => column.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (view.Columns.Select(column => column.Key).Distinct(StringComparer.Ordinal).Count() != view.Columns.Count ||
            view.Columns.Any(column =>
                !expected.Contains(column.Key) ||
                !double.IsFinite(column.Width) ||
                column.Width < 0) ||
            view.Columns.All(column => !column.IsVisible))
        {
            return false;
        }

        var savedByKey = view.Columns.ToDictionary(column => column.Key, StringComparer.Ordinal);
        _reportView = new ReportViewState(ReportColumnDefinitions
            .Select(definition => savedByKey.GetValueOrDefault(
                definition.Key,
                new ReportColumnState(definition.Key, IsVisible: true, definition.Width)))
            .Select(column => column.Width > 0
                ? column
                : column with { Width = ReportColumnDefinitions.Single(definition =>
                    string.Equals(definition.Key, column.Key, StringComparison.Ordinal)).Width })
            .ToArray());
        ApplyReportViewToVisibleElements();
        UpdateReportColumnsMenu();
        UpdateReportViewDirtyState();
        return true;
    }

    private async Task SaveReportViewAsync()
    {
        CaptureReportColumnWidthsFromVisibleGrid();
        ReportSaveViewButton.IsEnabled = false;
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_reportView);
            await _store.SetSettingAsync(ReportViewSettingKey, json);
            _savedReportView = _reportView;
            UpdateReportViewDirtyState();
        }
        finally
        {
            ReportSaveViewButton.IsEnabled = true;
        }
    }

    private static bool ReportViewsEqual(ReportViewState left, ReportViewState right)
    {
        var rightByKey = right.Columns.ToDictionary(column => column.Key, StringComparer.Ordinal);
        return left.Columns.Count == right.Columns.Count &&
               left.Columns.All(column =>
                   rightByKey.TryGetValue(column.Key, out var other) &&
                   column.IsVisible == other.IsVisible &&
                   Math.Abs(column.Width - other.Width) < 0.01);
    }

    private void UpdateReportViewDirtyState() =>
        ReportSaveViewButton.Visibility = ReportViewsEqual(_reportView, _savedReportView)
            ? Visibility.Collapsed
            : Visibility.Visible;

    private IReadOnlyDictionary<string, bool> GetReportColumnVisibility() =>
        _reportView.Columns.ToDictionary(
            column => column.Key,
            column => column.IsVisible,
            StringComparer.Ordinal);

    private void SetReportColumnVisibility(string key, bool isVisible)
    {
        if (!ReportColumnDefinitions.Any(definition =>
                string.Equals(definition.Key, key, StringComparison.Ordinal)))
        {
            return;
        }

        var visibleCount = _reportView.Columns.Count(column => column.IsVisible);
        var current = _reportView.Columns.First(column =>
            string.Equals(column.Key, key, StringComparison.Ordinal));
        if (!isVisible && current.IsVisible && visibleCount <= 1)
        {
            return;
        }

        _reportView = new ReportViewState(_reportView.Columns
            .Select(column => string.Equals(column.Key, key, StringComparison.Ordinal)
                ? column with { IsVisible = isVisible }
                : column)
            .ToArray());
        ApplyReportViewToVisibleElements();
        UpdateReportColumnsMenu();
        UpdateReportViewDirtyState();
    }

    private void ApplyReportViewToVisibleElements()
    {
        if (ReportGrid is null)
        {
            return;
        }

        ReportGrid.UpdateLayout();
        foreach (var grid in FindVisualDescendants<DataGrid>(ReportGrid))
        {
            ApplyReportColumnView(grid);
        }

        foreach (var summaryGrid in FindVisualDescendants<Grid>(ReportGrid)
                     .Where(IsReportSummaryGrid))
        {
            ApplyReportSummaryGridColumnView(summaryGrid);
        }
    }

    private static bool IsReportSummaryGrid(Grid grid) =>
        string.Equals(grid.Tag as string, "ReportSummaryHeader", StringComparison.Ordinal) ||
        string.Equals(grid.Tag as string, "ReportSummaryFooter", StringComparison.Ordinal);

    private void ApplyReportColumnView(DataGrid grid)
    {
        var columnsByKey = _reportView.Columns.ToDictionary(column => column.Key, StringComparer.Ordinal);
        foreach (var column in grid.Columns)
        {
            if (column.Header is string key && columnsByKey.TryGetValue(key, out var state))
            {
                column.Width = new DataGridLength(state.Width);
                column.Visibility = state.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void ApplyReportSummaryGridColumnView(Grid summaryGrid)
    {
        if (summaryGrid.ColumnDefinitions.Count != ReportColumnDefinitions.Length)
        {
            return;
        }

        var columnsByKey = _reportView.Columns.ToDictionary(column => column.Key, StringComparer.Ordinal);
        for (var index = 0; index < ReportColumnDefinitions.Length; index++)
        {
            var definition = ReportColumnDefinitions[index];
            var state = columnsByKey.GetValueOrDefault(
                definition.Key,
                new ReportColumnState(definition.Key, IsVisible: true, definition.Width));
            summaryGrid.ColumnDefinitions[index].Width = state.IsVisible
                ? new GridLength(state.Width)
                : new GridLength(0);
            foreach (var child in summaryGrid.Children.Cast<UIElement>()
                         .Where(child => Grid.GetColumn(child) == index))
            {
                child.Visibility = state.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void UpdateReportColumnsMenu()
    {
        if (GetReportColumnsSubmenu() is not { } columnsSubmenu)
        {
            return;
        }

        var visibility = GetReportColumnVisibility();
        var visibleCount = visibility.Count(pair => pair.Value);
        foreach (var item in columnsSubmenu.Items.OfType<MenuItem>())
        {
            if (item.Tag is not string key || !visibility.TryGetValue(key, out var isVisible))
            {
                continue;
            }

            item.IsChecked = isVisible;
            item.IsEnabled = !isVisible || visibleCount > 1;
        }
    }

    private MenuItem? GetReportColumnsSubmenu() =>
        ReportColumnsButton.ContextMenu?.Items
            .OfType<MenuItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                ReportColumnsSubmenuTag,
                StringComparison.Ordinal));

    private void ReportTaskGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is DataGrid grid)
        {
            ApplyReportColumnView(grid);
        }
    }

    private void ReportTaskGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _ = e;
        if (sender is not DataGrid grid)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => SynchronizeReportColumnWidthsFromGrid(grid)));
    }

    private void CaptureReportColumnWidthsFromVisibleGrid()
    {
        var grid = FindVisualDescendants<DataGrid>(ReportGrid).FirstOrDefault();
        if (grid is not null)
        {
            CaptureReportColumnWidths(grid);
        }
    }

    private void SynchronizeReportColumnWidthsFromGrid(DataGrid grid)
    {
        if (!CaptureReportColumnWidths(grid))
        {
            return;
        }

        ApplyReportViewToVisibleElements();
        UpdateReportViewDirtyState();
    }

    private bool CaptureReportColumnWidths(DataGrid grid)
    {
        var widths = grid.Columns
            .Where(column => column.Header is string)
            .ToDictionary(
                column => (string)column.Header,
                column => column.Width.Value,
                StringComparer.Ordinal);
        if (ReportColumnDefinitions.Any(definition =>
                !widths.TryGetValue(definition.Key, out var width) ||
                !double.IsFinite(width) ||
                width <= 0))
        {
            return false;
        }

        var updated = new ReportViewState(_reportView.Columns
            .Select(column => column with { Width = widths[column.Key] })
            .ToArray());
        if (ReportViewsEqual(updated, _reportView))
        {
            return false;
        }

        _reportView = updated;
        return true;
    }

    private void ReportGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_updatingReportSelection || ReportGrid.SelectedItem is not ProjectReportSummaryRow project)
        {
            return;
        }

        _reportTargetProjectId = project.ProjectId;
        UpdateReportTargetsList();
        _updatingReportSelection = true;
        try
        {
            foreach (var taskGrid in FindVisualDescendants<DataGrid>(ReportGrid))
            {
                taskGrid.UnselectAll();
            }
        }
        finally
        {
            _updatingReportSelection = false;
        }
    }

    private void ReportTaskGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = e;
        if (_updatingReportSelection || sender is not DataGrid selectedTaskGrid ||
            selectedTaskGrid.SelectedItem is not ReportTaskSummaryRow task)
        {
            return;
        }

        _reportTargetProjectId = task.ProjectId;
        UpdateReportTargetsList();
        _updatingReportSelection = true;
        try
        {
            ReportGrid.UnselectAll();
            foreach (var taskGrid in FindVisualDescendants<DataGrid>(ReportGrid))
            {
                if (!ReferenceEquals(taskGrid, selectedTaskGrid))
                {
                    taskGrid.UnselectAll();
                }
            }
        }
        finally
        {
            _updatingReportSelection = false;
        }
    }

    private void ReportSummaryGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Grid summaryGrid)
        {
            ApplyReportSummaryGridColumnView(summaryGrid);
        }
    }

    private void ReportColumnMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is MenuItem { Tag: string key } item)
        {
            SetReportColumnVisibility(key, item.IsChecked);
        }
    }

    private void ReportColumns_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        UpdateReportColumnsMenu();
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ReportHideColumn_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        var key = sender switch
        {
            MenuItem { DataContext: DataGridColumn { Header: string columnKey } } => columnKey,
            MenuItem { DataContext: FrameworkElement { Tag: string headerKey } } => headerKey,
            _ => null,
        };
        if (key is not null)
        {
            SetReportColumnVisibility(key, isVisible: false);
        }
    }

    private async void ReportSaveView_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            await SaveReportViewAsync();
        }
        catch (Exception exception)
        {
            ShowError("Could not save Reports view", exception);
        }
    }

    private void ReportRestoreDefaultView_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = TrySetReportView(CreateDefaultReportView());
    }

    private async void Controller_DataChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await RefreshAllAsync();
    }

    private async void Controller_RunningEntryChanged(object? sender, TimeEntry? entry)
    {
        _ = sender;
        if (entry is null)
        {
            ClearTimerTaskAfterStop();
        }

        await RefreshAllAsync();
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        if (!ReferenceEquals(e.OriginalSource, MainTabs) || !_loaded || _loading)
        {
            return;
        }

        if (MainTabs.SelectedIndex != 0 && _historySortMemberPath is not null)
        {
            ClearHistorySort();
        }

        if (MainTabs.SelectedIndex == 0)
        {
            if (_preserveHistoryFiltersOnNextTabEntry)
            {
                _preserveHistoryFiltersOnNextTabEntry = false;
                return;
            }

            await ResetHistoryFiltersAsync();
        }
        else if (MainTabs.SelectedIndex == 2)
        {
            await ResetReportFiltersAsync();
        }
        else if (MainTabs.SelectedIndex == 1 && ManagementTabs.SelectedIndex == 2)
        {
            ResetTargetProjectFilter();
        }
        else if (MainTabs.SelectedIndex == 1 && ManagementTabs.SelectedIndex == 3)
        {
            ResetTaskProjectFilter();
        }
    }

    private void ManagementTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        if (!ReferenceEquals(e.OriginalSource, ManagementTabs) || !_loaded || _loading)
        {
            return;
        }

        if (ManagementTabs.SelectedIndex == 2)
        {
            ResetTargetProjectFilter();
        }
        else if (ManagementTabs.SelectedIndex == 3)
        {
            ResetTaskProjectFilter();
        }
    }

    private async Task ResetHistoryFiltersAsync()
    {
        var wasUpdating = _updatingHistoryFilters;
        _updatingHistoryFilters = true;
        try
        {
            await SetDefaultHistoryRangeAsync(DateTime.Today);
            _historyProjectFilterId = null;
            _historyTaskFilterId = null;
            _historyUnassignedOnly = false;
            HistoryDescriptionFilterText.Clear();
            UpdateHistoryFilterOptions();
            if (HistoryTagCombo.ItemsSource is IEnumerable<TagOption> tags)
            {
                HistoryTagCombo.SelectedItem = tags.FirstOrDefault(tag => tag.Value is null);
            }
        }
        finally
        {
            _updatingHistoryFilters = wasUpdating;
        }

        await RefreshHistoryAsync();
    }

    private async Task SetDefaultHistoryRangeAsync(DateTime today)
    {
        var latestEntryUtc = await _store.GetLatestEntryStartUtcAsync();
        var latestEntryLocalDate = latestEntryUtc?.ToLocalTime().Date;
        var (start, end) = HistoryDefaultDateRange.Resolve(today, latestEntryLocalDate);
        HistoryRangePicker.SetRange(start, end, notify: false);
    }

    private static (DateTime Start, DateTime End) GetCalendarMonth(DateTime date)
    {
        var start = new DateTime(date.Year, date.Month, 1);
        return (start, start.AddMonths(1).AddDays(-1));
    }

    private async Task ResetReportFiltersAsync()
    {
        var wasUpdating = _updatingReportFilters;
        _updatingReportFilters = true;
        try
        {
            var today = DateTime.Today;
            ReportRangePicker.SetRange(new DateTime(today.Year, today.Month, 1), today, notify: false);
            ReportClientCombo.SelectedIndex = 0;
            ReportProjectCombo.SelectedIndex = 0;
            ReportTaskCombo.SelectedIndex = 0;
            ReportPaidCombo.SelectedIndex = 0;
            if (ReportTagCombo.ItemsSource is IEnumerable<TagOption> tags)
            {
                ReportTagCombo.SelectedItem = tags.FirstOrDefault(tag => tag.Value is null);
            }

            UpdateReportFilterOptions();
        }
        finally
        {
            _updatingReportFilters = wasUpdating;
        }

        await RefreshReportAsync();
    }

    private void Controller_TimerTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateElapsed();
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await _timerActionGate.WaitAsync();
        try
        {
            if (_controller.RunningEntry is not null)
            {
                await PersistTimerStartAsync();
                var runningProjectId = _controller.RunningEntry.ProjectId;
                var runningTaskId = await ResolveTimerTaskAsync(runningProjectId);
                await _controller.SaveRunningDetailsAsync(
                    runningTaskId,
                    TimerDescriptionText.Text);
                await _controller.StopTimerAsync();
                TimerDescriptionText.Clear();
                TimerTaskCombo.SelectedIndex = -1;
                TimerTaskCombo.Text = string.Empty;
                return;
            }

            if (TimerProjectCombo.SelectedValue is not Guid projectId)
            {
                MessageBox.Show(this, "Create and choose a project before starting a timer.", "Project required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var taskId = await ResolveTimerTaskAsync(projectId);
            await _controller.StartTimerAsync(
                projectId,
                TrackingSource.Manual,
                showDetails: false,
                initialDescription: TimerDescriptionText.Text,
                initialTaskId: taskId);
            TimerTaskCombo.SelectedValue = taskId;
        }
        catch (Exception exception)
        {
            ShowError("Could not change the timer", exception);
        }
        finally
        {
            _timerActionGate.Release();
        }
    }

    private async void TimerCallCheck_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loading || _updatingTimerCall || _controller.RunningEntry is null)
        {
            return;
        }

        await _timerActionGate.WaitAsync();
        try
        {
            if (_controller.RunningEntry is not { } runningEntry)
            {
                return;
            }

            var isCall = TimerCallCheck.IsChecked == true;
            if (runningEntry.IsCall == isCall)
            {
                return;
            }

            await PersistTimerStartAsync();
            var taskId = await ResolveTimerTaskAsync(runningEntry.ProjectId);
            await _controller.SaveRunningDetailsAsync(taskId, TimerDescriptionText.Text);
            await _controller.SetRunningCallTrackingAsync(isCall);
        }
        catch (Exception exception)
        {
            UpdateTimerUi();
            ShowError("Could not update call tracking", exception);
        }
        finally
        {
            _timerActionGate.Release();
        }
    }

    private async void TimerProjectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loading)
        {
            return;
        }

        var changeVersion = ++_timerProjectChangeVersion;
        var selectedProjectId = TimerProjectCombo.SelectedValue as Guid?;
        var pendingTaskText = TimerTaskCombo.SelectedValue is Guid ? null : TimerTaskCombo.Text;
        await ReloadTimerTasksAsync(null, pendingTaskText);
        if (changeVersion != _timerProjectChangeVersion ||
            TimerProjectCombo.SelectedValue as Guid? != selectedProjectId)
        {
            return;
        }

        ApplyTimerTagDefinitions();
        if (selectedProjectId is not { } projectId ||
            _controller.RunningEntry is not { } running ||
            running.ProjectId == projectId)
        {
            return;
        }

        await _timerActionGate.WaitAsync();
        try
        {
            if (_controller.RunningEntry is not null &&
                TimerProjectCombo.SelectedValue is Guid currentProjectId &&
                currentProjectId == projectId)
            {
                await _controller.SaveRunningAssignmentAsync(
                    projectId,
                    TimerTaskCombo.SelectedValue as Guid?,
                    TimerDescriptionText.Text);
            }
        }
        catch (Exception exception)
        {
            ShowError("Could not change the tracked project", exception);
        }
        finally
        {
            _timerActionGate.Release();
        }
    }

    private async void TimerTaskCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loading ||
            _updatingTimerTaskSearch ||
            TimerTaskCombo.SelectedItem is not SavedTask selectedTask)
        {
            return;
        }

        _updatingTimerTaskSearch = true;
        try
        {
            TimerTaskCombo.Text = selectedTask.Name;
            TimerTaskCombo.IsDropDownOpen = false;
            if (_timerTaskEditor is not null)
            {
                _timerTaskEditor.CaretIndex = _timerTaskEditor.Text.Length;
                _timerTaskEditor.SelectionLength = 0;
            }
        }
        finally
        {
            _updatingTimerTaskSearch = false;
        }

        if (_controller.RunningEntry is null ||
            _controller.RunningEntry.TaskId == selectedTask.Id)
        {
            return;
        }

        await _timerActionGate.WaitAsync();
        try
        {
            if (_controller.RunningEntry is not null)
            {
                await _controller.SaveRunningDetailsAsync(
                    selectedTask.Id,
                    TimerDescriptionText.Text);
            }
        }
        catch (Exception exception)
        {
            ShowError("Could not change the tracked task", exception);
        }
        finally
        {
            _timerActionGate.Release();
        }
    }

    private void ApplyTimerTagDefinitions()
    {
        var projectId = _controller.RunningEntry?.ProjectId
            ?? (TimerProjectCombo.SelectedValue is Guid selectedProjectId
                ? selectedProjectId
                : (Guid?)null);
        TimerDescriptionText.SetTagDefinitions(projectId is { } id
            ? _tagDefinitions.Where(tag => tag.IsAvailableFor(id))
            : _tagDefinitions.Where(tag => tag.IsGlobal));
    }

    private async void TimerBarEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if ((e.Key != Key.Enter && e.Key != Key.Return) ||
            Keyboard.Modifiers != ModifierKeys.None ||
            TimerTaskCombo.IsDropDownOpen ||
            TimerProjectCombo.IsDropDownOpen)
        {
            return;
        }

        var running = _controller.RunningEntry;
        var hasTask = TimerTaskCombo.SelectedValue is Guid ||
                      !string.IsNullOrWhiteSpace(TimerTaskCombo.Text);
        if (running is null &&
            (TimerProjectCombo.SelectedValue is not Guid || !hasTask))
        {
            return;
        }

        e.Handled = true;
        await _timerActionGate.WaitAsync();
        try
        {
            TimerTaskCombo.IsDropDownOpen = false;
            TimerProjectCombo.IsDropDownOpen = false;

            if (_controller.RunningEntry is { } currentEntry)
            {
                await PersistTimerStartAsync();
                var runningTaskId = await ResolveTimerTaskAsync(currentEntry.ProjectId);
                await _controller.SaveRunningDetailsAsync(runningTaskId, TimerDescriptionText.Text);
                TimerTaskCombo.SelectedValue = runningTaskId;
                UpdateTimerUi();
                return;
            }

            if (TimerProjectCombo.SelectedValue is not Guid projectId)
            {
                return;
            }

            var taskId = await ResolveTimerTaskAsync(projectId);
            if (taskId is null)
            {
                return;
            }

            await _controller.StartTimerAsync(
                projectId,
                TrackingSource.Manual,
                showDetails: false,
                initialDescription: TimerDescriptionText.Text,
                initialTaskId: taskId);
            TimerTaskCombo.SelectedValue = taskId;
            UpdateTimerUi();
        }
        catch (Exception exception)
        {
            ShowError(
                _controller.RunningEntry is null
                    ? "Could not start the timer"
                    : "Could not update the running timer",
                exception);
        }
        finally
        {
            _timerActionGate.Release();
        }
    }

    private void TimerStartTimeText_TextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_settingTimerStartTimeText && _controller.RunningEntry is not null)
        {
            _timerStartTimeDirty = true;
        }
    }

    private async void TimerStartTimeText_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_timerStartTimeDirty || _controller.RunningEntry is null)
        {
            return;
        }

        await _timerActionGate.WaitAsync();
        try
        {
            await PersistTimerStartAsync();
        }
        catch (Exception exception)
        {
            ShowError("Could not change the timer start", exception);
        }
        finally
        {
            _timerActionGate.Release();
        }
    }

    private async Task ReloadTimerTasksAsync(Guid? selectedTaskId, string? pendingTaskText = null)
    {
        if (TimerProjectCombo.SelectedValue is not Guid projectId)
        {
            _timerTaskSearchView = null;
            TimerTaskCombo.ItemsSource = null;
            TimerTaskCombo.SelectedIndex = -1;
            TimerTaskCombo.Text = pendingTaskText ?? string.Empty;
            return;
        }

        var tasks = await _store.GetTasksAsync(projectId);
        _updatingTimerTaskSearch = true;
        try
        {
            _timerTaskSearchView = new ListCollectionView(tasks.ToList());
            TimerTaskCombo.ItemsSource = _timerTaskSearchView;
            TimerTaskCombo.SelectedValue = selectedTaskId;
            if (selectedTaskId is null)
            {
                TimerTaskCombo.SelectedIndex = -1;
                TimerTaskCombo.Text = pendingTaskText ?? string.Empty;
            }

            ApplyTimerTaskSearch(TimerTaskCombo.Text, openDropDown: false);
        }
        finally
        {
            _updatingTimerTaskSearch = false;
        }
    }

    private void InitializeTimerTaskSearch()
    {
        TimerTaskCombo.ApplyTemplate();
        var editor = TimerTaskCombo.Template.FindName(
            "PART_EditableTextBox",
            TimerTaskCombo) as TextBox;
        if (editor is null || ReferenceEquals(editor, _timerTaskEditor))
        {
            return;
        }

        if (_timerTaskEditor is not null)
        {
            _timerTaskEditor.TextChanged -= TimerTaskEditor_TextChanged;
        }

        _timerTaskEditor = editor;
        _timerTaskEditor.TextChanged += TimerTaskEditor_TextChanged;
    }

    private void TimerTaskEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_updatingTimerTaskSearch || _timerTaskSearchView is null)
        {
            return;
        }

        var typedText = _timerTaskEditor?.Text ?? TimerTaskCombo.Text;
        var selectedTaskMatches = TimerTaskCombo.SelectedItem is SavedTask selectedTask &&
                                  string.Equals(
                                      typedText?.Trim(),
                                      selectedTask.Name,
                                      StringComparison.OrdinalIgnoreCase);
        if (!selectedTaskMatches &&
            TimerTaskCombo.SelectedItem is SavedTask &&
            _timerTaskEditor is { } editor)
        {
            // An editable ComboBox retains its selected item while the user
            // starts replacing the label. Opening the dropdown can then make
            // WPF restore that item label over the first typed character.
            // Detach the old selection once, preserving the editor exactly.
            var pendingText = editor.Text;
            var selectionStart = editor.SelectionStart;
            var selectionLength = editor.SelectionLength;
            var restoreKeyboardFocus = editor.IsKeyboardFocusWithin;
            _updatingTimerTaskSearch = true;
            try
            {
                TimerTaskCombo.SelectedIndex = -1;
                if (!string.Equals(editor.Text, pendingText, StringComparison.Ordinal))
                {
                    editor.Text = pendingText;
                }

                var safeStart = Math.Clamp(selectionStart, 0, editor.Text.Length);
                editor.Select(
                    safeStart,
                    Math.Clamp(selectionLength, 0, editor.Text.Length - safeStart));
                if (restoreKeyboardFocus)
                {
                    _ = Keyboard.Focus(editor);
                }
            }
            finally
            {
                _updatingTimerTaskSearch = false;
            }
        }

        ApplyTimerTaskSearch(
            typedText,
            openDropDown: !selectedTaskMatches &&
                          _timerTaskEditor?.IsKeyboardFocusWithin == true);
    }

    private void ApplyTimerTaskSearch(string? text, bool openDropDown)
    {
        if (_timerTaskSearchView is null)
        {
            return;
        }

        _timerTaskSearchText = text?.Trim() ?? string.Empty;
        _timerTaskSearchView.Filter = item =>
            item is SavedTask task &&
            (string.IsNullOrEmpty(_timerTaskSearchText) ||
             task.Name.Contains(_timerTaskSearchText, StringComparison.OrdinalIgnoreCase));
        _timerTaskSearchView.CustomSort = new TimerTaskSearchComparer(_timerTaskSearchText);
        _timerTaskSearchView.Refresh();

        if (openDropDown &&
            !string.IsNullOrEmpty(_timerTaskSearchText) &&
            _timerTaskSearchView.Count > 0)
        {
            OpenTimerTaskDropDownWithoutSelectingText(_timerTaskSearchText);
        }
        else if (_timerTaskSearchView.Count == 0 || string.IsNullOrEmpty(_timerTaskSearchText))
        {
            TimerTaskCombo.IsDropDownOpen = false;
        }
    }

    private void OpenTimerTaskDropDownWithoutSelectingText(string expectedSearchText)
    {
        var editor = _timerTaskEditor;
        if (editor is null)
        {
            TimerTaskCombo.IsDropDownOpen = true;
            return;
        }

        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
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
                    // Refocusing the editable ComboBox can select its complete
                    // contents. This invocation follows a real text change, so
                    // the intended typing position is after the new text.
                    selectionStart = expectedText.Length;
                    selectionLength = 0;
                }

                TimerTaskCombo.IsDropDownOpen = true;
                _ = Keyboard.Focus(editor);
                editor.Select(
                    Math.Clamp(selectionStart, 0, editor.Text.Length),
                    Math.Clamp(
                        selectionLength,
                        0,
                        editor.Text.Length - Math.Clamp(selectionStart, 0, editor.Text.Length)));

                _ = Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ContextIdle,
                    () =>
                    {
                        if (!string.Equals(editor.Text, expectedText, StringComparison.Ordinal) ||
                            selectionLength >= expectedText.Length ||
                            editor.SelectionLength != editor.Text.Length)
                        {
                            return;
                        }

                        var safeStart = Math.Clamp(selectionStart, 0, editor.Text.Length);
                        editor.Select(
                            safeStart,
                            Math.Clamp(selectionLength, 0, editor.Text.Length - safeStart));
                    });
            });
    }

    private async Task<Guid?> ResolveTimerTaskAsync(Guid projectId)
    {
        var taskName = TimerTaskCombo.Text?.Trim();
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return TimerTaskCombo.SelectedValue is Guid selectedTaskId ? selectedTaskId : null;
        }

        var task = await _store.GetOrAddTaskAsync(projectId, taskName);
        await ReloadTimerTasksAsync(task.Id);
        TimerTaskCombo.Text = task.Name;
        return task.Id;
    }

    private void UpdateTimerUi()
    {
        var running = _controller.RunningEntry;
        StartStopButton.Content = running is null ? "Start" : "Stop";
        StartStopButton.Style = (Style)FindResource(running is null ? "PrimaryButton" : "RunningTimerButton");
        TimerProjectCombo.IsEnabled = true;
        _updatingTimerCall = true;
        try
        {
            TimerCallCheck.Visibility = running is null ? Visibility.Collapsed : Visibility.Visible;
            TimerCallCheck.IsChecked = running?.IsCall == true;
        }
        finally
        {
            _updatingTimerCall = false;
        }
        if (running is not null)
        {
            TimerProjectCombo.SelectedValue = running.ProjectId;
            TimerTaskCombo.SelectedValue = running.TaskId;
            TimerDescriptionText.Text = running.Description ?? string.Empty;
            TimerStartTimePanel.Visibility = Visibility.Visible;
            if (!_timerStartTimeDirty &&
                !TimerStartTimeText.IsKeyboardFocusWithin)
            {
                SetTimerStartTimeText(running.StartUtc);
            }
        }
        else
        {
            TimerStartTimePanel.Visibility = Visibility.Collapsed;
            SetTimerStartTimeText(null);
        }

        UpdateElapsed();
        UpdateRemoteTimerStatus();
    }

    private void UpdateRemoteTimerStatus()
    {
        var timers = _googleSheetsSync.RemoteTimers;
        if (timers.Count == 0)
        {
            RemoteTimerStatusText.Text = string.Empty;
            RemoteTimerStatusText.Visibility = Visibility.Collapsed;
            return;
        }

        var timer = timers[0];
        var work = string.Join(
            " · ",
            new[] { timer.TaskName, timer.ProjectName, timer.ClientName }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(work))
        {
            work = "an entry";
        }
        var started = timer.StartedUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "an unknown time";
        var additional = timers.Count > 1 ? $" (+{timers.Count - 1} more)" : string.Empty;
        RemoteTimerStatusText.Text = $"{timer.DeviceName} is tracking {work} since {started}{additional}";
        RemoteTimerStatusText.ToolTip = "Read-only status from another synchronized computer. It is not the local running timer.";
        RemoteTimerStatusText.Visibility = Visibility.Visible;
    }

    private void ClearTimerTaskAfterStop()
    {
        _updatingTimerTaskSearch = true;
        try
        {
            TimerTaskCombo.IsDropDownOpen = false;
            TimerTaskCombo.SelectedIndex = -1;
            TimerTaskCombo.Text = string.Empty;
            if (_timerTaskEditor is not null)
            {
                _timerTaskEditor.Clear();
                _timerTaskEditor.Select(0, 0);
            }

            ApplyTimerTaskSearch(string.Empty, openDropDown: false);
        }
        finally
        {
            _updatingTimerTaskSearch = false;
        }
    }

    private async Task PersistTimerStartAsync()
    {
        if (!_timerStartTimeDirty)
        {
            return;
        }

        var running = _controller.RunningEntry;
        if (running is null)
        {
            SetTimerStartTimeText(null);
            return;
        }

        if (!RunningStartTimeText.TryResolve(
                TimerStartTimeText.Text,
                running.StartUtc,
                _controller.UtcNow,
                TimeZoneInfo.Local,
                out var startUtc))
        {
            throw new InvalidOperationException(
                "Enter a valid Start time that is not in the future.");
        }

        var updated = await _controller.UpdateRunningStartAsync(
            running.Id,
            startUtc);
        SetTimerStartTimeText(updated.StartUtc);
    }

    private void SetTimerStartTimeText(DateTimeOffset? startUtc)
    {
        _settingTimerStartTimeText = true;
        try
        {
            TimerStartTimeText.Text = startUtc is { } value
                ? TimeOfDayText.Format(
                    TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local).TimeOfDay)
                : string.Empty;
            _timerStartTimeDirty = false;
        }
        finally
        {
            _settingTimerStartTimeText = false;
        }
    }

    private void UpdateElapsed()
    {
        var running = _controller.RunningEntry;
        var duration = running is null ? TimeSpan.Zero : _controller.RunningElapsed;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        ElapsedText.Text = $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RefreshHistoryAsync();
    }

    private async void HistoryDateRangeChanged(object? sender, DateRangeChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loaded && !_loading && !_updatingHistoryFilters)
        {
            await RefreshHistoryAsync();
        }
    }

    private void HistoryDateRangeShortcut_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        ApplyDateRangeShortcut(HistoryRangePicker, sender as Button);
    }

    private void ReportDateRangeShortcut_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        ApplyDateRangeShortcut(ReportRangePicker, sender as Button);
    }

    private static void ApplyDateRangeShortcut(
        DateRangePicker picker,
        Button? button)
    {
        if (button?.Tag is not string presetName ||
            !Enum.TryParse<CalendarDateRangePreset>(
                presetName,
                ignoreCase: false,
                out var preset))
        {
            return;
        }

        var (start, end) = CalendarDateRangePresets.Resolve(
            DateTime.Today,
            preset);
        picker.SetRange(start, end);
    }

    private static void UpdateDateRangeShortcutStates(
        DateRangePicker picker,
        Button thisMonthButton,
        Button thisWeekButton,
        Button todayButton)
    {
        var startDate = picker.StartDate?.Date;
        var endDate = picker.EndDate?.Date;
        foreach (var (button, preset) in new[]
                 {
                     (thisMonthButton, CalendarDateRangePreset.ThisMonth),
                     (thisWeekButton, CalendarDateRangePreset.ThisWeek),
                     (todayButton, CalendarDateRangePreset.Today),
                 })
        {
            var expected = CalendarDateRangePresets.Resolve(DateTime.Today, preset);
            SetDateRangeShortcutActive(
                button,
                startDate == expected.Start && endDate == expected.End);
        }
    }

    private async Task RefreshHistoryAsync()
    {
        UpdateDateRangeShortcutStates(
            HistoryRangePicker,
            HistoryThisMonthButton,
            HistoryThisWeekButton,
            HistoryTodayButton);
        var (fromUtc, toUtc) = GetRange(HistoryRangePicker.StartDate, HistoryRangePicker.EndDate);
        var entries = await _store.GetEntriesAsync(fromUtc, toUtc);
        var overlapIds = TimeEntryOverlapDetector.FindOverlappingEntries(
            entries,
            _controller.UtcNow);
        _historyRows = entries
            .Select(entry => new TimeEntryRow(
                entry,
                _controller.UtcNow,
                _tagDefinitions,
                overlapIds.Contains(entry.Id)))
            .ToArray();
        UpdateTagOptions(HistoryTagCombo, _historyRows.SelectMany(row => row.TagList));
        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        var tag = (HistoryTagCombo.SelectedItem as TagOption)?.Value;
        var descriptionQuery = HistoryDescriptionFilterText.Text.Trim();
        IEnumerable<TimeEntryRow> filtered = _historyRows;
        if (_historyProjectFilterId is { } projectId)
        {
            filtered = filtered.Where(row => row.Entry.ProjectId == projectId);
        }

        if (_historyUnassignedOnly)
        {
            filtered = filtered.Where(row => row.Entry.TaskId is null);
        }
        else if (_historyTaskFilterId is { } taskId)
        {
            filtered = filtered.Where(row => row.Entry.TaskId == taskId);
        }

        if (tag is not null)
        {
            filtered = filtered.Where(row => row.TagList.Contains(tag, StringComparer.OrdinalIgnoreCase));
        }

        if (descriptionQuery.Length > 0)
        {
            filtered = filtered.Where(row =>
                row.DescriptionSource.Contains(descriptionQuery, StringComparison.OrdinalIgnoreCase));
        }

        var view = new ListCollectionView(SortHistoryRows(filtered).ToList());
        if (_historySortMemberPath is null || _historySortDirection is null)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TimeEntryRow.Day)));
        }

        HistoryGrid.ItemsSource = view;
    }

    private void HistoryGrid_Sorting(object? sender, DataGridSortingEventArgs e)
    {
        _ = sender;
        var memberPath = GetHistorySortMemberPath(e.Column);
        if (memberPath is null)
        {
            return;
        }

        e.Handled = true;
        var direction = string.Equals(
                _historySortMemberPath,
                memberPath,
                StringComparison.Ordinal) &&
                _historySortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        SetHistorySort(memberPath, direction);
    }

    private string? GetHistorySortMemberPath(DataGridColumn column)
    {
        if (ReferenceEquals(column, HistoryClientColumn)) return nameof(TimeEntryRow.Client);
        if (ReferenceEquals(column, HistoryProjectColumn)) return nameof(TimeEntryRow.Project);
        if (ReferenceEquals(column, HistoryTaskColumn)) return nameof(TimeEntryRow.Task);
        if (ReferenceEquals(column, HistoryDescriptionColumn)) return nameof(TimeEntryRow.Description);
        if (ReferenceEquals(column, HistoryTagsColumn)) return nameof(TimeEntryRow.Tags);
        if (ReferenceEquals(column, HistorySoftwareColumn)) return nameof(TimeEntryRow.Software);
        if (ReferenceEquals(column, HistoryStartColumn)) return nameof(TimeEntryRow.StartUtc);
        if (ReferenceEquals(column, HistoryEndColumn)) return nameof(TimeEntryRow.EndUtc);
        if (ReferenceEquals(column, HistoryDurationColumn)) return nameof(TimeEntryRow.NetDurationSeconds);
        if (ReferenceEquals(column, HistoryPaymentColumn)) return nameof(TimeEntryRow.Payment);
        return ReferenceEquals(column, HistoryStatusColumn) ? nameof(TimeEntryRow.Status) : null;
    }

    private IEnumerable<TimeEntryRow> SortHistoryRows(IEnumerable<TimeEntryRow> rows)
    {
        if (_historySortMemberPath is null || _historySortDirection is null)
        {
            return rows;
        }

        return _historySortMemberPath switch
        {
            nameof(TimeEntryRow.Client) => OrderHistoryRows(rows, row => row.Client),
            nameof(TimeEntryRow.Project) => OrderHistoryRows(rows, row => row.Project),
            nameof(TimeEntryRow.Task) => OrderHistoryRows(rows, row => row.Task),
            nameof(TimeEntryRow.Description) => OrderHistoryRows(rows, row => row.Description),
            nameof(TimeEntryRow.Tags) => OrderHistoryRows(rows, row => row.Tags),
            nameof(TimeEntryRow.Software) => OrderHistoryRows(rows, row => row.Software),
            nameof(TimeEntryRow.StartUtc) => OrderHistoryRows(rows, row => row.StartUtc),
            nameof(TimeEntryRow.EndUtc) => OrderHistoryRows(rows, row => row.EndUtc),
            nameof(TimeEntryRow.NetDurationSeconds) => OrderHistoryRows(rows, row => row.NetDurationSeconds),
            nameof(TimeEntryRow.Payment) => OrderHistoryRows(rows, row => row.Payment),
            nameof(TimeEntryRow.Status) => OrderHistoryRows(rows, row => row.Status),
            _ => rows,
        };
    }

    private IOrderedEnumerable<TimeEntryRow> OrderHistoryRows<T>(
        IEnumerable<TimeEntryRow> rows,
        Func<TimeEntryRow, T> selector)
    {
        var comparer = typeof(T) == typeof(string)
            ? (IComparer<T>)(object)StringComparer.OrdinalIgnoreCase
            : Comparer<T>.Default;
        var ordered = _historySortDirection == ListSortDirection.Ascending
            ? rows.OrderBy(selector, comparer)
            : rows.OrderByDescending(selector, comparer);
        return ordered.ThenByDescending(row => row.StartUtc);
    }

    private void SetHistorySort(string memberPath, ListSortDirection direction)
    {
        _historySortMemberPath = memberPath;
        _historySortDirection = direction;
        foreach (var column in HistoryGrid.Columns)
        {
            column.SortDirection = string.Equals(
                GetHistorySortMemberPath(column),
                memberPath,
                StringComparison.Ordinal)
                ? direction
                : null;
        }

        UpdateHistorySortControls();
        ApplyHistoryFilter();
    }

    private void ClearHistorySort()
    {
        _historySortMemberPath = null;
        _historySortDirection = null;
        foreach (var column in HistoryGrid.Columns)
        {
            column.SortDirection = null;
        }

        UpdateHistorySortControls();
        ApplyHistoryFilter();
    }

    private void UpdateHistorySortControls() =>
        HistoryClearSortingButton.Visibility = _historySortMemberPath is null
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void HistoryClearSorting_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ClearHistorySort();
    }

    private void HistoryTagChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loaded && !_loading && !_updatingTagFilters && !_updatingHistoryFilters)
        {
            ApplyHistoryFilter();
        }
    }

    private void HistoryDescriptionFilterText_TextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loaded && !_loading && !_updatingHistoryFilters)
        {
            ApplyHistoryFilter();
        }
    }

    private async void HistoryProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loaded || _loading || _updatingHistoryFilters)
        {
            return;
        }

        _historyProjectFilterId = (HistoryProjectCombo.SelectedItem as ProjectFilterOption)?.ProjectId;
        _historyTaskFilterId = null;
        _historyUnassignedOnly = false;
        UpdateHistoryFilterOptions();
        await RefreshHistoryAsync();
    }

    private void HistoryFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loaded || _loading || _updatingHistoryFilters)
        {
            return;
        }

        var task = HistoryTaskCombo.SelectedItem as TaskFilterOption;
        _historyTaskFilterId = task?.TaskId;
        _historyUnassignedOnly = task?.IsUnassigned == true;
        ApplyHistoryFilter();
    }

    private void UpdateHistoryFilterOptions()
    {
        var wasUpdating = _updatingHistoryFilters;
        _updatingHistoryFilters = true;
        try
        {
            var clientNames = _activeClients.ToDictionary(client => client.Id, client => client.Name);
            var duplicateProjectNames = _activeProjects
                .GroupBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Skip(1).Any())
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var projects = new[] { new ProjectFilterOption(null, null, "All projects") }
                .Concat(_activeProjects
                    .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(project => clientNames.GetValueOrDefault(project.ClientId), StringComparer.OrdinalIgnoreCase)
                    .Select(project => new ProjectFilterOption(
                        project.Id,
                        project.ClientId,
                        duplicateProjectNames.Contains(project.Name)
                            ? $"{project.Name} · {clientNames[project.ClientId]}"
                            : project.Name)))
                .ToArray();
            HistoryProjectCombo.ItemsSource = projects;
            HistoryProjectCombo.SelectedItem = projects.FirstOrDefault(option => option.ProjectId == _historyProjectFilterId) ?? projects[0];
            _historyProjectFilterId = (HistoryProjectCombo.SelectedItem as ProjectFilterOption)?.ProjectId;

            var tasks = new[]
                {
                    new TaskFilterOption(null, _historyProjectFilterId, "All tasks"),
                    new TaskFilterOption(null, _historyProjectFilterId, "Unassigned", IsUnassigned: true),
                }
                .Concat(_activeTasks
                    .Where(task => _historyProjectFilterId is null || task.ProjectId == _historyProjectFilterId)
                    .OrderBy(task => task.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(task => new TaskFilterOption(
                        task.Id,
                        task.ProjectId,
                        _historyProjectFilterId is null
                            ? $"{task.Name} · {_activeProjects.First(project => project.Id == task.ProjectId).Name}"
                            : task.Name)))
                .ToArray();
            HistoryTaskCombo.ItemsSource = tasks;
            HistoryTaskCombo.SelectedItem = tasks.FirstOrDefault(option =>
                option.TaskId == _historyTaskFilterId && option.IsUnassigned == _historyUnassignedOnly) ?? tasks[0];
            var selectedTask = HistoryTaskCombo.SelectedItem as TaskFilterOption;
            _historyTaskFilterId = selectedTask?.TaskId;
            _historyUnassignedOnly = selectedTask?.IsUnassigned == true;
        }
        finally
        {
            _updatingHistoryFilters = wasUpdating;
        }
    }

    private async void AddEntry_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var initialDate = _historyContextAddDate;
        _historyContextAddDate = null;
        var dialog = new EntryEditorWindow(
            _store,
            initialLocalDate: initialDate)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() == true && dialog.Result is { } result)
        {
            try
            {
                await _store.AddManualEntryAsync(result.ProjectId, result.TaskId, result.Description, result.StartUtc, result.EndUtc, result.IsPaid);
                _controller.NotifyDataChanged();
            }
            catch (Exception exception)
            {
                ShowError("Could not add the entry", exception);
            }
        }
    }

    private void HistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (GetDataGridRowItem<TimeEntryRow>(HistoryGrid, e) is not { } row)
        {
            return;
        }

        HistoryGrid.SelectedItem = row;
        e.Handled = true;
        EditSelectedEntry();
    }

    private void EditEntry_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        EditSelectedEntry();
    }

    private async void ContinueEntry_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var selectedRows = HistoryGrid.SelectedItems.OfType<TimeEntryRow>().ToArray();
        if (selectedRows.Length != 1)
        {
            MessageBox.Show(
                this,
                "Select one time entry to continue.",
                "Select one entry",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var row = selectedRows[0];
        if (GetContinueEntryUnavailableReason(row) is { } reason)
        {
            MessageBox.Show(
                this,
                reason,
                "Cannot continue entry",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            await ContinueHistoryEntryAsync(row);
        }
        catch (Exception exception)
        {
            ShowError("Could not continue the entry", exception);
        }
    }

    private string? GetContinueEntryUnavailableReason(TimeEntryRow row)
    {
        if (row.Entry.EndUtc is null || row.Entry.Id == _controller.RunningEntry?.Id)
        {
            return "This entry is already running.";
        }

        if (_activeProjects.All(project => project.Id != row.Entry.ProjectId))
        {
            return "This entry's project has been removed and cannot be continued.";
        }

        if (row.Entry.TaskId is { } taskId && _activeTasks.All(task => task.Id != taskId))
        {
            return "This entry's saved task has been removed and cannot be continued.";
        }

        return null;
    }

    private async Task ContinueHistoryEntryAsync(TimeEntryRow row)
    {
        await _timerActionGate.WaitAsync();
        try
        {
            await _controller.ContinueTimerAsync(
                row.Entry.ProjectId,
                row.Entry.TaskId,
                row.Entry.Description);
        }
        finally
        {
            _timerActionGate.Release();
        }
    }

    internal async Task ContinueHistoryEntryForPreviewAsync(Guid entryId)
    {
        var row = _historyRows.Single(candidate => candidate.Entry.Id == entryId);
        if (GetContinueEntryUnavailableReason(row) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        HistoryGrid.SelectedItem = row;
        await ContinueHistoryEntryAsync(row);
    }

    private async void EditSelectedEntry()
    {
        if (HistoryGrid.SelectedItem is not TimeEntryRow row)
        {
            MessageBox.Show(this, "Select a completed entry first.", "No entry selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (row.Entry.EndUtc is null)
        {
            MessageBox.Show(this, "Stop the running timer before editing its times.", "Timer is running", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new EntryEditorWindow(_store, row.Entry) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } result)
        {
            try
            {
                await _store.UpdateTimeEntryAsync(
                    row.Entry.Id,
                    result.ProjectId,
                    result.TaskId,
                    result.Description,
                    result.StartUtc,
                    result.EndUtc,
                    result.IsPaid,
                    result.ExcludedSeconds,
                    isCall: result.IsCall);
                _controller.NotifyDataChanged();
            }
            catch (Exception exception)
            {
                ShowError("Could not update the entry", exception);
            }
        }
    }

    private async void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (HistoryGrid.SelectedItem is not TimeEntryRow row || row.Entry.EndUtc is null)
        {
            MessageBox.Show(this, "Select a completed entry first.", "No entry selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, "Delete the selected time entry? This cannot be undone.", "Delete entry", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _store.DeleteTimeEntryAsync(row.Entry.Id);
        _controller.NotifyDataChanged();
    }

    private async void MarkSelectedPaid_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await SetSelectedEntriesPaidAsync(true);
    }

    private async void MarkSelectedUnpaid_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await SetSelectedEntriesPaidAsync(false);
    }

    private async Task SetSelectedEntriesPaidAsync(bool isPaid)
    {
        var entryIds = HistoryGrid.SelectedItems
            .OfType<TimeEntryRow>()
            .Select(row => row.Entry.Id)
            .Distinct()
            .ToArray();
        if (entryIds.Length == 0)
        {
            MessageBox.Show(this, "Select one or more time entries first.", "No entries selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await _store.SetEntriesPaidAsync(entryIds, isPaid);
            _controller.NotifyDataChanged();
        }
        catch (Exception exception)
        {
            ShowError("Could not update payment status", exception);
        }
    }

    private async void AddClient_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new TextInputDialog("New client", "Client name") { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await RunCrudAsync(() => _store.AddClientAsync(dialog.Value, "#E45C4A"), reloadRecognition: false);
        }
    }

    private void SelectListItemOnRightClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(ClientsGrid, source) is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
        else
        {
            ClientsGrid.UnselectAll();
        }
    }

    private async void ClientsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source ||
            FindDataContext<ClientProjectRow>(source) is not null ||
            ItemsControl.ContainerFromElement(ClientsGrid, source) is not ListBoxItem { DataContext: ClientRow row })
        {
            return;
        }

        ClientsGrid.SelectedItem = row;
        e.Handled = true;
        await RenameClientAsync(row);
    }

    private async void ClientProject_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2 ||
            sender is not FrameworkElement { DataContext: ClientProjectRow projectRow })
        {
            return;
        }

        e.Handled = true;
        var clientName = _reportClients.FirstOrDefault(client => client.Id == projectRow.Project.ClientId)?.Name
            ?? "Archived client";
        SelectProjectRow(projectRow.Project.Id);
        await EditProjectAsync(projectRow.Project, clientName);
    }

    private async void EditClientProject_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (GetClientProjectFromContextMenu(sender) is not { } row)
        {
            return;
        }

        await EditProjectAsync(row.Project, row.Client);
    }

    private async void RenameClientProject_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (GetClientProjectFromContextMenu(sender) is { } row)
        {
            await RenameProjectAsync(row);
        }
    }

    private async void ChangeClientProjectColor_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (GetClientProjectFromContextMenu(sender) is { } row)
        {
            await ChangeProjectColorAsync(row);
        }
    }

    private async void ArchiveClientProject_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (GetClientProjectFromContextMenu(sender) is { } row)
        {
            await ArchiveProjectAsync(row);
        }
    }

    private ProjectRow? GetClientProjectFromContextMenu(object sender)
    {
        if (sender is not MenuItem menuItem ||
            ItemsControl.ItemsControlFromItemContainer(menuItem) is not ContextMenu { PlacementTarget: FrameworkElement placementTarget } ||
            placementTarget.DataContext is not ClientProjectRow projectRow)
        {
            return null;
        }

        return SelectProjectRow(projectRow.Project.Id);
    }

    private async void RenameClient_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ClientsGrid.SelectedItem is not ClientRow row)
        {
            return;
        }

        await RenameClientAsync(row);
    }

    private async Task RenameClientAsync(ClientRow row)
    {
        var dialog = new TextInputDialog("Rename client", "Client name", row.Name) { Owner = this };
        if (dialog.ShowDialog() == true && !string.Equals(dialog.Value, row.Name, StringComparison.Ordinal))
        {
            await RunCrudAsync(() => _store.RenameClientAsync(row.Client.Id, dialog.Value), reloadRecognition: true);
        }
    }

    private async void ArchiveClient_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ClientsGrid.SelectedItem is not ClientRow row)
        {
            return;
        }

        if (_controller.RunningEntry is { } running &&
            _activeProjects.Any(project => project.Id == running.ProjectId && project.ClientId == row.Client.Id))
        {
            MessageBox.Show(this, "Stop the client’s active project timer before removing it.", "Timer is running", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmPermanentProjectRemoval(
                $"client ‘{row.Name}’ and all of its projects",
                "Every related time entry, task, target, rule, and project setting will be deleted."))
        {
            return;
        }

        await RunCrudAsync(() => _store.ArchiveClientAsync(row.Client.Id), reloadRecognition: true);
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var clients = await _store.GetClientsAsync();
        if (clients.Count == 0)
        {
            MessageBox.Show(this, "Create a client before adding a project.", "Client required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preferredClientId = (ProjectsGrid.SelectedItem as ProjectRow)?.Project.ClientId
            ?? (ClientsGrid.SelectedItem as ClientRow)?.Client.Id;
        var dialog = new NewProjectWindow(clients, preferredClientId) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } result)
        {
            await RunCrudAsync(
                () => _store.AddProjectAsync(result.ClientId, result.ProjectName, result.Color),
                reloadRecognition: true);
        }
    }

    private async void ChangeProjectColor_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var rows = GetSelectedRows<ProjectRow>(ProjectsGrid);
        if (rows.Length == 0)
        {
            return;
        }

        if (rows.Length == 1)
        {
            await ChangeProjectColorAsync(rows[0]);
            return;
        }

        var commonColor = rows
            .Select(row => row.Color)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        var dialog = new ProjectColorWindow(
            "Project color",
            $"{rows.Length} selected projects",
            commonColor.Length == 1 ? commonColor[0] : "#339CFF")
        {
            Owner = this,
        };
        if (dialog.ShowDialog() == true)
        {
            await RunCrudAsync(
                () => _store.BulkUpdateProjectsAsync(
                    rows.Select(row => row.Project.Id).ToArray(),
                    new ProjectBulkEdit(UpdateColor: true, Color: dialog.SelectedColorHex)),
                reloadRecognition: true);
        }
    }

    private async Task ChangeProjectColorAsync(ProjectRow row)
    {
        var dialog = new ProjectColorWindow(row.Project, row.Client) { Owner = this };
        if (dialog.ShowDialog() == true &&
            !string.Equals(dialog.SelectedColorHex, row.Color, StringComparison.OrdinalIgnoreCase))
        {
            await RunCrudAsync(
                () => _store.UpdateProjectColorAsync(row.Project.Id, dialog.SelectedColorHex),
                reloadRecognition: true);
        }
    }

    private async void RenameProject_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ProjectsGrid.SelectedItem is not ProjectRow row)
        {
            return;
        }

        await RenameProjectAsync(row);
    }

    private async Task RenameProjectAsync(ProjectRow row)
    {
        var dialog = new TextInputDialog("Rename project", "Project name", row.Name) { Owner = this };
        if (dialog.ShowDialog() == true && !string.Equals(dialog.Value, row.Name, StringComparison.Ordinal))
        {
            await RunCrudAsync(() => _store.RenameProjectAsync(row.Project.Id, dialog.Value), reloadRecognition: true);
        }
    }

    private async void ProjectSettings_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var rows = GetSelectedRows<ProjectRow>(ProjectsGrid);
        if (rows.Length == 0)
        {
            MessageBox.Show(this, "Select a project first.", "Project required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (rows.Length == 1)
        {
            await EditProjectAsync(rows[0].Project, rows[0].Client);
        }
        else
        {
            await BulkEditProjectsAsync(rows);
        }
    }

    private async Task BulkEditProjectsAsync(IReadOnlyList<ProjectRow> rows)
    {
        var clients = await _store.GetClientsAsync();
        var dialog = BulkEditWindow.ForProjects(
            rows.Select(row => row.Project).ToArray(),
            clients);
        dialog.Owner = this;
        if (dialog.ShowDialog() == true && dialog.ProjectEdit is { } edit)
        {
            await RunCrudAsync(
                () => _store.BulkUpdateProjectsAsync(
                    rows.Select(row => row.Project.Id).ToArray(),
                    edit),
                reloadRecognition: true);
        }
    }

    private async Task EditProjectAsync(Project project, string clientName)
    {
        var clients = await _store.GetClientsAsync();
        var activeDebtCancellations = await _store.GetProjectTargetDebtCancellationsAsync(project.Id);
        var projectTargets = (await _store.GetCustomTargetsAsync())
            .Where(target => target.ProjectId == project.Id &&
                OneTimeTargetLifecycle.IsVisible(target, _controller.UtcNow, TimeZoneInfo.Local))
            .ToArray();
        var dialog = new ProjectSettingsWindow(
            project,
            clientName,
            clients,
            activeDebtCancellations,
            projectTargets)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() == true && dialog.Result is { } result)
        {
            await RunCrudAsync(
                async () =>
                {
                    await _store.UpdateProjectDetailsAsync(
                        project.Id,
                        result.ClientId,
                        result.HourlyRate,
                        result.Currency,
                        result.CarryOverTargetDebtEnabled);
                    await _store.ReplaceProjectTargetsAsync(project.Id, result.Targets);
                    if (result.RestoreCanceledDebt)
                    {
                        await _store.RestoreProjectTargetDebtAsync(project.Id, _controller.UtcNow);
                    }
                },
                reloadRecognition: true);
        }
    }

    private async void ProjectsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (GetDataGridRowItem<ProjectRow>(ProjectsGrid, e) is not { } row)
        {
            return;
        }

        e.Handled = true;
        var rows = GetSelectedRows<ProjectRow>(ProjectsGrid);
        if (rows.Length > 1 && rows.Contains(row))
        {
            await BulkEditProjectsAsync(rows);
            return;
        }

        ProjectsGrid.SelectedItem = row;
        await EditProjectAsync(row.Project, row.Client);
    }

    private async void ArchiveProject_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var rows = GetSelectedRows<ProjectRow>(ProjectsGrid);
        if (rows.Length == 0)
        {
            return;
        }

        if (rows.Length == 1)
        {
            await ArchiveProjectAsync(rows[0]);
            return;
        }

        if (_controller.RunningEntry is { } running &&
            rows.Any(row => row.Project.Id == running.ProjectId))
        {
            MessageBox.Show(this, "Stop the selected project timer before removing these projects.", "Timer is running", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmPermanentBulkProjectRemoval(rows.Length))
        {
            return;
        }

        await RunCrudAsync(
            async () =>
            {
                foreach (var row in rows)
                {
                    await _store.ArchiveProjectAsync(row.Project.Id);
                }
            },
            reloadRecognition: true);
    }

    private async Task ArchiveProjectAsync(ProjectRow row)
    {
        if (_controller.RunningEntry?.ProjectId == row.Project.Id)
        {
            MessageBox.Show(this, "Stop the project timer before removing it.", "Timer is running", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmPermanentProjectRemoval(
                $"project ‘{row.Name}’",
                "Every related time entry, task, target, rule, and project setting will be deleted."))
        {
            return;
        }

        await RunCrudAsync(() => _store.ArchiveProjectAsync(row.Project.Id), reloadRecognition: true);
    }

    private async void FreezeProject_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ProjectsGrid.SelectedItem is not ProjectRow { Project.IsFrozen: false } row)
        {
            return;
        }

        await RunCrudAsync(
            () => _store.SetProjectFrozenAsync(row.Project.Id, isFrozen: true),
            reloadRecognition: true,
            dismissActiveRecognitionReminder: true);
    }

    private async void UnfreezeProject_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ProjectsGrid.SelectedItem is not ProjectRow { Project.IsFrozen: true } row)
        {
            return;
        }

        await RunCrudAsync(
            () => _store.SetProjectFrozenAsync(row.Project.Id, isFrozen: false),
            reloadRecognition: true);
    }

    private void FrozenProjectsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var current = source;
        while (current is not null && current is not ListBoxItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        list.SelectedItem = current is ListBoxItem item &&
                            ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), list)
            ? item.Content
            : null;
    }

    private async void UnfreezeFrozenProject_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (FrozenProjectsList.SelectedItem is not ProjectRow { Project.IsFrozen: true } row)
        {
            return;
        }

        await RunCrudAsync(
            () => _store.SetProjectFrozenAsync(row.Project.Id, isFrozen: false),
            reloadRecognition: true);
    }

    private async void AddCustomTarget_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowCustomTargetDialogAsync(target: null);
    }

    private async void EditCustomTarget_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (CustomTargetsGrid.SelectedItem is ITargetManagementRow row)
        {
            await EditTargetManagementRowAsync(row);
        }
    }

    private async void CustomTargetsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (GetDataGridRowItem<ITargetManagementRow>(CustomTargetsGrid, e) is not { } row)
        {
            return;
        }

        e.Handled = true;
        CustomTargetsGrid.SelectedItem = row;
        await EditTargetManagementRowAsync(row);
    }

    private async void DeleteCustomTarget_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (CustomTargetsGrid.SelectedItem is not ITargetManagementRow row ||
            MessageBox.Show(
                this,
                $"Permanently delete target ‘{row.Name}’?\n\nNo target data will be retained. This cannot be undone.",
                "Delete target",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await DeleteTargetManagementRowAsync(row);
    }

    private async Task EditTargetManagementRowAsync(ITargetManagementRow row)
    {
        if (row is CustomTargetRow customTarget)
        {
            await ShowCustomTargetDialogAsync(customTarget.Target);
        }
    }

    private async Task DeleteTargetManagementRowAsync(ITargetManagementRow row)
    {
        if (row is not CustomTargetRow customTarget)
        {
            return;
        }

        await RunCrudAsync(
            () => _store.DeleteCustomTargetAsync(customTarget.Target.Id),
            reloadRecognition: false);
    }

    private async Task ShowCustomTargetDialogAsync(CustomTarget? target)
    {
        var dialog = new TargetSettingsWindow(_projectOptions, target) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result)
        {
            return;
        }

        if (target is null)
        {
            await RunCrudAsync(
                () => _store.AddCustomTargetAsync(
                    result.Name,
                    result.ProjectId,
                    result.Cadence,
                    result.TargetHours,
                    result.DurationMetric),
                reloadRecognition: false);
            return;
        }

        await RunCrudAsync(
            () => _store.UpdateCustomTargetAsync(
                target.Id,
                result.Name,
                result.ProjectId,
                result.Cadence,
                result.TargetHours,
                result.DurationMetric),
            reloadRecognition: false);
    }

    private async Task RefreshTrelloAsync()
    {
        var connection = await _trelloSync.GetConnectionAsync();
        var connected = connection is not null;
        TrelloConnectButton.Content = connected ? "Reconnect" : "Connect";
        TrelloSyncButton.IsEnabled = connected;
        TrelloDisconnectButton.IsEnabled = connected;
        TrelloMappingsGrid.IsEnabled = connected;
        if (connection is null)
        {
            TrelloConnectionText.Text = "Not connected";
            TrelloSyncStatusText.Text = "Connect Trello to import assigned cards.";
            _trelloMappingRows = [];
            TrelloMappingsGrid.ItemsSource = _trelloMappingRows;
            return;
        }

        TrelloConnectionText.Text = $"Connected as {connection.DisplayName} (@{connection.Username})";
        if (connection.RequiresReconnect)
        {
            TrelloSyncStatusText.Text = "Authorization needs attention. Reconnect this profile to resume synchronization.";
            TrelloSyncStatusText.Foreground = (Brush)FindResource("DangerBrush");
        }
        else if (!string.IsNullOrWhiteSpace(connection.LastError))
        {
            TrelloSyncStatusText.Text = $"Last sync failed: {connection.LastError}";
            TrelloSyncStatusText.Foreground = (Brush)FindResource("DangerBrush");
        }
        else if (connection.LastSuccessfulSyncUtc is { } lastSync)
        {
            TrelloSyncStatusText.Text = $"Last synchronized {lastSync.ToLocalTime():g}. Automatic refresh runs every 15 minutes.";
            TrelloSyncStatusText.Foreground = (Brush)FindResource("MutedBrush");
        }
        else
        {
            TrelloSyncStatusText.Text = "Connected. Add a board mapping to import tasks.";
            TrelloSyncStatusText.Foreground = (Brush)FindResource("MutedBrush");
        }

        var clients = _activeClients.ToDictionary(client => client.Id, client => client.Name);
        var projects = _activeProjects.ToDictionary(project => project.Id);
        _trelloMappingRows = (await _trelloSync.GetMappingsAsync())
            .Select(mapping =>
            {
                var project = projects.GetValueOrDefault(mapping.ProjectId);
                return new TrelloMappingRow(
                    mapping,
                    project?.Name ?? "Removed project",
                    project is null ? "Removed client" : clients.GetValueOrDefault(project.ClientId, "Removed client"));
            })
            .OrderBy(row => row.Board, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        TrelloMappingsGrid.ItemsSource = _trelloMappingRows;
    }

    private async void TrelloConnect_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new TrelloConnectionWindow(_trelloSync) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                _ = await _trelloSync.SyncNowAsync();
                _controller.NotifyDataChanged();
            }
            catch (Exception exception)
            {
                ShowError("Trello connected, but the first sync failed", exception);
            }

            await RefreshTrelloAsync();
        }
    }

    private async void TrelloSync_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        TrelloSyncButton.IsEnabled = false;
        TrelloSyncStatusText.Text = "Synchronizing Trello tasks…";
        TrelloSyncStatusText.Foreground = (Brush)FindResource("ContentSecondaryBrush");
        try
        {
            var result = await _trelloSync.SyncNowAsync();
            _controller.NotifyDataChanged();
            await RefreshTrelloAsync();
            TrelloSyncStatusText.Text =
                $"Synchronized {result.ImportedCount} new, {result.UpdatedCount} existing, " +
                $"{result.DetachedCount} detached, and {result.DeletedCount} unused tasks.";
        }
        catch (Exception exception)
        {
            ShowError("Could not synchronize Trello", exception);
            await RefreshTrelloAsync();
        }
        finally
        {
            TrelloSyncButton.IsEnabled = await _trelloSync.GetConnectionAsync() is not null;
        }
    }

    private async void TrelloDisconnect_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (MessageBox.Show(
                this,
                "Disconnect Trello from this profile? Linked tasks with time history become local tasks; unused linked tasks are removed.",
                "Disconnect Trello",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _trelloSync.DisconnectAsync();
            _controller.NotifyDataChanged();
            await RefreshTrelloAsync();
        }
        catch (Exception exception)
        {
            ShowError("Could not disconnect Trello", exception);
        }
    }

    private async void AddTrelloMapping_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await EditTrelloMappingAsync(null);
    }

    private async void EditTrelloMapping_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (TrelloMappingsGrid.SelectedItem is TrelloMappingRow row)
        {
            await EditTrelloMappingAsync(row.Mapping);
        }
    }

    private async Task EditTrelloMappingAsync(TrelloBoardMapping? mapping)
    {
        if (_projectOptions.Count == 0)
        {
            MessageBox.Show(this, "Create a project before adding a Trello mapping.", "Project required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new TrelloMappingWindow(_trelloSync, _projectOptions, mapping) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result)
        {
            return;
        }

        try
        {
            await _trelloSync.SaveMappingAsync(result);
            _controller.NotifyDataChanged();
            await RefreshTrelloAsync();
        }
        catch (Exception exception)
        {
            ShowError("Could not save Trello mapping", exception);
        }
    }

    private async void RemoveTrelloMapping_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (TrelloMappingsGrid.SelectedItem is not TrelloMappingRow row ||
            MessageBox.Show(
                this,
                $"Remove the mapping for ‘{row.Board}’? Tasks with time history become local tasks; unused linked tasks are removed.",
                "Remove Trello mapping",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _trelloSync.RemoveMappingAsync(row.Mapping.Id);
            _controller.NotifyDataChanged();
            await RefreshTrelloAsync();
        }
        catch (Exception exception)
        {
            ShowError("Could not remove Trello mapping", exception);
        }
    }

    private async void TrelloMappingsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (GetDataGridRowItem<TrelloMappingRow>(TrelloMappingsGrid, e) is not { } row)
        {
            return;
        }

        e.Handled = true;
        TrelloMappingsGrid.SelectedItem = row;
        await EditTrelloMappingAsync(row.Mapping);
    }

    private void TrelloMappingsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _ = e;
        if (sender is DataGrid { ContextMenu: { } menu } grid)
        {
            ConfigureRowOrEmptyContextMenu(menu, grid.SelectedItem is TrelloMappingRow, "TrelloMappingOnly");
        }
    }

    private async void AddTask_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_projectOptions.Count == 0)
        {
            MessageBox.Show(this, "Create a project before adding a task.", "Project required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preferredProjectId = _taskProjectFilterId
            ?? (ProjectsGrid.SelectedItem as ProjectRow)?.Project.Id
            ?? (TimerProjectCombo.SelectedValue is Guid timerProjectId ? timerProjectId : (Guid?)null);
        var dialog = new NewTaskWindow(_projectOptions, preferredProjectId) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } result)
        {
            await RunCrudAsync(() => _store.AddTaskAsync(result.ProjectId, result.TaskName), reloadRecognition: false);
        }
    }

    private void TargetProjectFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loaded || _loading || _updatingTargetFilter)
        {
            return;
        }

        var option = TargetProjectCombo.SelectedItem as TargetProjectFilterOption;
        _targetProjectFilterId = option?.ProjectId;
        _targetGlobalOnly = option?.IsGlobal == true;
        ApplyTargetFilter();
    }

    private void UpdateTargetFilterOptions()
    {
        var options = new[]
            {
                new TargetProjectFilterOption(null, "All projects"),
                new TargetProjectFilterOption(null, "Global targets", IsGlobal: true),
            }
            .Concat(_projectOptions
                .OrderBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(project => project.ClientName, StringComparer.OrdinalIgnoreCase)
                .Select(project => new TargetProjectFilterOption(
                    project.ProjectId,
                    $"{project.ProjectName} \u00B7 {project.ClientName}")))
            .ToArray();

        _updatingTargetFilter = true;
        try
        {
            TargetProjectCombo.ItemsSource = options;
            TargetProjectCombo.SelectedItem = options.FirstOrDefault(option =>
                option.ProjectId == _targetProjectFilterId &&
                option.IsGlobal == _targetGlobalOnly) ?? options[0];
            var selected = TargetProjectCombo.SelectedItem as TargetProjectFilterOption;
            _targetProjectFilterId = selected?.ProjectId;
            _targetGlobalOnly = selected?.IsGlobal == true;
        }
        finally
        {
            _updatingTargetFilter = false;
        }
    }

    private void ApplyTargetFilter()
    {
        IEnumerable<ITargetManagementRow> filtered = _targetManagementRows;
        if (_targetGlobalOnly)
        {
            filtered = filtered.Where(row => GetTargetProjectId(row) is null);
        }
        else if (_targetProjectFilterId is { } projectId)
        {
            filtered = filtered.Where(row => GetTargetProjectId(row) == projectId);
        }

        CustomTargetsGrid.ItemsSource = filtered.ToArray();
    }

    private void ResetTargetProjectFilter()
    {
        _updatingTargetFilter = true;
        try
        {
            _targetProjectFilterId = null;
            _targetGlobalOnly = false;
            TargetProjectCombo.SelectedIndex = TargetProjectCombo.Items.Count == 0 ? -1 : 0;
        }
        finally
        {
            _updatingTargetFilter = false;
        }

        ApplyTargetFilter();
    }

    private static Guid? GetTargetProjectId(ITargetManagementRow row) => row switch
    {
        CustomTargetRow custom => custom.Target.ProjectId,
        _ => null,
    };

    private void TaskProjectFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loaded || _loading || _updatingTaskFilter)
        {
            return;
        }

        _taskProjectFilterId =
            (TaskProjectCombo.SelectedItem as ProjectFilterOption)?.ProjectId;
        ApplyTaskFilter();
    }

    private void UpdateTaskFilterOptions()
    {
        var options = new[] { new ProjectFilterOption(null, null, "All projects") }
            .Concat(_projectOptions
                .OrderBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(project => project.ClientName, StringComparer.OrdinalIgnoreCase)
                .Select(project => new ProjectFilterOption(
                    project.ProjectId,
                    project.ClientId,
                    $"{project.ProjectName} · {project.ClientName}")))
            .ToArray();

        _updatingTaskFilter = true;
        try
        {
            TaskProjectCombo.ItemsSource = options;
            TaskProjectCombo.SelectedItem = options.FirstOrDefault(option =>
                option.ProjectId == _taskProjectFilterId) ?? options[0];
            _taskProjectFilterId =
                (TaskProjectCombo.SelectedItem as ProjectFilterOption)?.ProjectId;
        }
        finally
        {
            _updatingTaskFilter = false;
        }
    }

    private void ApplyTaskFilter()
    {
        IEnumerable<TaskRow> filtered = _taskRows;
        if (_taskProjectFilterId is { } projectId)
        {
            filtered = filtered.Where(row => row.ProjectId == projectId);
        }

        TasksGrid.ItemsSource = filtered.ToArray();
    }

    private void ResetTaskProjectFilter()
    {
        _updatingTaskFilter = true;
        try
        {
            _taskProjectFilterId = null;
            TaskProjectCombo.SelectedIndex = TaskProjectCombo.Items.Count == 0 ? -1 : 0;
        }
        finally
        {
            _updatingTaskFilter = false;
        }

        ApplyTaskFilter();
    }

    private async void RenameTask_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var rows = GetSelectedRows<TaskRow>(TasksGrid);
        if (rows.Length != 1)
        {
            return;
        }

        if (rows[0].IsTrelloLinked)
        {
            OpenTaskInTrello(rows[0]);
            return;
        }

        await RenameTaskAsync(rows[0].Task, rows[0].Name);
    }

    private async void EditTasks_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var rows = GetSelectedRows<TaskRow>(TasksGrid);
        if (rows.Length == 0)
        {
            return;
        }

        await BulkEditTasksAsync(rows);
    }

    private async Task BulkEditTasksAsync(IReadOnlyList<TaskRow> rows)
    {
        if (rows.Any(row => row.IsTrelloLinked))
        {
            MessageBox.Show(
                this,
                "Trello-linked tasks stay in their mapped project. Edit the board mapping or open the card in Trello.",
                "Task is linked to Trello",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = BulkEditWindow.ForTasks(
            rows.Select(row => row.Task).ToArray(),
            _projectOptions);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true || dialog.TaskEdit is not { } edit)
        {
            return;
        }

        if (edit.UpdateProject &&
            edit.ProjectId is Guid targetProjectId &&
            _controller.RunningEntry is { } running &&
            rows.Any(row => row.Task.Id == running.TaskId) &&
            running.ProjectId != targetProjectId)
        {
            MessageBox.Show(
                this,
                "The running task cannot be moved to another project. Stop its timer first.",
                "Task is in use",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await RunCrudAsync(
            () => _store.BulkUpdateTasksAsync(
                rows.Select(row => row.Task.Id).ToArray(),
                edit),
            reloadRecognition: false);
    }

    private async Task RenameTaskAsync(SavedTask task, string currentName)
    {
        var dialog = new TextInputDialog("Rename task", "Task name", currentName) { Owner = this };
        if (dialog.ShowDialog() == true && !string.Equals(dialog.Value, currentName, StringComparison.Ordinal))
        {
            await RunCrudAsync(() => _store.RenameTaskAsync(task.Id, dialog.Value), reloadRecognition: false);
        }
    }

    private async void TasksGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (GetDataGridRowItem<TaskRow>(TasksGrid, e) is not { } row)
        {
            return;
        }

        e.Handled = true;
        if (row.IsTrelloLinked)
        {
            TasksGrid.SelectedItem = row;
            OpenTaskInTrello(row);
            return;
        }

        var rows = GetSelectedRows<TaskRow>(TasksGrid);
        if (rows.Length > 1 && rows.Contains(row))
        {
            await BulkEditTasksAsync(rows);
            return;
        }

        TasksGrid.SelectedItem = row;
        await RenameTaskAsync(row.Task, row.Name);
    }

    private void OpenTaskInTrello_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (GetSelectedRows<TaskRow>(TasksGrid) is [var row])
        {
            OpenTaskInTrello(row);
        }
    }

    private static void OpenTaskInTrello(TaskRow row)
    {
        if (!row.IsTrelloLinked ||
            !Uri.TryCreate(row.ExternalUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return;
        }

        _ = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private async void ArchiveTask_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var rows = GetSelectedRows<TaskRow>(TasksGrid);
        if (rows.Length == 0)
        {
            return;
        }

        if (_controller.RunningEntry is { } running &&
            rows.Any(row => row.Task.Id == running.TaskId))
        {
            MessageBox.Show(this, "Stop the timer or choose another task before removing the selected tasks.", "Task is in use", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (rows.Length == 1)
        {
            if (!ConfirmRemove($"task ‘{rows[0].Name}’"))
            {
                return;
            }
        }
        else if (!ConfirmBulkRemove(rows.Length, "tasks"))
        {
            return;
        }

        await RunCrudAsync(
            async () =>
            {
                foreach (var row in rows)
                {
                    await _store.ArchiveTaskAsync(row.Task.Id);
                }
            },
            reloadRecognition: false);
    }

    private async void RenameTag_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var rows = GetSelectedRows<TagRow>(TagsGrid);
        if (rows.Length != 1)
        {
            return;
        }

        await RenameTagAsync(rows[0]);
    }

    private async void AddTag_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new TagSettingsWindow(
            _projectOptions,
            suggestedColor: CreateSuggestedTagColor())
        {
            Owner = this,
        };
        if (dialog.ShowDialog() == true && dialog.Result is { } result)
        {
            await RunCrudAsync(
                () => _store.AddTagAsync(result.Name, result.Color, result.ProjectId),
                reloadRecognition: false);
        }
    }

    private async void EditTags_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var rows = GetSelectedRows<TagRow>(TagsGrid);
        if (rows.Length == 0)
        {
            return;
        }

        if (rows.Length == 1)
        {
            await EditTagAsync(rows[0]);
        }
        else
        {
            await BulkEditTagsAsync(rows);
        }
    }

    private async Task EditTagAsync(TagRow row)
    {
        var dialog = new TagSettingsWindow(_projectOptions, row.Tag)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result)
        {
            return;
        }

        await RunCrudAsync(
            async () =>
            {
                if (result.PreserveExistingScope)
                {
                    await _store.RenameTagAsync(row.Tag.Id, result.Name);
                    await _store.UpdateTagColorAsync(row.Tag.Id, result.Color);
                }
                else
                {
                    await _store.UpdateTagAsync(
                        row.Tag.Id,
                        result.Name,
                        result.Color,
                        result.ProjectId);
                }
                if (!string.Equals(row.Name, result.Name, StringComparison.OrdinalIgnoreCase) &&
                    _controller.RunningEntry is { } running &&
                    TagParser.Contains(running.Description, row.Name))
                {
                    await _controller.SaveRunningDetailsAsync(
                        running.TaskId,
                        TagParser.Rename(running.Description, row.Name, result.Name));
                }
            },
            reloadRecognition: false);
    }

    private string CreateSuggestedTagColor()
    {
        string[] palette =
        [
            "#339CFF", "#40C977", "#FB6A22", "#B57CFF", "#F45D9A",
            "#34C6C8", "#E0B94C", "#6F8BFF", "#D46B62", "#7DBB52",
        ];
        var used = _tagDefinitions
            .Select(tag => tag.Color)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return palette.FirstOrDefault(color => !used.Contains(color))
            ?? $"#{Random.Shared.Next(0x303030, 0xE0E0E0):X6}";
    }

    private async Task BulkEditTagsAsync(IReadOnlyList<TagRow> rows)
    {
        var dialog = BulkEditWindow.ForTags(
            rows.Select(row => row.Tag).ToArray());
        dialog.Owner = this;
        if (dialog.ShowDialog() == true && dialog.TagEdit is { } edit)
        {
            await RunCrudAsync(
                () => _store.BulkUpdateTagsAsync(
                    rows.Select(row => row.Tag.Id).ToArray(),
                    edit),
                reloadRecognition: false);
        }
    }

    private async Task RenameTagAsync(TagRow row)
    {
        var dialog = new TextInputDialog(
            "Rename tag everywhere",
            "Tag name (all matching logs will be updated)",
            row.Name) { Owner = this };
        if (dialog.ShowDialog() == true && !string.Equals(dialog.Value, row.Name, StringComparison.OrdinalIgnoreCase))
        {
            await RunCrudAsync(
                async () =>
                {
                    await _store.RenameTagAsync(row.Tag.Id, dialog.Value);
                    if (_controller.RunningEntry is { } running && TagParser.Contains(running.Description, row.Name))
                    {
                        await _controller.SaveRunningDetailsAsync(
                            running.TaskId,
                            TagParser.Rename(running.Description, row.Name, dialog.Value));
                    }
                },
                reloadRecognition: false);
        }
    }

    private async void TagsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (GetDataGridRowItem<TagRow>(TagsGrid, e) is not { } row)
        {
            return;
        }

        e.Handled = true;
        var rows = GetSelectedRows<TagRow>(TagsGrid);
        if (rows.Length > 1 && rows.Contains(row))
        {
            await BulkEditTagsAsync(rows);
            return;
        }

        TagsGrid.SelectedItem = row;
        await EditTagAsync(row);
    }

    private async void ChangeTagColor_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var rows = GetSelectedRows<TagRow>(TagsGrid);
        if (rows.Length == 0)
        {
            return;
        }

        var colors = rows
            .Select(row => row.Color)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        var dialog = new ProjectColorWindow(
            "Tag color",
            rows.Length == 1 ? rows[0].Name : $"{rows.Length} selected tags",
            colors.Length == 1 ? colors[0] : "#339CFF")
        {
            Owner = this,
        };
        if (dialog.ShowDialog() == true)
        {
            await RunCrudAsync(
                () => _store.BulkUpdateTagsAsync(
                    rows.Select(row => row.Tag.Id).ToArray(),
                    new TagBulkEdit(UpdateColor: true, Color: dialog.SelectedColorHex)),
                reloadRecognition: false);
        }
    }

    private async void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var rows = GetSelectedRows<TagRow>(TagsGrid);
        if (rows.Length == 0 ||
            MessageBox.Show(
                this,
                rows.Length == 1
                    ? $"Remove the tag ‘{rows[0].Name}’?\n\nIn every existing description, #{rows[0].Name} will become ordinary text. Other tags will not be changed."
                    : $"Remove {rows.Length} selected tags?\n\nIn every existing description, their # markers will be removed and their names will remain as ordinary text. Other tags will not be changed.",
                rows.Length == 1 ? "Remove tag" : "Remove tags",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunCrudAsync(
            async () =>
            {
                var running = _controller.RunningEntry;
                var convertedDescription = running?.Description;
                foreach (var row in rows)
                {
                    convertedDescription = TagParser.ConvertToText(convertedDescription, row.Name);
                    await _store.DeleteTagAsync(row.Tag.Id);
                }

                if (running is not null &&
                    !string.Equals(running.Description, convertedDescription, StringComparison.Ordinal))
                {
                    _controller.NotifyEntryDetailsChanged(
                        running.Id,
                        running.TaskId,
                        convertedDescription);
                }
            },
            reloadRecognition: false);
    }

    private async void EditSoftware_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (SoftwareGrid.SelectedItem is SoftwareRow row)
        {
            await ShowSoftwareDialogAsync(row);
        }
    }

    private async void AddSoftware_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowSoftwareDialogAsync(null);
    }

    private async void RemoveSoftwareFromList_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (SoftwareGrid.SelectedItem is SoftwareRow row)
        {
            await RemoveSoftwareFromListAsync(row, requireConfirmation: true);
        }
    }

    internal async Task RemoveSoftwareFromListForPreviewAsync(Guid softwareId)
    {
        var row = _softwareRows.FirstOrDefault(candidate => candidate.Software.Id == softwareId)
            ?? throw new InvalidOperationException("The software smoke row is not visible.");
        await RemoveSoftwareFromListAsync(row, requireConfirmation: false);
    }

    private async Task RemoveSoftwareFromListAsync(SoftwareRow row, bool requireConfirmation)
    {
        if (requireConfirmation && MessageBox.Show(
                this,
                $"Remove ‘{row.Software.Label}’ from the Software list?\n\n" +
                "Its software label will be removed from every historical entry and monthly log. " +
                "Global/project exclusions and correlated tags for this process will also be removed. " +
                "Adding the process later starts with no historical associations.",
                "Remove software",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _store.RemoveSoftwareFromListAsync(row.Software.Id);
            await _controller.ReloadSoftwareSettingsAsync();
            await RefreshAllAsync();
        }
        catch (Exception exception)
        {
            ShowError("Could not remove software", exception);
        }
    }

    private async void SoftwareGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (GetDataGridRowItem<SoftwareRow>(SoftwareGrid, e) is not { } row)
        {
            return;
        }

        e.Handled = true;
        SoftwareGrid.SelectedItem = row;
        await ShowSoftwareDialogAsync(row);
    }

    private async Task ShowSoftwareDialogAsync(SoftwareRow? row)
    {
        var dialog = new SoftwareSettingsWindow(
            row?.Setting,
            _tagDefinitions,
            _projectOptions,
            _softwareProjectFilterId,
            () => _controller.CurrentActivity)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var selectedTagIds = await ResolveSoftwareTagIdsAsync(
                dialog.SelectedTagNames,
                dialog.ProjectId);
            if (row is null)
            {
                await _store.AddSoftwareAsync(
                    dialog.ProcessName,
                    dialog.Label,
                    dialog.ProjectId,
                    dialog.IsExcluded,
                    selectedTagIds);
            }
            else
            {
                await _store.UpdateSoftwareAsync(
                    row.Software.Id,
                    row.ProjectId,
                    dialog.Label,
                    dialog.IsExcluded,
                    selectedTagIds);
            }

            await _controller.ReloadSoftwareSettingsAsync();
            await RefreshAllAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                row is null ? "Could not add software" : "Could not update software",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task<IReadOnlyList<Guid>> ResolveSoftwareTagIdsAsync(
        IEnumerable<string> tagNames,
        Guid softwareScopeId)
    {
        var ids = new List<Guid>();
        var tagProjectId = softwareScopeId == SystemEntityIds.GlobalSoftwareScopeId
            ? (Guid?)null
            : softwareScopeId;
        foreach (var name in tagNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ids.Add((await _store.GetOrAddTagAsync(name, tagProjectId)).Id);
        }

        return ids;
    }

    internal Task<IReadOnlyList<Guid>> ResolveSoftwareTagNamesForPreviewAsync(
        IEnumerable<string> tagNames,
        Guid? softwareScopeId = null) => ResolveSoftwareTagIdsAsync(
            tagNames,
            softwareScopeId ?? SystemEntityIds.GlobalSoftwareScopeId);

    private void SoftwareProjectFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loaded || _loading || _updatingSoftwareFilter)
        {
            return;
        }

        _softwareProjectFilterId =
            (SoftwareProjectCombo.SelectedItem as ProjectFilterOption)?.ProjectId;
        ApplySoftwareFilter();
    }

    private void UpdateSoftwareFilterOptions()
    {
        var options = new[] { new ProjectFilterOption(null, null, "All projects") }
            .Concat(_projectOptions
                .OrderBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(project => project.ClientName, StringComparer.OrdinalIgnoreCase)
                .Select(project => new ProjectFilterOption(
                    project.ProjectId,
                    project.ClientId,
                    $"{project.ProjectName} \u00B7 {project.ClientName}")))
            .ToArray();

        _updatingSoftwareFilter = true;
        try
        {
            SoftwareProjectCombo.ItemsSource = options;
            SoftwareProjectCombo.SelectedItem = options.FirstOrDefault(option =>
                option.ProjectId == _softwareProjectFilterId) ?? options[0];
            _softwareProjectFilterId =
                (SoftwareProjectCombo.SelectedItem as ProjectFilterOption)?.ProjectId;
        }
        finally
        {
            _updatingSoftwareFilter = false;
        }
    }

    private void ApplySoftwareFilter()
    {
        IEnumerable<SoftwareRow> filtered = _softwareRows;
        if (_softwareProjectFilterId is { } projectId)
        {
            filtered = filtered.Where(row =>
                row.ProjectId == projectId || row.IsGlobal);
        }

        SoftwareGrid.ItemsSource = filtered
            .OrderBy(row => row.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Client, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Process, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_projectOptions.Count == 0)
        {
            MessageBox.Show(this, "Create a project before adding a window rule.", "Project required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ShowRuleDialogAsync(null);
    }

    private void RuleProjectFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loaded || _loading || _updatingRuleFilter)
        {
            return;
        }

        _ruleProjectFilterId = (RuleProjectCombo.SelectedItem as ProjectFilterOption)?.ProjectId;
        ApplyRuleFilter();
    }

    private void UpdateRuleFilterOptions()
    {
        var options = new[] { new ProjectFilterOption(null, null, "All projects") }
            .Concat(_projectOptions
                .OrderBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(project => project.ClientName, StringComparer.OrdinalIgnoreCase)
                .Select(project => new ProjectFilterOption(
                    project.ProjectId,
                    project.ClientId,
                    $"{project.ProjectName} · {project.ClientName}")))
            .ToArray();

        _updatingRuleFilter = true;
        try
        {
            RuleProjectCombo.ItemsSource = options;
            RuleProjectCombo.SelectedItem = options.FirstOrDefault(option =>
                option.ProjectId == _ruleProjectFilterId) ?? options[0];
            _ruleProjectFilterId = (RuleProjectCombo.SelectedItem as ProjectFilterOption)?.ProjectId;
        }
        finally
        {
            _updatingRuleFilter = false;
        }
    }

    private void ApplyRuleFilter()
    {
        IEnumerable<RuleRow> filtered = _ruleRows;
        if (_ruleProjectFilterId is { } projectId)
        {
            filtered = filtered.Where(row => row.ProjectId == projectId);
        }

        var view = new ListCollectionView(filtered.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RuleRow.ProjectGroup)));
        RulesGrid.ItemsSource = view;
    }

    private async void EditRule_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var rows = GetSelectedRows<RuleRow>(RulesGrid);
        if (rows.Length == 1)
        {
            await ShowRuleDialogAsync(rows[0]);
        }
        else if (rows.Length > 1)
        {
            await BulkEditRulesAsync(rows);
        }
    }

    private async Task BulkEditRulesAsync(IReadOnlyList<RuleRow> rows)
    {
        var dialog = BulkEditWindow.ForRules(
            rows.Select(row => row.Rule).ToArray(),
            _projectOptions);
        dialog.Owner = this;
        if (dialog.ShowDialog() == true && dialog.RuleEdit is { } edit)
        {
            await RunCrudAsync(
                () => _store.BulkUpdateRulesAsync(
                    rows.Select(row => row.Rule.Id).ToArray(),
                    edit),
                reloadRecognition: true);
        }
    }

    private async void RulesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var current = source;
        while (current is not null && current is not DataGridRow)
        {
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        if (current is DataGridRow { Item: RuleRow row })
        {
            e.Handled = true;
            var rows = GetSelectedRows<RuleRow>(RulesGrid);
            if (rows.Length > 1 && rows.Contains(row))
            {
                await BulkEditRulesAsync(rows);
            }
            else
            {
                RulesGrid.SelectedItem = row;
                await ShowRuleDialogAsync(row);
            }
        }
    }

    private void RulesGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is DataGrid grid)
        {
            ApplyRuleGridDefaultColumnWidths(grid);
        }
    }

    private void RulesGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _ = e;
        if (sender is DataGrid grid)
        {
            ApplyRuleGridDefaultColumnWidths(grid);
        }
    }

    private void ApplyRuleGridDefaultColumnWidths(DataGrid grid)
    {
        if (_ruleGridDefaultColumnsApplied || grid.Columns.Count != 2 || grid.ActualWidth <= 0)
        {
            return;
        }

        _ruleGridDefaultColumnsApplied = true;
        var availableWidth = Math.Max(0, grid.ActualWidth - SystemParameters.VerticalScrollBarWidth);
        var titleWidth = Math.Max(360d, Math.Floor(availableWidth / 2));
        var applicationWidth = Math.Max(0, availableWidth - titleWidth);
        grid.Columns[0].Width = new DataGridLength(titleWidth, DataGridLengthUnitType.Pixel);
        grid.Columns[1].Width = new DataGridLength(applicationWidth, DataGridLengthUnitType.Pixel);
    }

    private async Task ShowRuleDialogAsync(RuleRow? existing)
    {
        var preferredProjectId = existing?.Rule.ProjectId
            ?? (RuleProjectCombo.SelectedItem as ProjectFilterOption)?.ProjectId
            ?? (ProjectsGrid.SelectedItem as ProjectRow)?.Project.Id
            ?? (TimerProjectCombo.SelectedValue is Guid timerProjectId ? timerProjectId : (Guid?)null);
        var dialog = new RuleDialog(
            _projectOptions,
            preferredProjectId,
            existing?.Rule.TitlePattern ?? string.Empty,
            existing?.Rule.ProcessName,
            () => _controller.CurrentActivity,
            isEditing: existing is not null)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.ProjectId is not { } projectId)
        {
            return;
        }

        if (existing is null)
        {
            await RunCrudAsync(
                () => _store.AddRuleAsync(projectId, dialog.TitlePattern, dialog.ProcessName),
                reloadRecognition: true);
        }
        else
        {
            await RunCrudAsync(
                () => _store.UpdateRuleAsync(existing.Rule.Id, projectId, dialog.TitlePattern, dialog.ProcessName),
                reloadRecognition: true);
        }
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var rows = GetSelectedRows<RuleRow>(RulesGrid);
        if (rows.Length == 0)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                rows.Length == 1
                    ? $"Remove the window rule ‘{rows[0].TitlePattern}’?"
                    : $"Remove {rows.Length} selected window rules?",
                rows.Length == 1 ? "Remove rule" : "Remove rules",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunCrudAsync(
            async () =>
            {
                foreach (var row in rows)
                {
                    await _store.DeleteRuleAsync(row.Rule.Id);
                }
            },
            reloadRecognition: true);
    }

    private async Task RunCrudAsync<T>(
        Func<Task<T>> action,
        bool reloadRecognition,
        bool dismissActiveRecognitionReminder = false)
    {
        try
        {
            await action();
            if (reloadRecognition)
            {
                await _controller.ReloadRecognitionAsync(dismissActiveRecognitionReminder);
            }
            else
            {
                _controller.NotifyDataChanged();
            }
        }
        catch (Exception exception)
        {
            ShowError("Could not save the change", exception);
        }
    }

    private async Task RunCrudAsync(
        Func<Task> action,
        bool reloadRecognition,
        bool dismissActiveRecognitionReminder = false)
    {
        try
        {
            await action();
            if (reloadRecognition)
            {
                await _controller.ReloadRecognitionAsync(dismissActiveRecognitionReminder);
            }
            else
            {
                _controller.NotifyDataChanged();
            }
        }
        catch (Exception exception)
        {
            ShowError("Could not save the change", exception);
        }
    }

    private async void RefreshReport_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RefreshReportAsync();
    }

    private async void EditReportProject_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ReportGrid.SelectedItem is ProjectReportSummaryRow project)
        {
            await EditProjectByIdAsync(project.ProjectId);
        }
    }

    private async void ReportGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (e.ChangedButton != MouseButton.Left ||
            e.OriginalSource is not DependencyObject source ||
            FindDataContext<ReportTaskSummaryRow>(source) is not null ||
            ItemsControl.ContainerFromElement(ReportGrid, source) is not ListBoxItem
            {
                DataContext: ProjectReportSummaryRow project,
            })
        {
            return;
        }

        ReportGrid.SelectedItem = project;
        e.Handled = true;
        await EditProjectByIdAsync(project.ProjectId);
    }

    private void SelectReportProjectOnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        list.SelectedItem = ItemsControl.ContainerFromElement(list, source) is ListBoxItem
        {
            DataContext: ProjectReportSummaryRow project,
        }
            ? project
            : null;
    }

    private async void EditReportTask_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (GetReportTaskFromContext(sender) is { TaskId: { } taskId })
        {
            await EditTaskByIdAsync(taskId);
        }
    }

    private async void ReportTaskGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid taskGrid)
        {
            return;
        }

        var task = GetDataGridRowItem<ReportTaskSummaryRow>(taskGrid, e);
        if (task?.TaskId is not Guid taskId)
        {
            return;
        }

        taskGrid.SelectedItem = task;
        e.Handled = true;
        await EditTaskByIdAsync(taskId);
    }

    private async void EditTargetProject_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (GetTargetFromContextMenu(sender) is { } target)
        {
            await EditTargetSummaryRowAsync(target);
        }
    }

    private void SelectTargetOnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        list.SelectedItem = ItemsControl.ContainerFromElement(list, source) is ListBoxItem item
            ? item.DataContext
            : null;
    }

    private async void TargetsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(list, source) is not ListBoxItem { DataContext: ProjectTargetRow target })
        {
            return;
        }

        list.SelectedItem = target;
        e.Handled = true;
        await EditTargetSummaryRowAsync(target);
    }

    private async Task EditTargetSummaryRowAsync(ProjectTargetRow target)
    {
        if (target.IsGlobalAggregate)
        {
            MainTabs.SelectedIndex = 1;
            ManagementTabs.SelectedIndex = 2;
            TargetProjectCombo.SelectedItem = TargetProjectCombo.Items
                .OfType<TargetProjectFilterOption>()
                .FirstOrDefault(option => option.IsGlobal);
            return;
        }

        if (target.CustomTarget is { } customTarget)
        {
            await ShowCustomTargetDialogAsync(customTarget);
            return;
        }

        await EditProjectAsync(target.Project, target.ClientName);
    }

    private async void CancelTargetDebt_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (!TryGetTargetDebtContext(sender, out var project, out var debt))
        {
            return;
        }

        var amount = TargetDebtText.Format(debt.OutstandingSeconds);
        if (MessageBox.Show(
                this,
                $"Cancel {amount} of carried debt for '{project.Name}'?\n\n" +
                "The cancellation date will be remembered. You can bring the debt back later by editing this target.",
                "Cancel target debt",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunCrudAsync(
            async () =>
            {
                _ = await _store.CancelProjectTargetDebtAsync(
                    project.Id,
                    debt.OutstandingSeconds,
                    _controller.UtcNow);
            },
            reloadRecognition: false);
    }

    private async void LowerTargetDebt_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (!TryGetTargetDebtContext(sender, out var project, out var debt))
        {
            return;
        }

        var outstandingText = TargetDebtText.Format(debt.OutstandingSeconds);
        var initialValue = string.Empty;
        long reductionSeconds;
        while (true)
        {
            var dialog = new TextInputDialog(
                "Lower target debt",
                $"Lower {outstandingText} by (hours or H:MM)",
                initialValue)
            {
                Owner = this,
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            initialValue = dialog.Value;
            if (TryParseDebtReduction(dialog.Value, debt.OutstandingSeconds, out reductionSeconds))
            {
                break;
            }

            MessageBox.Show(
                this,
                $"Enter a positive amount smaller than the current {outstandingText} debt. " +
                "Use decimal hours such as 1.5, or a duration such as 1:30. " +
                "Use Cancel debt to remove the full amount.",
                "Invalid debt reduction",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        await RunCrudAsync(
            async () =>
            {
                _ = await _store.CancelProjectTargetDebtAsync(
                    project.Id,
                    reductionSeconds,
                    _controller.UtcNow);
            },
            reloadRecognition: false);
    }

    private static bool TryGetTargetDebtContext(
        object sender,
        out Project project,
        out ProjectTargetDebt debt)
    {
        project = null!;
        debt = null!;
        if (sender is not MenuItem menuItem ||
            ItemsControl.ItemsControlFromItemContainer(menuItem) is not ContextMenu contextMenu)
        {
            return false;
        }

        switch (contextMenu.PlacementTarget)
        {
            case ListBox { SelectedItem: ProjectTargetRow target } when
                target.CanCancelDebt && target.TargetDebt is { OutstandingSeconds: > 0 } listDebt:
                project = target.Project;
                debt = listDebt;
                return true;
            case DataGrid { SelectedItem: CustomTargetRow target } when
                target.CanCancelDebt &&
                target.ScopedProject is { } scopedProject &&
                target.TargetDebt is { OutstandingSeconds: > 0 } gridDebt:
                project = scopedProject;
                debt = gridDebt;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseDebtReduction(string text, long outstandingSeconds, out long reductionSeconds)
    {
        reductionSeconds = 0;
        var trimmed = text.Trim();
        double totalSeconds;
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var hours) ||
            double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out hours))
        {
            totalSeconds = hours * 3600d;
        }
        else if (TimeSpan.TryParse(trimmed, CultureInfo.CurrentCulture, out var duration) ||
                 TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out duration))
        {
            totalSeconds = duration.TotalSeconds;
        }
        else
        {
            return false;
        }

        if (!double.IsFinite(totalSeconds) || totalSeconds <= 0 || totalSeconds >= outstandingSeconds)
        {
            return false;
        }

        reductionSeconds = (long)Math.Round(totalSeconds, MidpointRounding.AwayFromZero);
        return reductionSeconds > 0 && reductionSeconds < outstandingSeconds;
    }

    internal static void VerifyTargetDebtReductionInputForPreview()
    {
        const long sixHours = 6 * 3600;
        if (!TryParseDebtReduction("1.5", sixHours, out var decimalHours) ||
            decimalHours != 90 * 60 ||
            !TryParseDebtReduction("1:30", sixHours, out var clockDuration) ||
            clockDuration != 90 * 60 ||
            TryParseDebtReduction("0", sixHours, out _) ||
            TryParseDebtReduction("6", sixHours, out _) ||
            TryParseDebtReduction("not a duration", sixHours, out _))
        {
            throw new InvalidOperationException(
                "Target debt reduction input does not accept valid hours/durations or reject invalid/full amounts.");
        }
    }

    private void RequireTargetSelection_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is not ProjectTargetRow target)
        {
            e.Handled = true;
            return;
        }

        if (list.ContextMenu is { } menu)
        {
            SetContextMenuActionVisibility(menu, "Lower debt...", target.CanCancelDebt);
            SetContextMenuActionVisibility(menu, "Cancel debt", target.CanCancelDebt);
        }
    }

    private ProjectTargetRow? GetTargetFromContextMenu(object sender)
    {
        if (sender is MenuItem menuItem &&
            ItemsControl.ItemsControlFromItemContainer(menuItem) is ContextMenu { PlacementTarget: ListBox list } &&
            list.SelectedItem is ProjectTargetRow target)
        {
            return target;
        }

        return TargetsGrid.SelectedItem as ProjectTargetRow;
    }

    private async Task EditProjectByIdAsync(Guid projectId)
    {
        var project = _reportProjects.FirstOrDefault(item => item.Id == projectId);
        if (project is null)
        {
            return;
        }

        SelectProjectRow(projectId);
        var clientName = _reportClients.FirstOrDefault(client => client.Id == project.ClientId)?.Name
            ?? "Archived client";
        await EditProjectAsync(project, clientName);
    }

    private async Task EditTaskByIdAsync(Guid taskId)
    {
        var task = _reportTasks.FirstOrDefault(item => item.Id == taskId);
        if (task is null)
        {
            return;
        }

        SelectTaskRow(taskId);
        await RenameTaskAsync(task, task.Name);
    }

    private async void ShowReportProject_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ReportGrid.SelectedItem is ProjectReportSummaryRow project)
        {
            await ShowInHistoryAsync(project.ProjectId, null, unassignedOnly: false);
        }
    }

    private async void ShowReportTask_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (GetReportTaskFromContext(sender) is not { } task)
        {
            return;
        }

        await ShowInHistoryAsync(task.ProjectId, task.TaskId, task.IsUnassigned);
    }

    private async void SetReportProjectPaid_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ReportGrid.SelectedItem is ProjectReportSummaryRow project)
        {
            await SetReportEntriesPaidAsync(
                project.ProjectId,
                null,
                unassignedOnly: false,
                $"project ‘{project.Project}’");
        }
    }

    private async void SetReportTaskPaid_Click(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (GetReportTaskFromContext(sender) is { } task)
        {
            await SetReportEntriesPaidAsync(
                task.ProjectId,
                task.TaskId,
                task.IsUnassigned,
                $"task ‘{task.Task}’");
        }
    }

    private static ReportTaskSummaryRow? GetReportTaskFromContext(object sender)
    {
        if (sender is MenuItem menuItem &&
            ItemsControl.ItemsControlFromItemContainer(menuItem) is ContextMenu { PlacementTarget: DataGrid taskGrid } &&
            taskGrid.SelectedItem is ReportTaskSummaryRow task)
        {
            return task;
        }

        return null;
    }

    private async Task SetReportEntriesPaidAsync(
        Guid projectId,
        Guid? taskId,
        bool unassignedOnly,
        string scopeName)
    {
        try
        {
            var (fromUtc, toUtc) = GetRange(ReportRangePicker.StartDate, ReportRangePicker.EndDate);
            var filter = GetSelectedReportFilter();
            var entries = await _store.GetEntriesAsync(fromUtc, toUtc);
            var entryIds = entries
                .Where(entry => !entry.IsPaid)
                .Where(entry => MatchesReportFilter(entry, filter))
                .Where(entry => entry.ProjectId == projectId)
                .Where(entry => unassignedOnly
                    ? entry.TaskId is null
                    : taskId is null || entry.TaskId == taskId)
                .Select(entry => entry.Id)
                .Distinct()
                .ToArray();

            if (entryIds.Length == 0)
            {
                MessageBox.Show(
                    this,
                    "No unpaid logs match this report row, date range, and current filters.",
                    "Nothing to update",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var fromDate = (ReportRangePicker.StartDate ?? DateTime.Today).Date;
            var toDate = (ReportRangePicker.EndDate ?? fromDate).Date;
            var logLabel = entryIds.Length == 1 ? "log" : "logs";
            if (MessageBox.Show(
                    this,
                    $"Set {entryIds.Length} unpaid {logLabel} in {scopeName} as paid?\n\n" +
                    $"Range: {AppTextCulture.FormatShortDate(fromDate)} – {AppTextCulture.FormatShortDate(toDate)}",
                    "Set as paid",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            await _store.SetEntriesPaidAsync(entryIds, isPaid: true);
            _controller.NotifyDataChanged();
        }
        catch (Exception exception)
        {
            ShowError("Could not update payment status", exception);
        }
    }

    private async Task ShowInHistoryAsync(Guid projectId, Guid? taskId, bool unassignedOnly)
    {
        if (_activeProjects.All(project => project.Id != projectId))
        {
            MessageBox.Show(
                this,
                "This project has been removed and is no longer available as a History filter.",
                "Project removed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (taskId is { } selectedTaskId && _activeTasks.All(task => task.Id != selectedTaskId))
        {
            MessageBox.Show(
                this,
                "This task has been removed and is no longer available as a History filter.",
                "Task removed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _updatingHistoryFilters = true;
        try
        {
            _historyProjectFilterId = projectId;
            _historyTaskFilterId = taskId;
            _historyUnassignedOnly = unassignedOnly;
            var reportStart = ReportRangePicker.StartDate ?? DateTime.Today;
            var reportEnd = ReportRangePicker.EndDate ?? reportStart;
            HistoryRangePicker.SetRange(reportStart, reportEnd, notify: false);
            HistoryDescriptionFilterText.Clear();
            UpdateHistoryFilterOptions();
            if (HistoryTagCombo.ItemsSource is IEnumerable<TagOption> tags)
            {
                HistoryTagCombo.SelectedItem = tags.FirstOrDefault(tag => tag.Value is null);
            }

            if (MainTabs.SelectedIndex != 0)
            {
                _preserveHistoryFiltersOnNextTabEntry = true;
                MainTabs.SelectedIndex = 0;
            }
        }
        finally
        {
            _updatingHistoryFilters = false;
        }

        await RefreshHistoryAsync();
    }

    private async Task RefreshReportAsync()
    {
        UpdateDateRangeShortcutStates(
            ReportRangePicker,
            ReportThisMonthButton,
            ReportThisWeekButton,
            ReportTodayButton);
        var (fromUtc, toUtc) = GetRange(ReportRangePicker.StartDate, ReportRangePicker.EndDate);
        var entries = await _store.GetEntriesAsync(fromUtc, toUtc);
        UpdateTagOptions(ReportTagCombo, entries.SelectMany(entry => TagParser.Extract(entry.Description)));
        var filter = GetSelectedReportFilter();
        var rows = await _store.GetReportAsync(fromUtc, toUtc, filter);
        var projectRows = BuildProjectReportRows(rows);
        var filteredProjectId = (ReportProjectCombo.SelectedItem as ProjectFilterOption)?.ProjectId
            ?? (ReportTaskCombo.SelectedItem as TaskFilterOption)?.ProjectId;
        _reportTargetProjectId = filteredProjectId
            ?? (_reportTargetProjectId is { } currentProjectId &&
                projectRows.Any(row => row.ProjectId == currentProjectId)
                    ? currentProjectId
                    : projectRows.FirstOrDefault()?.ProjectId);
        ReportGrid.ItemsSource = projectRows;
        ReportGrid.SelectedItem = projectRows.FirstOrDefault(row => row.ProjectId == _reportTargetProjectId);
        ReportGrid.UpdateLayout();
        ApplyReportViewToVisibleElements();
        ReportLegendItems.ItemsSource = projectRows;
        UpdateReportDonut(projectRows);
        ReportInclusiveLegendItems.ItemsSource = projectRows;
        UpdateReportInclusiveDonut(projectRows);

        var fromDate = (ReportRangePicker.StartDate ?? DateTime.Today).Date;
        var toDate = (ReportRangePicker.EndDate ?? fromDate).Date;
        ReportChartRangeText.Text =
            $"{AppTextCulture.FormatShortDate(fromDate)} – {AppTextCulture.FormatShortDate(toDate)}";

        ReportInclusiveChartRangeText.Text =
            ReportChartRangeText.Text +
            $" · idle intervals up to {_controller.ShortIdleReportingMaximumMinutes} min";

        var day = TrackingPeriodCalculator.CurrentDay(_controller.UtcNow, TimeZoneInfo.Local);
        var week = TrackingPeriodCalculator.CurrentWeek(_controller.UtcNow, TimeZoneInfo.Local);
        var month = TrackingPeriodCalculator.CurrentMonth(_controller.UtcNow, TimeZoneInfo.Local);
        var dailyRows = await _store.GetReportAsync(day.StartUtc, day.EndUtc);
        var weeklyRows = await _store.GetReportAsync(week.StartUtc, week.EndUtc);
        var monthlyRows = await _store.GetReportAsync(month.StartUtc, month.EndUtc);
        var clientRows = BuildClientReportRows(monthlyRows);
        ReportClientLegendItems.ItemsSource = clientRows;
        UpdateReportClientDonut(clientRows);
        var monthStartDate = TimeZoneInfo.ConvertTime(month.StartUtc, TimeZoneInfo.Local).Date;
        var monthEndDate = TimeZoneInfo.ConvertTime(
            month.EndUtc.AddTicks(-1),
            TimeZoneInfo.Local).Date;
        ReportClientChartRangeText.Text =
            $"Current month · {AppTextCulture.FormatShortDate(monthStartDate)} – " +
            AppTextCulture.FormatShortDate(monthEndDate);
        await RefreshTargetsAsync(dailyRows, weeklyRows, monthlyRows);

    }

    private ProjectReportSummaryRow[] BuildProjectReportRows(IReadOnlyList<ReportRow> rows)
    {
        var grouped = rows
            .GroupBy(row => (row.ProjectId, row.ClientName, row.ProjectName))
            .Select(group => new
            {
                ProjectId = group.Key.ProjectId,
                Client = group.Key.ClientName,
                Project = group.Key.ProjectName,
                TotalSeconds = group.Sum(row => row.DurationSeconds),
                TotalWithShortIdleSeconds = group.Sum(row => row.DurationWithShortIdleSeconds),
                CallSeconds = group.Sum(row => row.CallDurationSeconds),
                PaidSeconds = group.Sum(row => row.PaidDurationSeconds),
                UnpaidSeconds = group.Sum(row => row.UnpaidDurationSeconds),
                EntryCount = group.Sum(row => row.EntryCount),
                Value = FormatValueTotals(group, unpaidOnly: false),
                Tasks = group
                    .OrderByDescending(row => row.LatestActivityUtc)
                    .ThenBy(row => row.TaskName, StringComparer.OrdinalIgnoreCase)
                    .Select(row => new ReportTaskSummaryRow(
                        row.ProjectId,
                        row.TaskId,
                        row.TaskName,
                        row.DurationSeconds,
                        row.DurationWithShortIdleSeconds,
                        row.PaidDurationSeconds,
                        row.UnpaidDurationSeconds,
                        row.EntryCount,
                        row.HourlyRate,
                        row.Currency,
                        row.LatestActivityUtc,
                        row.CallDurationSeconds))
                    .ToArray(),
            })
            .OrderByDescending(row => row.TotalSeconds)
            .ThenBy(row => row.Project, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var totalSeconds = grouped.Sum(row => row.TotalSeconds);
        var fallbackColors = new[] { "#FF7356", "#6EA8FF", "#9D7BFF", "#52C7A4", "#F1B35A", "#E873A4", "#63C3E8", "#B6CE5A" };

        return grouped
            .Select((row, index) => new ProjectReportSummaryRow(
                row.ProjectId,
                row.Client,
                row.Project,
                _projectOptions.FirstOrDefault(option => option.ProjectId == row.ProjectId)?.Color
                    ?? fallbackColors[index % fallbackColors.Length],
                row.TotalSeconds,
                row.TotalWithShortIdleSeconds,
                row.PaidSeconds,
                row.UnpaidSeconds,
                row.EntryCount,
                row.Value,
                totalSeconds == 0 ? 0 : row.TotalSeconds * 100d / totalSeconds,
                row.Tasks,
                row.CallSeconds))
            .ToArray();
    }

    private void UpdateReportDonut(IReadOnlyList<ProjectReportSummaryRow> rows)
    {
        var totalSeconds = rows.Sum(row => row.TotalSeconds);
        ReportDonutTotalHours.Text = FormatReportChartDuration(totalSeconds);
        ReportDonutImage.Source = CreateDonutDrawing(
            rows,
            totalSeconds,
            row => row.TotalSeconds,
            row => row.Color);
    }

    private void UpdateReportInclusiveDonut(IReadOnlyList<ProjectReportSummaryRow> rows)
    {
        var totalSeconds = rows.Sum(row => row.TotalWithShortIdleSeconds);
        ReportInclusiveDonutTotalHours.Text = FormatReportChartDuration(totalSeconds);
        ReportInclusiveDonutImage.Source = CreateDonutDrawing(
            rows,
            totalSeconds,
            row => row.TotalWithShortIdleSeconds,
            row => row.Color);
    }

    private static ClientReportSummaryRow[] BuildClientReportRows(
        IReadOnlyList<ReportRow> rows)
    {
        var grouped = rows
            .GroupBy(row => row.ClientName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Client = group.First().ClientName,
                TotalSeconds = group.Sum(row => row.DurationSeconds),
            })
            .ToArray();
        var totalSeconds = grouped.Sum(row => row.TotalSeconds);
        var colors = grouped
            .OrderBy(row => row.Client, StringComparer.OrdinalIgnoreCase)
            .Select((row, index) => (
                row.Client,
                Color: ReportClientChartColors[index % ReportClientChartColors.Length]))
            .ToDictionary(item => item.Client, item => item.Color, StringComparer.OrdinalIgnoreCase);

        return grouped
            .OrderByDescending(row => row.TotalSeconds)
            .ThenBy(row => row.Client, StringComparer.OrdinalIgnoreCase)
            .Select(row => new ClientReportSummaryRow(
                row.Client,
                colors[row.Client],
                row.TotalSeconds,
                totalSeconds == 0 ? 0 : row.TotalSeconds * 100d / totalSeconds))
            .ToArray();
    }

    private void UpdateReportClientDonut(IReadOnlyList<ClientReportSummaryRow> rows)
    {
        var totalSeconds = rows.Sum(row => row.TotalSeconds);
        ReportClientDonutTotalHours.Text = FormatReportChartDuration(totalSeconds);
        ReportClientDonutImage.Source = CreateDonutDrawing(
            rows,
            totalSeconds,
            row => row.TotalSeconds,
            row => row.Color);
    }

    private static string FormatReportChartDuration(long totalSeconds) =>
        $"{totalSeconds / 3600}:{totalSeconds % 3600 / 60:00} h";

    private static DrawingImage CreateDonutDrawing<T>(
        IReadOnlyList<T> rows,
        long totalSeconds,
        Func<T, long> getSeconds,
        Func<T, string> getColor)
    {
        const double size = 160;
        const double radius = 58;
        const double thickness = 24;
        var center = new Point(size / 2, size / 2);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            null,
            new Pen(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), thickness),
            new EllipseGeometry(center, radius, radius)));

        if (totalSeconds > 0)
        {
            var startAngle = -90d;
            foreach (var row in rows.Where(row => getSeconds(row) > 0))
            {
                var rowSeconds = getSeconds(row);
                var sweep = rowSeconds * 360d / totalSeconds;
                var brush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(getColor(row)));
                var pen = new Pen(brush, thickness)
                {
                    StartLineCap = PenLineCap.Flat,
                    EndLineCap = PenLineCap.Flat,
                };

                if (sweep >= 359.999)
                {
                    group.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(center, radius, radius)));
                }
                else
                {
                    var gap = Math.Min(2d, sweep / 3d);
                    var arcStart = startAngle + gap / 2d;
                    var arcSweep = Math.Max(0.1d, sweep - gap);
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        context.BeginFigure(PointOnCircle(center, radius, arcStart), isFilled: false, isClosed: false);
                        context.ArcTo(
                            PointOnCircle(center, radius, arcStart + arcSweep),
                            new Size(radius, radius),
                            0,
                            arcSweep > 180,
                            SweepDirection.Clockwise,
                            isStroked: true,
                            isSmoothJoin: false);
                    }

                    geometry.Freeze();
                    group.Children.Add(new GeometryDrawing(null, pen, geometry));
                }

                startAngle += sweep;
            }
        }

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private async Task RefreshTargetsAsync(
        IReadOnlyList<ReportRow> dailyRows,
        IReadOnlyList<ReportRow> weeklyRows,
        IReadOnlyList<ReportRow> monthlyRows)
    {
        _ = dailyRows;
        _ = weeklyRows;
        _ = monthlyRows;
        var activeCancellationsByProject = (await _store.GetProjectTargetDebtCancellationsAsync())
            .GroupBy(item => item.ProjectId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ProjectTargetDebtCancellation>)group.ToArray());
        var debtsByProject = (await _store.GetProjectTargetDebtsAsync(
                _controller.UtcNow,
                TimeZoneInfo.Local))
            .ToDictionary(debt => debt.ProjectId);
        foreach (var (projectId, cancellations) in activeCancellationsByProject)
        {
            if (debtsByProject.TryGetValue(projectId, out var debt))
            {
                debtsByProject[projectId] = debt with { Cancellations = cancellations };
            }
            else
            {
                debtsByProject[projectId] = ProjectTargetDebt.None(projectId) with
                {
                    Cancellations = cancellations,
                };
            }
        }
        _customTargetRows = _customTargetRows
            .Select(row => row with
            {
                TargetDebt = row.Target.ProjectId is { } projectId
                    ? debtsByProject.GetValueOrDefault(projectId)
                    : null,
            })
            .ToArray();
        _sidebarTargetRows = BuildSidebarTargetRows(
            dailyRows,
            weeklyRows,
            monthlyRows,
            debtsByProject);
        _allTargetRows = _customTargetRows
            .Select(ProjectTargetRow.FromCustomTarget)
            .OrderBy(row => row.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Client, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        TargetsGrid.ItemsSource = _sidebarTargetRows;
        FloatingTargetsGrid.ItemsSource = _sidebarTargetRows;

        var frozenProjectIds = _activeProjects
            .Where(project => project.IsFrozen)
            .Select(project => project.Id)
            .ToHashSet();
        var targetManagementRows = _customTargetRows
            .Cast<ITargetManagementRow>()
            .OrderBy(row => row.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _targetManagementRows = targetManagementRows
            .Where(row => row is not CustomTargetRow target ||
                          target.Target.ProjectId is not { } projectId ||
                          !frozenProjectIds.Contains(projectId))
            .ToArray();
        _frozenTargetManagementRows = targetManagementRows
            .Where(row => row is CustomTargetRow { Target.ProjectId: { } projectId } && frozenProjectIds.Contains(projectId))
            .ToArray();
        FrozenTargetsList.ItemsSource = _frozenTargetManagementRows;
        UpdateTargetFilterOptions();
        ApplyTargetFilter();

        UpdateReportTargetsList();
    }

    private ProjectTargetRow[] BuildSidebarTargetRows(
        IReadOnlyList<ReportRow> dailyRows,
        IReadOnlyList<ReportRow> weeklyRows,
        IReadOnlyList<ReportRow> monthlyRows,
        IReadOnlyDictionary<Guid, ProjectTargetDebt> debtsByProject)
    {
        var projectRows = _customTargetRows
            .Where(row => row.Target.ProjectId is not null && row.ScopedProject is not null)
            .GroupBy(row => row.Target.ProjectId!.Value)
            .Select(group =>
            {
                var first = group.First();
                var project = first.ScopedProject!;
                var targets = group.ToArray();
                var dailyTarget = SumTargetHours(targets, CustomTargetCadence.Daily);
                var weeklyTarget = SumTargetHours(targets, CustomTargetCadence.Weekly);
                var monthlyTarget = SumTargetHours(targets, CustomTargetCadence.Monthly);
                var oneTimeTargets = targets
                    .Where(row => row.Target.Cadence == CustomTargetCadence.OneTime)
                    .ToArray();
                var summaryProject = project with
                {
                    DailyTargetHours = dailyTarget,
                    WeeklyTargetHours = weeklyTarget,
                    MonthlyTargetHours = monthlyTarget,
                };
                return new ProjectTargetRow(
                    summaryProject,
                    first.Client,
                    GetCadenceTargetSeconds(targets, CustomTargetCadence.Daily, dailyRows, project.Id),
                    GetCadenceTargetSeconds(targets, CustomTargetCadence.Weekly, weeklyRows, project.Id),
                    GetCadenceTargetSeconds(targets, CustomTargetCadence.Monthly, monthlyRows, project.Id),
                    debtsByProject.GetValueOrDefault(project.Id),
                    OneTimeSeconds: oneTimeTargets.Sum(row => row.CompletedSeconds),
                    OneTimeTargetHoursOverride: SumTargetHours(
                        oneTimeTargets,
                        CustomTargetCadence.OneTime));
            });

        var globalTargets = _customTargetRows
            .Where(row => row.Target.ProjectId is null)
            .ToArray();
        ProjectTargetRow[] globalRows = [];
        if (globalTargets.Length > 0)
        {
            var oneTimeTargets = globalTargets
                .Where(row => row.Target.Cadence == CustomTargetCadence.OneTime)
                .ToArray();
            var globalProject = new Project(
                SystemEntityIds.GlobalSoftwareScopeId,
                SystemEntityIds.UnassignedClientId,
                "All projects",
                "#766F80",
                DailyTargetHours: SumTargetHours(globalTargets, CustomTargetCadence.Daily),
                WeeklyTargetHours: SumTargetHours(globalTargets, CustomTargetCadence.Weekly),
                MonthlyTargetHours: SumTargetHours(globalTargets, CustomTargetCadence.Monthly));
            globalRows =
            [
                new ProjectTargetRow(
                    globalProject,
                    "Every client",
                    GetCadenceTargetSeconds(globalTargets, CustomTargetCadence.Daily, dailyRows),
                    GetCadenceTargetSeconds(globalTargets, CustomTargetCadence.Weekly, weeklyRows),
                    GetCadenceTargetSeconds(globalTargets, CustomTargetCadence.Monthly, monthlyRows),
                    DisplayNameOverride: "All projects",
                    ScopeOverride: "Every client",
                    OneTimeSeconds: oneTimeTargets.Sum(row => row.CompletedSeconds),
                    OneTimeTargetHoursOverride: SumTargetHours(
                        oneTimeTargets,
                        CustomTargetCadence.OneTime),
                    IsGlobalAggregate: true),
            ];
        }

        return projectRows
            .Concat(globalRows)
            .OrderBy(row => row.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Client, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static long GetCadenceTargetSeconds(
        IReadOnlyCollection<CustomTargetRow> targets,
        CustomTargetCadence cadence,
        IReadOnlyList<ReportRow> reportRows,
        Guid? projectId = null)
    {
        var cadenceTargets = targets
            .Where(row => row.Target.Cadence == cadence)
            .ToArray();
        var useShortIdle = cadenceTargets.Length > 0 &&
            cadenceTargets.All(row =>
                row.Target.DurationMetric == TargetDurationMetric.IncludingShortIdle);
        var rows = projectId is { } scopedProjectId
            ? reportRows.Where(row => row.ProjectId == scopedProjectId)
            : reportRows;
        return useShortIdle
            ? rows.Sum(row => row.DurationWithShortIdleSeconds)
            : rows.Sum(row => row.DurationSeconds);
    }

    private static double? SumTargetHours(
        IEnumerable<CustomTargetRow> rows,
        CustomTargetCadence cadence)
    {
        var values = rows
            .Where(row => row.Target.Cadence == cadence)
            .Select(row => row.Target.TargetHours)
            .ToArray();
        return values.Length == 0 ? null : values.Sum();
    }

    private void UpdateReportTargetsList()
    {
        ReportTargetsList.ItemsSource = _allTargetRows
            .Where(row => row.HasMonthlyTarget)
            .Select(row => row.AsMonthlyOnly())
            .OrderBy(row => row.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Client, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<CustomTargetRow[]> BuildCustomTargetRowsAsync(
        IReadOnlyList<CustomTarget> targets,
        IReadOnlyDictionary<Guid, Project> projectsById,
        IReadOnlyDictionary<Guid, string> clientNames)
    {
        var pendingRows = targets.Select(async target =>
        {
            var period = GetCustomTargetPeriod(target);
            var reportRows = await _store.GetReportAsync(
                period.StartUtc,
                period.EndUtc,
                new ReportFilter(ProjectId: target.ProjectId));
            var completedSeconds = target.DurationMetric == TargetDurationMetric.IncludingShortIdle
                ? reportRows.Sum(row => row.DurationWithShortIdleSeconds)
                : reportRows.Sum(row => row.DurationSeconds);
            if (target.Cadence == CustomTargetCadence.OneTime)
            {
                var completedUtc = OneTimeTargetLifecycle.ResolveCompletionUtc(
                    target,
                    completedSeconds,
                    reportRows.Max(row => row.LatestActivityUtc),
                    _controller.UtcNow);
                if (completedUtc != target.CompletedUtc)
                {
                    await _store.SetCustomTargetCompletionAsync(target.Id, completedUtc);
                    target = target with { CompletedUtc = completedUtc };
                }

                if (!OneTimeTargetLifecycle.IsVisible(target, _controller.UtcNow, TimeZoneInfo.Local))
                {
                    return null;
                }
            }

            var projectName = "All projects";
            var clientName = "Every client";
            if (target.ProjectId is { } projectId)
            {
                var project = projectsById.GetValueOrDefault(projectId);
                projectName = project?.Name ?? "Archived project";
                clientName = project is null
                    ? "Archived client"
                    : clientNames.GetValueOrDefault(project.ClientId, "Archived client");
            }

            return (CustomTargetRow?)new CustomTargetRow(
                target,
                projectName,
                clientName,
                completedSeconds,
                target.ProjectId is { } scopedProjectId
                    ? projectsById.GetValueOrDefault(scopedProjectId)
                    : null);
        });

        return (await Task.WhenAll(pendingRows))
            .Where(row => row is not null)
            .Select(row => row!)
            .OrderBy(row => row.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private TrackingPeriod GetCustomTargetPeriod(CustomTarget target) => target.Cadence switch
    {
        CustomTargetCadence.Daily => TrackingPeriodCalculator.CurrentDay(_controller.UtcNow, TimeZoneInfo.Local),
        CustomTargetCadence.Weekly => TrackingPeriodCalculator.CurrentWeek(_controller.UtcNow, TimeZoneInfo.Local),
        CustomTargetCadence.Monthly => TrackingPeriodCalculator.CurrentMonth(_controller.UtcNow, TimeZoneInfo.Local),
        CustomTargetCadence.OneTime => OneTimeTargetLifecycle.GetProgressPeriod(target, _controller.UtcNow),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    private async void ReportFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = e;
        if (!_loaded || _loading || _updatingTagFilters || _updatingReportFilters)
        {
            return;
        }

        if (ReferenceEquals(sender, ReportClientCombo) || ReferenceEquals(sender, ReportProjectCombo))
        {
            UpdateReportFilterOptions();
        }

        await RefreshReportAsync();
    }

    private async void ReportDateRangeChanged(object sender, DateRangeChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loaded && !_loading && !_updatingReportFilters)
        {
            await RefreshReportAsync();
        }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new SaveFileDialog
        {
            Title = "Export tracked time",
            Filter = "CSV files (*.csv)|*.csv",
            DefaultExt = ".csv",
            FileName = $"project-time-{DateTime.Today:yyyy-MM-dd}.csv",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var (fromUtc, toUtc) = GetRange(ReportRangePicker.StartDate, ReportRangePicker.EndDate);
            IReadOnlyList<TimeEntryView> entries = await _store.GetEntriesAsync(fromUtc, toUtc);
            var filter = GetSelectedReportFilter();
            entries = entries.Where(entry => MatchesReportFilter(entry, filter)).ToArray();

            await CsvExporter.ExportAsync(dialog.FileName, entries, TimeZoneInfo.Local, _controller.UtcNow);
            MessageBox.Show(this, "CSV export completed.", "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError("Could not export the CSV", exception);
        }
    }

    private void Controller_AutomaticRecognitionSettingsChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateAutomaticRecognitionControls);
            return;
        }

        UpdateAutomaticRecognitionControls();
    }

    private void UpdateAutomaticRecognitionControls()
    {
        _updatingAutomaticRecognitionControls = true;
        try
        {
            AutomaticRecognitionToggle.IsChecked = _controller.AutomaticRecognitionEnabled;
            AutomaticRecognitionToggle.ToolTip =
                "Full automatic mode\n" +
                "Starts recognized projects silently. Stops and project switches become final after " +
                $"{_controller.AutomaticRecognitionGraceMinutes} minutes, using the original foreground-change time.";
            AutomaticRecognitionGraceMinutesText.Text =
                _controller.AutomaticRecognitionGraceMinutes.ToString(CultureInfo.CurrentCulture);
            AutomaticRecognitionGraceValidationText.Visibility = Visibility.Collapsed;
            RecognitionCheck.IsChecked = _controller.RecognitionEnabled;
        }
        finally
        {
            _updatingAutomaticRecognitionControls = false;
        }
    }

    private async void AutomaticRecognitionToggle_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loaded || _loading || _updatingAutomaticRecognitionControls)
        {
            return;
        }

        try
        {
            await _controller.SetAutomaticRecognitionEnabledAsync(
                AutomaticRecognitionToggle.IsChecked == true);
        }
        catch (Exception exception)
        {
            UpdateAutomaticRecognitionControls();
            ShowError("Could not update full automatic mode", exception);
        }
    }

    private async void RecognitionCheck_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loaded || _loading || _updatingAutomaticRecognitionControls)
        {
            return;
        }

        await _controller.SetRecognitionEnabledAsync(RecognitionCheck.IsChecked == true);
    }

    private async void AutomaticRecognitionGraceMinutesText_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ApplyAutomaticRecognitionGraceMinutesAsync();
    }

    private async void AutomaticRecognitionGraceMinutesText_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await ApplyAutomaticRecognitionGraceMinutesAsync();
    }

    private async Task ApplyAutomaticRecognitionGraceMinutesAsync()
    {
        if (!_loaded || _loading || _updatingAutomaticRecognitionControls)
        {
            return;
        }

        var value = AutomaticRecognitionGraceMinutesText.Text.Trim();
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.CurrentCulture,
                out var minutes) ||
            !AutomaticRecognitionSettings.IsValidGraceMinutes(minutes))
        {
            AutomaticRecognitionGraceValidationText.Text =
                $"Enter a whole number from {AutomaticRecognitionSettings.MinimumAllowedMinutes} to {AutomaticRecognitionSettings.MaximumAllowedMinutes} minutes.";
            AutomaticRecognitionGraceValidationText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            if (minutes != _controller.AutomaticRecognitionGraceMinutes)
            {
                await _controller.SetAutomaticRecognitionGraceMinutesAsync(minutes);
            }

            UpdateAutomaticRecognitionControls();
        }
        catch (Exception exception)
        {
            UpdateAutomaticRecognitionControls();
            ShowError("Could not update automatic recognition grace period", exception);
        }
    }

    private async void SessionBehaviorCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateSessionBehaviorDescription();
        if (!_loaded || _loading || SessionBehaviorCombo.SelectedIndex < 0)
        {
            return;
        }

        var behavior = SessionBehaviorCombo.SelectedIndex == 0
            ? SessionTrackingBehavior.StopTimer
            : SessionTrackingBehavior.KeepRunningAndExclude;
        await _controller.SetSessionTrackingBehaviorAsync(behavior);
    }

    private void UpdateSessionBehaviorDescription()
    {
        SessionBehaviorDescriptionText.Text = SessionBehaviorCombo.SelectedIndex == 1
            ? "The timer remains active. After unlock, resume, or the next launch following sign-out, the app asks whether to cut the inactive period from work duration."
            : "The running entry is stopped immediately when Windows locks, signs out, or enters sleep. Its details popup opens after return so the entry can be completed or edited.";
    }

    private async void CallsIdleProtectionCheck_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loaded || _loading)
        {
            return;
        }

        try
        {
            await _controller.SetCallsIdleProtectionEnabledAsync(
                CallsIdleProtectionCheck.IsChecked == true);
        }
        catch (Exception exception)
        {
            CallsIdleProtectionCheck.IsChecked = _controller.CallsIdleProtectionEnabled;
            ShowError("Could not update call idle protection", exception);
        }
    }

    private async void VideoIdleProtectionCheck_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loaded || _loading)
        {
            return;
        }

        try
        {
            await _controller.SetVideoIdleProtectionEnabledAsync(
                VideoIdleProtectionCheck.IsChecked == true);
        }
        catch (Exception exception)
        {
            VideoIdleProtectionCheck.IsChecked = _controller.VideoIdleProtectionEnabled;
            ShowError("Could not update video idle protection", exception);
        }
    }

    private void Controller_IdleProtectionChanged(
        object? sender,
        IdleProtectionState state)
    {
        _ = sender;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateIdleProtectionStatus(state));
            return;
        }

        UpdateIdleProtectionStatus(state);
    }

    private void UpdateIdleProtectionStatus(IdleProtectionState state)
    {
        var statuses = new List<string>(3);
        if (state.ActiveReasons.HasFlag(IdleProtectionReason.CommunicationAudio))
        {
            statuses.Add("Call active");
        }

        if (state.ActiveReasons.HasFlag(IdleProtectionReason.ForegroundAudio))
        {
            statuses.Add("Foreground audio active");
        }

        if (state.ActiveReasons.HasFlag(IdleProtectionReason.VideoPlayback))
        {
            statuses.Add("Video playing");
        }

        if (statuses.Count > 0)
        {
            IdleProtectionStatusText.Text = string.Join(" · ", statuses);
            IdleProtectionStatusText.Foreground = (Brush)FindResource("SuccessBrush");
            IdleProtectionStatusDot.Fill = (Brush)FindResource("SuccessBrush");
            return;
        }

        if (!state.IsInitialized)
        {
            IdleProtectionStatusText.Text = "Idle protection is starting…";
        }
        else if ((_controller.CallsIdleProtectionEnabled && !state.CallsAvailable) ||
                 (_controller.VideoIdleProtectionEnabled && !state.VideoAvailable))
        {
            var unavailable = new List<string>(2);
            if (_controller.CallsIdleProtectionEnabled && !state.CallsAvailable)
            {
                unavailable.Add("call detection unavailable");
            }

            if (_controller.VideoIdleProtectionEnabled && !state.VideoAvailable)
            {
                unavailable.Add("video detection unavailable");
            }

            IdleProtectionStatusText.Text = string.Join(" · ", unavailable);
        }
        else
        {
            IdleProtectionStatusText.Text = "No protected activity";
        }

        IdleProtectionStatusText.Foreground = (Brush)FindResource("ContentSecondaryBrush");
        IdleProtectionStatusDot.Fill = (Brush)FindResource("ContentTertiaryBrush");
    }

    private async void ExcludedSoftwareReviewMinutesText_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ApplyExcludedSoftwareReviewMinutesAsync();
    }

    private async void RecentEntryResumeMinutesText_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ApplyRecentEntryResumeMinutesAsync();
    }

    private async void RecentEntryResumeMinutesText_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await ApplyRecentEntryResumeMinutesAsync();
    }

    private async Task ApplyRecentEntryResumeMinutesAsync()
    {
        if (!_loaded || _loading)
        {
            return;
        }

        var value = RecentEntryResumeMinutesText.Text.Trim();
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.CurrentCulture,
                out var minutes) ||
            !RecentEntryResumeSettings.IsValidMaximumGapMinutes(minutes))
        {
            RecentEntryResumeValidationText.Text =
                $"Enter a whole number from {RecentEntryResumeSettings.MinimumAllowedMinutes} to {RecentEntryResumeSettings.MaximumAllowedMinutes} minutes.";
            RecentEntryResumeValidationText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            if (minutes != _controller.RecentEntryResumeMaximumGapMinutes)
            {
                await _controller.SetRecentEntryResumeMaximumGapMinutesAsync(minutes);
            }

            RecentEntryResumeMinutesText.Text =
                _controller.RecentEntryResumeMaximumGapMinutes.ToString(CultureInfo.CurrentCulture);
            RecentEntryResumeValidationText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            ShowError("Could not update recent-entry resume time", exception);
            RecentEntryResumeMinutesText.Text =
                _controller.RecentEntryResumeMaximumGapMinutes.ToString(CultureInfo.CurrentCulture);
        }
    }

    private async void BreakReminderMinutesText_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ApplyBreakReminderMinutesAsync();
    }

    private async void BreakReminderMinutesText_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await ApplyBreakReminderMinutesAsync();
    }

    private async Task ApplyBreakReminderMinutesAsync()
    {
        if (!_loaded || _loading)
        {
            return;
        }

        var value = BreakReminderMinutesText.Text.Trim();
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.CurrentCulture,
                out var minutes) ||
            !BreakReminderSettings.IsValidIntervalMinutes(minutes))
        {
            BreakReminderValidationText.Text =
                $"Enter a whole number from {BreakReminderSettings.MinimumAllowedMinutes} to {BreakReminderSettings.MaximumAllowedMinutes} minutes.";
            BreakReminderValidationText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            if (minutes != _controller.BreakReminderIntervalMinutes)
            {
                await _controller.SetBreakReminderIntervalMinutesAsync(minutes);
            }

            BreakReminderMinutesText.Text =
                _controller.BreakReminderIntervalMinutes.ToString(CultureInfo.CurrentCulture);
            BreakReminderValidationText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            ShowError("Could not update break reminder time", exception);
            BreakReminderMinutesText.Text =
                _controller.BreakReminderIntervalMinutes.ToString(CultureInfo.CurrentCulture);
        }
    }

    private async void BreakReminderPlacement_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loaded || _loading || !TryGetBreakReminderPlacement(out var placement))
        {
            return;
        }

        try
        {
            if (placement != _controller.BreakReminderPlacement)
            {
                await _controller.SetBreakReminderPlacementAsync(placement);
            }
        }
        catch (Exception exception)
        {
            ShowError("Could not update break reminder position", exception);
            SetBreakReminderPlacementControls(_controller.BreakReminderPlacement);
        }
    }

    private void SetBreakReminderPlacementControls(BreakReminderPlacement placement)
    {
        BreakReminderBottomRight.IsChecked = placement == BreakReminderPlacement.BottomRight;
        BreakReminderScreenCenter.IsChecked = placement == BreakReminderPlacement.ScreenCenter;
    }

    private bool TryGetBreakReminderPlacement(out BreakReminderPlacement placement)
    {
        var value = BreakReminderBottomRight.IsChecked == true
            ? BreakReminderBottomRight.Tag as string
            : BreakReminderScreenCenter.IsChecked == true
                ? BreakReminderScreenCenter.Tag as string
                : null;
        return Enum.TryParse(value, ignoreCase: true, out placement) &&
               Enum.IsDefined(placement);
    }

    private async void BreakReminderMessage_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loaded || _loading)
        {
            return;
        }

        var enabledMessageIds = GetBreakReminderMessageCheckBoxes()
            .Where(checkBox => checkBox.IsChecked == true)
            .Select(checkBox => checkBox.Tag as string)
            .OfType<string>()
            .ToArray();
        if (_controller.BreakReminderEnabledMessageIds.SetEquals(enabledMessageIds))
        {
            return;
        }

        try
        {
            await _controller.SetBreakReminderEnabledMessageIdsAsync(enabledMessageIds);
        }
        catch (Exception exception)
        {
            ShowError("Could not update break reminder messages", exception);
            SetBreakReminderMessageControls(_controller.BreakReminderEnabledMessageIds);
        }
    }

    private IEnumerable<CheckBox> GetBreakReminderMessageCheckBoxes()
    {
        yield return BreakReminderBathroomMessageCheck;
        yield return BreakReminderBreakMessageCheck;
        yield return BreakReminderCoffeeMessageCheck;
        yield return BreakReminderTeaMessageCheck;
        yield return BreakReminderSnackMessageCheck;
        yield return BreakReminderStandUpMessageCheck;
        yield return BreakReminderLaundryMessageCheck;
        yield return BreakReminderDinnerMessageCheck;
        yield return BreakReminderEpisodeMessageCheck;
    }

    private void SetBreakReminderMessageControls(IReadOnlySet<string> enabledMessageIds)
    {
        foreach (var checkBox in GetBreakReminderMessageCheckBoxes())
        {
            checkBox.IsChecked = checkBox.Tag is string id && enabledMessageIds.Contains(id);
        }
    }

    private async void ExcludedSoftwareReviewMinutesText_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await ApplyExcludedSoftwareReviewMinutesAsync();
    }

    private async Task ApplyExcludedSoftwareReviewMinutesAsync()
    {
        if (!_loaded || _loading)
        {
            return;
        }

        var value = ExcludedSoftwareReviewMinutesText.Text.Trim();
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.CurrentCulture,
                out var minutes) ||
            !ExcludedSoftwareReviewSettings.IsValidMinimumMinutes(minutes))
        {
            ExcludedSoftwareReviewValidationText.Text =
                $"Enter a whole number from {ExcludedSoftwareReviewSettings.MinimumAllowedMinutes} to {ExcludedSoftwareReviewSettings.MaximumAllowedMinutes} minutes.";
            ExcludedSoftwareReviewValidationText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            if (minutes != _controller.ExcludedSoftwareReviewMinimumMinutes)
            {
                await _controller.SetExcludedSoftwareReviewMinimumMinutesAsync(minutes);
            }

            ExcludedSoftwareReviewMinutesText.Text =
                _controller.ExcludedSoftwareReviewMinimumMinutes.ToString(CultureInfo.CurrentCulture);
            ExcludedSoftwareReviewValidationText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            ShowError("Could not update excluded-software review time", exception);
            ExcludedSoftwareReviewMinutesText.Text =
                _controller.ExcludedSoftwareReviewMinimumMinutes.ToString(CultureInfo.CurrentCulture);
        }
    }

    private async void AccumulatedAwayReviewMinutesText_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ApplyAccumulatedAwayReviewMinutesAsync();
    }

    private async void AccumulatedAwayReviewMinutesText_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await ApplyAccumulatedAwayReviewMinutesAsync();
    }

    private async void TargetReviewNotificationCheck_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        TargetReviewSchedulePanel.IsEnabled = TargetReviewNotificationCheck.IsChecked == true;
        if (!_loaded || _loading)
        {
            return;
        }

        await ApplyTargetReviewScheduleAsync();
    }

    private async void TargetReviewDay_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loaded && !_loading)
        {
            await ApplyTargetReviewScheduleAsync();
        }
    }

    private async void TargetReviewMonthWeek_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_loaded && !_loading)
        {
            await ApplyTargetReviewScheduleAsync();
        }
    }

    private void SetTargetReviewScheduleControls(TargetReviewSchedule schedule)
    {
        TargetReviewNotificationCheck.IsChecked = schedule.Enabled;
        TargetReviewSchedulePanel.IsEnabled = schedule.Enabled;
        foreach (var option in GetTargetReviewDayOptions())
        {
            option.IsChecked = string.Equals(
                option.Tag as string,
                schedule.DayOfWeek.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }

        foreach (var option in GetTargetReviewMonthWeekOptions())
        {
            option.IsChecked = string.Equals(
                option.Tag as string,
                schedule.MonthWeek.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task ApplyTargetReviewScheduleAsync()
    {
        if (!TryGetTargetReviewDay(out var day) || !TryGetTargetReviewMonthWeek(out var monthWeek))
        {
            return;
        }

        try
        {
            await _controller.SetTargetReviewScheduleAsync(
                new TargetReviewSchedule(
                    TargetReviewNotificationCheck.IsChecked == true,
                    day,
                    monthWeek));
        }
        catch (Exception exception)
        {
            ShowError("Could not update target review reminder", exception);
            SetTargetReviewScheduleControls(_controller.TargetReviewSchedule);
        }
    }

    private bool TryGetTargetReviewDay(out DayOfWeek day)
    {
        var tag = GetTargetReviewDayOptions()
            .FirstOrDefault(option => option.IsChecked == true)?.Tag as string;
        return Enum.TryParse(tag, ignoreCase: true, out day) && Enum.IsDefined(day);
    }

    private bool TryGetTargetReviewMonthWeek(out TargetReviewMonthWeek monthWeek)
    {
        var tag = GetTargetReviewMonthWeekOptions()
            .FirstOrDefault(option => option.IsChecked == true)?.Tag as string;
        return Enum.TryParse(tag, ignoreCase: true, out monthWeek) && Enum.IsDefined(monthWeek);
    }

    private IEnumerable<RadioButton> GetTargetReviewDayOptions() =>
    [
        TargetReviewMonday, TargetReviewTuesday, TargetReviewWednesday, TargetReviewThursday,
        TargetReviewFriday, TargetReviewSaturday, TargetReviewSunday,
    ];

    private IEnumerable<RadioButton> GetTargetReviewMonthWeekOptions() =>
    [
        TargetReviewFirstWeek, TargetReviewSecondWeek,
        TargetReviewPenultimateWeek, TargetReviewLastWeek,
    ];

    private async Task ApplyAccumulatedAwayReviewMinutesAsync()
    {
        if (!_loaded || _loading)
        {
            return;
        }

        var value = AccumulatedAwayReviewMinutesText.Text.Trim();
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.CurrentCulture,
                out var minutes) ||
            !AccumulatedAwayReviewSettings.IsValidMinimumMinutes(minutes))
        {
            AccumulatedAwayReviewValidationText.Text =
                $"Enter a whole number from {AccumulatedAwayReviewSettings.MinimumAllowedMinutes} to {AccumulatedAwayReviewSettings.MaximumAllowedMinutes}.";
            AccumulatedAwayReviewValidationText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            if (minutes != _controller.AccumulatedAwayReviewMinimumMinutes)
            {
                await _controller.SetAccumulatedAwayReviewMinimumMinutesAsync(minutes);
            }

            AccumulatedAwayReviewMinutesText.Text =
                _controller.AccumulatedAwayReviewMinimumMinutes.ToString(CultureInfo.CurrentCulture);
            AccumulatedAwayReviewValidationText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            ShowError("Could not update accumulated short-idle review time", exception);
            AccumulatedAwayReviewMinutesText.Text =
                _controller.AccumulatedAwayReviewMinimumMinutes.ToString(CultureInfo.CurrentCulture);
        }
    }

    private async void ShortIdleReportingMinutesText_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ApplyShortIdleReportingMinutesAsync();
    }

    private async void ShortIdleReportingMinutesText_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await ApplyShortIdleReportingMinutesAsync();
        Keyboard.ClearFocus();
    }

    private async Task ApplyShortIdleReportingMinutesAsync()
    {
        if (!_loaded || _loading)
        {
            return;
        }

        var value = ShortIdleReportingMinutesText.Text.Trim();
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.CurrentCulture,
                out var minutes) ||
            !ShortIdleReportingSettings.IsValidMaximumMinutes(minutes))
        {
            ShortIdleReportingValidationText.Text =
                $"Enter a whole number from {ShortIdleReportingSettings.MinimumAllowedMinutes} to {ShortIdleReportingSettings.MaximumAllowedMinutes}.";
            ShortIdleReportingValidationText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            if (minutes != _controller.ShortIdleReportingMaximumMinutes)
            {
                await _controller.SetShortIdleReportingMaximumMinutesAsync(minutes);
                await RefreshReportAsync();
            }

            ShortIdleReportingMinutesText.Text =
                _controller.ShortIdleReportingMaximumMinutes.ToString(CultureInfo.CurrentCulture);
            ShortIdleReportingValidationText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            ShortIdleReportingMinutesText.Text =
                _controller.ShortIdleReportingMaximumMinutes.ToString(CultureInfo.CurrentCulture);
            ShowError("Could not save the short-idle report limit", exception);
        }
    }

    private void AutostartCheck_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_loaded || _loading)
        {
            return;
        }

        try
        {
            _autostart.SetEnabled(AutostartCheck.IsChecked == true);
        }
        catch (Exception exception)
        {
            ShowError("Could not update launch-at-sign-in", exception);
            _loading = true;
            AutostartCheck.IsChecked = _autostart.IsEnabled;
            _loading = false;
        }
    }

    private void UpdateReportFilterOptions()
    {
        var selectedClientId = (ReportClientCombo.SelectedItem as ClientFilterOption)?.ClientId;
        var selectedProjectId = (ReportProjectCombo.SelectedItem as ProjectFilterOption)?.ProjectId;
        var selectedTask = ReportTaskCombo.SelectedItem as TaskFilterOption;
        var selectedPaid = (ReportPaidCombo.SelectedItem as PaidFilterOption)?.Value ?? PaidStatusFilter.All;

        _updatingReportFilters = true;
        try
        {
            var clients = new[] { new ClientFilterOption(null, "All clients") }
                .Concat(_activeClients
                    .OrderBy(client => client.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(client => new ClientFilterOption(client.Id, client.Name)))
                .ToArray();
            ReportClientCombo.ItemsSource = clients;
            ReportClientCombo.SelectedItem = clients.FirstOrDefault(option => option.ClientId == selectedClientId) ?? clients[0];
            selectedClientId = (ReportClientCombo.SelectedItem as ClientFilterOption)?.ClientId;

            var availableProjects = _projectOptions
                .Where(project => selectedClientId is null || project.ClientId == selectedClientId)
                .OrderBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(project => project.ClientName, StringComparer.OrdinalIgnoreCase)
                .Select(project => new ProjectFilterOption(project.ProjectId, project.ClientId, project.DisplayName))
                .ToArray();
            var projects = new[] { new ProjectFilterOption(null, selectedClientId, "All projects") }
                .Concat(availableProjects)
                .ToArray();
            ReportProjectCombo.ItemsSource = projects;
            ReportProjectCombo.SelectedItem = projects.FirstOrDefault(option => option.ProjectId == selectedProjectId) ?? projects[0];
            selectedProjectId = (ReportProjectCombo.SelectedItem as ProjectFilterOption)?.ProjectId;

            var projectClients = _activeProjects.ToDictionary(project => project.Id, project => project.ClientId);
            var projectNames = _activeProjects.ToDictionary(project => project.Id, project => project.Name);
            var availableTasks = _activeTasks
                .Where(task => selectedProjectId is null || task.ProjectId == selectedProjectId)
                .Where(task => selectedClientId is null || projectClients.GetValueOrDefault(task.ProjectId) == selectedClientId)
                .OrderBy(task => task.Name, StringComparer.OrdinalIgnoreCase)
                .Select(task => new TaskFilterOption(
                    task.Id,
                    task.ProjectId,
                    selectedProjectId is null
                        ? $"{task.Name} · {projectNames[task.ProjectId]}" 
                        : task.Name))
                .ToArray();
            var tasks = new[]
                {
                    new TaskFilterOption(null, selectedProjectId, "All tasks"),
                    new TaskFilterOption(null, selectedProjectId, "Unassigned", IsUnassigned: true),
                }
                .Concat(availableTasks)
                .ToArray();
            ReportTaskCombo.ItemsSource = tasks;
            ReportTaskCombo.SelectedItem = tasks.FirstOrDefault(option =>
                option.TaskId == selectedTask?.TaskId && option.IsUnassigned == selectedTask?.IsUnassigned) ?? tasks[0];

            var paidOptions = new[]
            {
                new PaidFilterOption(PaidStatusFilter.All, "All payments"),
                new PaidFilterOption(PaidStatusFilter.Paid, "Paid only"),
                new PaidFilterOption(PaidStatusFilter.Unpaid, "Unpaid only"),
            };
            ReportPaidCombo.ItemsSource = paidOptions;
            ReportPaidCombo.SelectedItem = paidOptions.First(option => option.Value == selectedPaid);
        }
        finally
        {
            _updatingReportFilters = false;
        }
    }

    private ReportFilter GetSelectedReportFilter()
    {
        var task = ReportTaskCombo.SelectedItem as TaskFilterOption;
        return new ReportFilter(
            (ReportClientCombo.SelectedItem as ClientFilterOption)?.ClientId,
            (ReportProjectCombo.SelectedItem as ProjectFilterOption)?.ProjectId,
            task?.TaskId,
            task?.IsUnassigned == true,
            (ReportTagCombo.SelectedItem as TagOption)?.Value,
            (ReportPaidCombo.SelectedItem as PaidFilterOption)?.Value ?? PaidStatusFilter.All);
    }

    private bool MatchesReportFilter(TimeEntryView entry, ReportFilter filter)
    {
        if (filter.ClientId is { } clientId &&
            !_reportProjects.Any(project => project.Id == entry.ProjectId && project.ClientId == clientId))
        {
            return false;
        }

        if (filter.ProjectId is { } projectId && entry.ProjectId != projectId)
        {
            return false;
        }

        if (filter.UnassignedTaskOnly && entry.TaskId is not null)
        {
            return false;
        }

        if (filter.TaskId is { } taskId && entry.TaskId != taskId)
        {
            return false;
        }

        if (filter.Tag is { } tag && !TagParser.Contains(entry.Description, tag))
        {
            return false;
        }

        return filter.PaidStatus switch
        {
            PaidStatusFilter.Paid => entry.IsPaid,
            PaidStatusFilter.Unpaid => !entry.IsPaid,
            _ => true,
        };
    }

    private void UpdateTagOptions(ComboBox comboBox, IEnumerable<string> tags)
    {
        var selected = (comboBox.SelectedItem as TagOption)?.Value;
        var colors = _tagDefinitions.ToDictionary(tag => tag.Name, tag => tag.Color, StringComparer.OrdinalIgnoreCase);
        var activeTags = colors.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = new[] { new TagOption(null) }
            .Concat(tags
                .Select(TagParser.Normalize)
                .OfType<string>()
                .Where(activeTags.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .Select(tag => new TagOption(tag, colors.GetValueOrDefault(tag, "#989BA3"))))
            .ToArray();

        _updatingTagFilters = true;
        try
        {
            comboBox.ItemsSource = options;
            comboBox.SelectedItem = options.FirstOrDefault(option =>
                string.Equals(option.Value, selected, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        }
        finally
        {
            _updatingTagFilters = false;
        }
    }

    private static string FormatDuration(TimeSpan duration) =>
        $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    private static string FormatValueTotals(IEnumerable<ReportRow> rows, bool unpaidOnly)
    {
        var values = rows
            .Where(row => row.HourlyRate is not null && (unpaidOnly ? row.UnpaidDurationSeconds : row.DurationSeconds) > 0)
            .GroupBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Currency = group.Key,
                Amount = group.Sum(row =>
                    row.HourlyRate!.Value * (unpaidOnly ? row.UnpaidDurationSeconds : row.DurationSeconds) / 3600m),
            })
            .OrderBy(value => value.Currency, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length == 0
            ? "—"
            : string.Join(" · ", values.Select(value => $"{value.Amount:N2} {value.Currency}"));
    }

    private static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) GetRange(DateTime? from, DateTime? to)
    {
        var fromLocal = DateTime.SpecifyKind((from ?? DateTime.Today).Date, DateTimeKind.Local);
        var toLocalExclusive = DateTime.SpecifyKind((to ?? from ?? DateTime.Today).Date.AddDays(1), DateTimeKind.Local);
        if (toLocalExclusive <= fromLocal)
        {
            toLocalExclusive = fromLocal.AddDays(1);
        }

        return (new DateTimeOffset(fromLocal).ToUniversalTime(), new DateTimeOffset(toLocalExclusive).ToUniversalTime());
    }

    private ProjectRow? SelectProjectRow(Guid projectId)
    {
        var row = (ProjectsGrid.ItemsSource as IEnumerable<ProjectRow>)?
            .FirstOrDefault(item => item.Project.Id == projectId);
        if (row is not null)
        {
            ProjectsGrid.SelectedItem = row;
            return row;
        }

        row = (FrozenProjectsList.ItemsSource as IEnumerable<ProjectRow>)?
            .FirstOrDefault(item => item.Project.Id == projectId);
        if (row is not null)
        {
            FreezedProjectsExpander.IsExpanded = true;
            FrozenProjectsList.SelectedItem = row;
        }

        return row;
    }

    private TaskRow? SelectTaskRow(Guid taskId)
    {
        var row = (TasksGrid.ItemsSource as IEnumerable<TaskRow>)?
            .FirstOrDefault(item => item.Task.Id == taskId);
        if (row is not null)
        {
            TasksGrid.SelectedItem = row;
        }

        return row;
    }

    private static T? GetDataGridRowItem<T>(DataGrid grid, MouseButtonEventArgs e) where T : class
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source)
        {
            return null;
        }

        var current = source;
        while (current is not null && current is not DataGridRow)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        return current is DataGridRow row &&
               ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), grid)
            ? row.Item as T
            : null;
    }

    private static T[] GetSelectedRows<T>(DataGrid grid) where T : class =>
        grid.SelectedItems
            .OfType<T>()
            .ToArray();

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject source)
        where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(source);
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindDataContext<T>(DependencyObject source) where T : class
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: T match })
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void SelectRowOnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ReferenceEquals(grid, HistoryGrid))
        {
            _historyContextAddDate = FindHistoryContextDate(source);
        }

        var current = source;
        while (current is not null && current is not DataGridRow)
        {
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        if (current is DataGridRow row)
        {
            if (!ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), grid))
            {
                return;
            }

            if (!row.IsSelected)
            {
                grid.SelectedItem = row.Item;
            }

            row.Focus();
        }
        else
        {
            grid.UnselectAll();
        }
    }

    private void ProjectsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid { ContextMenu: { } menu } grid)
        {
            return;
        }

        var selectedCount = GetSelectedRows<ProjectRow>(grid).Length;
        ConfigureRowOrEmptyContextMenu(menu, selectedCount > 0, "ProjectOnly");
        var selectedProject = selectedCount == 1 ? grid.SelectedItem as ProjectRow : null;
        ConfigureProjectFreezeContextMenu(menu, selectedProject);
        SetContextMenuActionVisibility(menu, "Rename…", selectedCount == 1);
    }

    internal void VerifyProjectFreezeContextMenuForPreview()
    {
        var menu = ProjectsGrid.ContextMenu
            ?? throw new InvalidOperationException("Projects does not have a context menu.");
        var freezeAction = menu.Items.OfType<MenuItem>()
            .SingleOrDefault(item => string.Equals(item.Header as string, "Freeze project", StringComparison.Ordinal));
        var unfreezeAction = menu.Items.OfType<MenuItem>()
            .SingleOrDefault(item => string.Equals(item.Header as string, "Unfreeze project", StringComparison.Ordinal));
        if (freezeAction?.Tag as string != "ProjectOnly" ||
            unfreezeAction?.Tag as string != "ProjectOnly")
        {
            throw new InvalidOperationException("Projects is missing its freeze context-menu actions.");
        }

        if (!string.Equals(FreezedProjectsExpander.Header as string, "Freezed Projects", StringComparison.Ordinal) ||
            FrozenProjectsList.Opacity >= 1d ||
            !string.Equals(FreezedTagsExpander.Header as string, "Freezed Projects", StringComparison.Ordinal) ||
            !string.Equals(FreezedTasksExpander.Header as string, "Freezed Projects", StringComparison.Ordinal) ||
            !string.Equals(FreezedTargetsExpander.Header as string, "Freezed Projects", StringComparison.Ordinal) ||
            !string.Equals(FreezedSoftwareExpander.Header as string, "Freezed Projects", StringComparison.Ordinal) ||
            !string.Equals(FreezedRulesExpander.Header as string, "Freezed Projects", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Frozen project sections must be folded and visually muted.");
        }

        var activeRow = new ProjectRow(
            new Project(Guid.NewGuid(), Guid.NewGuid(), "Preview project", "#445566"),
            "Preview client",
            null);
        ConfigureProjectFreezeContextMenu(menu, activeRow);
        if (freezeAction.Visibility != Visibility.Visible ||
            unfreezeAction.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("An active project does not expose only the Freeze project action.");
        }

        ConfigureProjectFreezeContextMenu(
            menu,
            activeRow with { Project = activeRow.Project with { IsFrozen = true } });
        if (freezeAction.Visibility != Visibility.Collapsed ||
            unfreezeAction.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException("A frozen project does not expose only the Unfreeze project action.");
        }
    }

    private static void ConfigureProjectFreezeContextMenu(ContextMenu menu, ProjectRow? selectedProject)
    {
        SetContextMenuActionVisibility(menu, "Freeze project", selectedProject is { Project.IsFrozen: false });
        SetContextMenuActionVisibility(menu, "Unfreeze project", selectedProject is { Project.IsFrozen: true });
    }

    private void CustomTargetsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _ = e;
        if (sender is DataGrid { ContextMenu: { } menu } grid)
        {
            ConfigureRowOrEmptyContextMenu(menu, grid.SelectedItem is ITargetManagementRow, "CustomTargetOnly");
            SetContextMenuActionVisibility(
                menu,
                "Lower debt...",
                grid.SelectedItem is CustomTargetRow { CanCancelDebt: true });
            SetContextMenuActionVisibility(
                menu,
                "Cancel debt",
                grid.SelectedItem is CustomTargetRow { CanCancelDebt: true });
        }
    }

    private void HistoryGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is DataGrid { ContextMenu: { } menu } grid)
        {
            if (e.CursorLeft < 0 && e.CursorTop < 0)
            {
                _historyContextAddDate = null;
            }

            ConfigureRowOrEmptyContextMenu(menu, grid.SelectedItems.Count > 0, "EntryOnly");
            SetContextMenuActionVisibility(
                menu,
                "Continue",
                grid.SelectedItems.OfType<TimeEntryRow>().Take(2).Count() == 1);
        }
    }

    private static DateTime? FindHistoryContextDate(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: TimeEntryRow row })
            {
                return row.Entry.StartUtc.ToLocalTime().Date;
            }

            if (current is FrameworkElement { DataContext: CollectionViewGroup group })
            {
                return GetHistoryGroupDate(group);
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static DateTime? GetHistoryGroupDate(CollectionViewGroup group)
    {
        foreach (var item in group.Items)
        {
            if (item is TimeEntryRow row)
            {
                return row.Entry.StartUtc.ToLocalTime().Date;
            }

            if (item is CollectionViewGroup nestedGroup &&
                GetHistoryGroupDate(nestedGroup) is { } nestedDate)
            {
                return nestedDate;
            }
        }

        return null;
    }

    private void ClientsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _ = e;
        if (sender is ListBox { ContextMenu: { } menu } list)
        {
            ConfigureRowOrEmptyContextMenu(menu, list.SelectedItem is ClientRow, "ClientOnly");
        }
    }

    private void TasksGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _ = e;
        if (sender is DataGrid { ContextMenu: { } menu } grid)
        {
            var selectedCount = GetSelectedRows<TaskRow>(grid).Length;
            var selectedRows = GetSelectedRows<TaskRow>(grid);
            var singleTrello = selectedRows is [var row] && row.IsTrelloLinked;
            var containsTrello = selectedRows.Any(row => row.IsTrelloLinked);
            ConfigureRowOrEmptyContextMenu(menu, selectedCount > 0, "TaskOnly");
            SetContextMenuActionVisibility(menu, "Rename…", selectedCount == 1 && !singleTrello);
            SetContextMenuActionVisibility(menu, "Edit…", selectedCount > 0 && !containsTrello);
            SetContextMenuActionVisibility(menu, "Open in Trello", singleTrello);
        }
    }

    private void TagsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid { ContextMenu: { } menu } grid)
        {
            e.Handled = true;
            return;
        }

        var selectedCount = GetSelectedRows<TagRow>(grid).Length;
        ConfigureRowOrEmptyContextMenu(menu, selectedCount > 0, "TagOnly");
        SetContextMenuActionVisibility(
            menu,
            "Rename everywhere…",
            selectedCount == 1);
    }

    private void SoftwareGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid { ContextMenu: { } menu } grid)
        {
            e.Handled = true;
            return;
        }

        ConfigureRowOrEmptyContextMenu(
            menu,
            grid.SelectedItem is SoftwareRow,
            "SoftwareOnly");
    }

    private void RequireReportProjectSelection_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is ListBox { SelectedItem: null })
        {
            e.Handled = true;
        }
    }

    private void ReportTaskGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid { ContextMenu: { } menu } grid ||
            grid.SelectedItem is not ReportTaskSummaryRow task)
        {
            e.Handled = true;
            return;
        }

        foreach (var item in menu.Items.OfType<FrameworkElement>())
        {
            if (string.Equals(item.Tag as string, "SavedTaskOnly", StringComparison.Ordinal))
            {
                item.Visibility = task.TaskId is null ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private static void ConfigureRowOrEmptyContextMenu(ContextMenu menu, bool hasRow, string rowTag)
    {
        foreach (var item in menu.Items.OfType<FrameworkElement>())
        {
            item.Visibility = item.Tag switch
            {
                "EmptyOnly" => hasRow ? Visibility.Collapsed : Visibility.Visible,
                string tag when string.Equals(tag, rowTag, StringComparison.Ordinal) =>
                    hasRow ? Visibility.Visible : Visibility.Collapsed,
                _ => item.Visibility,
            };
        }
    }

    private static void SetContextMenuActionVisibility(ContextMenu menu, string header, bool visible)
    {
        var item = menu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Header as string, header, StringComparison.Ordinal));
        if (item is not null)
        {
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void RulesGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGrid { ContextMenu: { } menu } grid)
        {
            return;
        }

        ConfigureRowOrEmptyContextMenu(
            menu,
            GetSelectedRows<RuleRow>(grid).Length > 0,
            "RuleOnly");
    }

    private bool ConfirmRemove(string item) =>
        MessageBox.Show(this, $"Remove the {item} from active lists? Historical time entries will be preserved.", "Remove", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private bool ConfirmBulkRemove(int count, string itemType) =>
        MessageBox.Show(
            this,
            $"Remove {count} selected {itemType} from active lists? Historical time entries will be preserved.",
            "Remove selected",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

    private bool ConfirmPermanentProjectRemoval(string item, string details) =>
        MessageBox.Show(
            this,
            $"Permanently delete the {item}?\n\n{details}\n\nThis cannot be undone.",
            "Delete permanently",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private bool ConfirmPermanentBulkProjectRemoval(int count) =>
        MessageBox.Show(
            this,
            $"Permanently delete {count} selected projects?\n\n" +
            "Every related time entry, task, target, rule, and project setting will be deleted. " +
            "This cannot be undone.",
            "Delete projects permanently",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void ShowError(string title, Exception exception) =>
        MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
