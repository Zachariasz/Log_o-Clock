using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectTimeTracker.Core;
using ProjectTimeTracker.Infrastructure;
using ProjectTimeTracker.Windows.Controls;
using ProjectTimeTracker.Windows.Services;
using ProjectTimeTracker.Windows.ViewModels;
using ProjectTimeTracker.Windows.Views;
using MessageBox = ProjectTimeTracker.Windows.Views.ThemedMessageBox;

namespace ProjectTimeTracker.Windows;

public partial class App : System.Windows.Application
{
    private SqliteTrackerStore? _store;
    private AppController? _controller;
    private MainWindow? _mainWindow;
    private TrayIconService? _tray;
    private SingleInstanceCoordinator? _singleInstance;
    private ProfileCatalog? _profileCatalog;
    private TrackerProfile? _activeProfile;
    private TrelloApiClient? _trelloApiClient;
    private WindowsCredentialStore? _credentialStore;
    private TrelloSyncService? _trelloSync;
    private GoogleSheetsApiClient? _googleSheetsApiClient;
    private GoogleSheetsSyncService? _googleSheetsSync;
    private EntryDetailsWindow? _detailsWindow;
    private Guid? _runningEntryId;
    private string _runningLabel = "Tracking";
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        EnglishUiCulture.Apply();
        WindowBackdropService.Register();
        base.OnStartup(e);
        var smokeTest = e.Args.Any(argument => string.Equals(argument, "--smoke-test", StringComparison.OrdinalIgnoreCase));
        if (!smokeTest)
        {
            _singleInstance = SingleInstanceCoordinator.Create(Dispatcher, OpenMainWindow);
            if (!_singleInstance.IsFirstInstance)
            {
                SingleInstanceCoordinator.SignalExisting();
                _singleInstance.Dispose();
                _singleInstance = null;
                Shutdown(0);
                return;
            }
        }

        try
        {
            WindowsSqliteRuntime.Initialize();
            var rootDataDirectory = Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_DATA_DIR");
            string? legacyDatabasePath = null;
            if (string.IsNullOrWhiteSpace(rootDataDirectory))
            {
                rootDataDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "TimeTracker");
                legacyDatabasePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ProjectTimeTracker",
                    "tracker.db");
            }

            _profileCatalog = ProfileCatalog.Load(rootDataDirectory);
            if (GetRequestedProfileId(e.Args) is { } requestedProfileId)
            {
                _profileCatalog.SetActive(requestedProfileId);
            }

            var verifyProfiles = smokeTest && string.Equals(
                Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_PROFILES"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            if (verifyProfiles &&
                _profileCatalog.Profiles.All(profile =>
                    !string.Equals(profile.Name, "Second profile", StringComparison.OrdinalIgnoreCase)))
            {
                _profileCatalog.Add("Second profile");
            }

            _activeProfile = _profileCatalog.ActiveProfile;
            var dataDirectory = _profileCatalog.GetDataDirectory(_activeProfile.Id);
            var databasePath = Path.Combine(dataDirectory, "TimeTracker.db");
            if (legacyDatabasePath is not null && _activeProfile.UsesRootDirectory)
            {
                await SqliteDatabaseMigrator.CopyIfTargetMissingAsync(legacyDatabasePath, databasePath);
            }

            _store = new SqliteTrackerStore(databasePath, dataDirectory);
            await _store.InitializeAsync();
            var verifySessionRecovery = smokeTest && string.Equals(
                Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_SESSION_RECOVERY"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            Guid? sessionRecoveryStoppedEntryId = null;
            Guid? sessionRecoveryRunningEntryId = null;
            if (verifySessionRecovery)
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var client = await _store.AddClientAsync(
                    $"Session recovery client {Guid.NewGuid():N}",
                    "#766F80");
                var project = await _store.AddProjectAsync(
                    client.Id,
                    $"Session recovery project {Guid.NewGuid():N}",
                    "#0D8F68");
                var task = await _store.AddTaskAsync(project.Id, "Recovered session task");

                var stoppedEntry = await _store.StartTimerAsync(
                    project.Id,
                    TrackingSource.Manual,
                    nowUtc.AddMinutes(-90));
                await _store.UpdateEntryDetailsAsync(
                    stoppedEntry.Id,
                    task.Id,
                    "Review after sign-in",
                    nowUtc.AddMinutes(-85));
                var stoppedResult = await _store.StopRunningTimerAsync(nowUtc.AddMinutes(-80));
                sessionRecoveryStoppedEntryId = stoppedResult?.Id
                    ?? throw new InvalidOperationException("The stopped recovery entry was not seeded.");
                await _store.SetSettingAsync(
                    SessionTrackingSettings.ReviewEntryKey,
                    sessionRecoveryStoppedEntryId.Value.ToString("D"));

                var runningEntry = await _store.StartTimerAsync(
                    project.Id,
                    TrackingSource.Manual,
                    nowUtc.AddMinutes(-60));
                await _store.UpdateEntryDetailsAsync(
                    runningEntry.Id,
                    task.Id,
                    "Continue after sign-in",
                    nowUtc.AddMinutes(-55));
                var unavailableSinceUtc = nowUtc.AddMinutes(-30);
                await _store.CheckpointRunningTimerAsync(unavailableSinceUtc);
                await _store.SetSettingAsync(
                    SessionTrackingSettings.BehaviorKey,
                    SessionTrackingBehavior.KeepRunningAndExclude.ToString());
                await _store.SetSettingAsync(
                    SessionTrackingSettings.ResumeMarkerKey,
                    SessionTrackingSettings.FormatResumeMarker(runningEntry.Id, unavailableSinceUtc));
                sessionRecoveryRunningEntryId = runningEntry.Id;
            }

            await _store.RecoverInterruptedTimerAsync(DateTimeOffset.UtcNow);

            var clock = new SystemClock();
            var verifyNoStartupPopup = smokeTest && string.Equals(
                Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_NO_STARTUP_POPUP"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            var verifyRecognitionSwitch = smokeTest && string.Equals(
                Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_RECOGNITION_SWITCH"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            var verifySessionBehavior = smokeTest && string.Equals(
                Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_SESSION_BEHAVIOR"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            Guid? recognitionSwitchCurrentEntryId = null;
            Guid? recognitionSwitchCurrentProjectId = null;
            Guid? recognitionSwitchTargetProjectId = null;
            Guid? recognitionSwitchTargetTaskId = null;
            var startupSmokeActivity = verifyNoStartupPopup
                ? new WindowActivity(
                    42,
                    "Startup matching work window",
                    "startup-work-app",
                    DateTimeOffset.UtcNow)
                : null;
            var startupSmokeMonitor = startupSmokeActivity is null
                ? null
                : new FixedForegroundActivityMonitor(startupSmokeActivity);
            IForegroundActivityMonitor foreground = startupSmokeMonitor is not null
                ? startupSmokeMonitor
                : new ForegroundActivityMonitor();

            if (smokeTest && string.Equals(
                    Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_SEED_MONTHLY_LOG"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                var client = (await _store.GetClientsAsync()).FirstOrDefault()
                    ?? await _store.AddClientAsync("Smoke test client", "#766F80");
                var project = (await _store.GetProjectsAsync()).FirstOrDefault()
                    ?? await _store.AddProjectAsync(client.Id, "Monthly storage", "#0D8F68");
                var task = (await _store.GetTasksAsync(project.Id)).FirstOrDefault()
                    ?? await _store.AddTaskAsync(project.Id, "Storage verification");
                await _store.UpdateProjectSettingsAsync(project.Id, 1, 10, 40, null, "PLN");
                var end = DateTimeOffset.UtcNow;
                await _store.AddManualEntryAsync(
                    project.Id,
                    task.Id,
                    "Packaged monthly log #storage",
                    end.AddHours(-1),
                    end);
            }

            if (verifyNoStartupPopup)
            {
                var client = await _store.AddClientAsync(
                    $"Startup popup client {Guid.NewGuid():N}",
                    "#766F80");
                var project = await _store.AddProjectAsync(
                    client.Id,
                    $"Startup popup project {Guid.NewGuid():N}",
                    "#0D8F68");
                await _store.AddRuleAsync(
                    project.Id,
                    startupSmokeActivity!.Title,
                    startupSmokeActivity.ProcessName);
                var end = DateTimeOffset.UtcNow.AddMinutes(-1);
                await _store.AddManualEntryAsync(
                    project.Id,
                    taskId: null,
                    description: null,
                    end.AddMinutes(-5),
                    end);
            }

            if (verifyRecognitionSwitch)
            {
                var client = await _store.AddClientAsync(
                    $"Recognition switch client {Guid.NewGuid():N}",
                    "#766F80");
                var currentProject = await _store.AddProjectAsync(
                    client.Id,
                    $"Current timer project {Guid.NewGuid():N}",
                    "#7B8495");
                var targetProject = await _store.AddProjectAsync(
                    client.Id,
                    $"Recognized target project {Guid.NewGuid():N}",
                    "#0D8F68");
                var currentTask = await _store.AddTaskAsync(currentProject.Id, "Current task");
                var targetTask = await _store.AddTaskAsync(targetProject.Id, "Recognized task");
                await _store.AddRuleAsync(
                    currentProject.Id,
                    "Current project smoke window",
                    "current-smoke-app");
                await _store.AddRuleAsync(
                    targetProject.Id,
                    "Target project smoke window",
                    "target-smoke-app");
                var currentEntry = await _store.StartTimerAsync(
                    currentProject.Id,
                    TrackingSource.Manual,
                    DateTimeOffset.UtcNow.AddMinutes(-2));
                await _store.UpdateEntryDetailsAsync(
                    currentEntry.Id,
                    currentTask.Id,
                    "Details that must stay on the old entry",
                    DateTimeOffset.UtcNow.AddMinutes(-1));
                recognitionSwitchCurrentEntryId = currentEntry.Id;
                recognitionSwitchCurrentProjectId = currentProject.Id;
                recognitionSwitchTargetProjectId = targetProject.Id;
                recognitionSwitchTargetTaskId = targetTask.Id;
            }

            // Observe short inactive intervals so they can be combined into the
            // configurable accumulated short-idle review. The review itself still
            // defaults to five minutes, so this does not produce a prompt for
            // every brief pause.
            var idleProtection = new IdleProtectionMonitor(foreground);
            var idle = new UserIdleMonitor(TimeSpan.FromSeconds(30), idleProtection);
            var sessions = new SystemSessionMonitor();
            var notifications = new NotificationService(Dispatcher);
            var autostart = new AutostartService();
            if (autostart.IsEnabled)
            {
                // Migrate an existing ProjectTimeTracker startup entry to the renamed executable.
                autostart.SetEnabled(true);
            }

            _controller = new AppController(
                _store,
                clock,
                foreground,
                idle,
                idleProtection,
                sessions,
                notifications,
                Dispatcher);
            await _controller.InitializeAsync();
            _credentialStore = new WindowsCredentialStore();
            _trelloApiClient = new TrelloApiClient();
            _trelloSync = new TrelloSyncService(
                _store,
                _trelloApiClient,
                _credentialStore,
                _activeProfile.Id,
                clock);
            _googleSheetsApiClient = new GoogleSheetsApiClient();
            _googleSheetsSync = new GoogleSheetsSyncService(
                _store,
                _googleSheetsApiClient,
                new GoogleAuthorizationBroker(),
                _credentialStore,
                _activeProfile.Id,
                _activeProfile.Name,
                clock);
            _runningEntryId = _controller.RunningEntry?.Id;
            _controller.DetailsRequested += Controller_DetailsRequested;
            _controller.RunningEntryChanged += Controller_RunningEntryChanged;
            _controller.TimerTick += Controller_TimerTick;
            _controller.DataChanged += Controller_DataChanged;

            _mainWindow = new MainWindow(
                _store,
                _controller,
                autostart,
                _trelloSync,
                _googleSheetsSync,
                _profileCatalog,
                _activeProfile,
                RequestProfileSwitchAsync);
            _trelloSync.Start();
            _googleSheetsSync.Start();
            MainWindow = _mainWindow;
            _tray = new TrayIconService(
                HandleTraySingleClick,
                OpenMainWindow,
                StartUnassignedFromTray,
                StartFromTray,
                StopFromTray,
                RequestExit);
            await RefreshRunningLabelAsync();
            await RefreshTrayProjectsAsync();
            UpdateTray();
            if (!verifySessionRecovery)
            {
                await _controller.ShowPendingSessionNotificationAsync();
            }

            if (smokeTest)
            {
                if (double.TryParse(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_WIDTH"),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var smokeWidth))
                {
                    _mainWindow.Width = Math.Max(_mainWindow.MinWidth, smokeWidth);
                }

                if (double.TryParse(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_HEIGHT"),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var smokeHeight))
                {
                    _mainWindow.Height = Math.Max(_mainWindow.MinHeight, smokeHeight);
                }

                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                for (var attempt = 0; attempt < 50 && !_mainWindow.IsReadyForPreview; attempt++)
                {
                    await Task.Delay(100);
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_ENGLISH_UI"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _mainWindow.VerifyEnglishInterfaceCultureForPreview();
                }

                if (verifyProfiles)
                {
                    _mainWindow.VerifyProfilesForPreview();
                }

                if (verifySessionRecovery)
                {
                    var stoppedEntryId = sessionRecoveryStoppedEntryId
                        ?? throw new InvalidOperationException("The stopped recovery entry ID was not retained.");
                    var runningEntryId = sessionRecoveryRunningEntryId
                        ?? throw new InvalidOperationException("The running recovery entry ID was not retained.");

                    await _controller.ShowPendingSessionNotificationAsync();
                    if (_detailsWindow?.EntryId != stoppedEntryId ||
                        !_detailsWindow.IsVisible ||
                        !string.Equals(
                            _detailsWindow.HeadingForPreview,
                            AppController.SessionReturnPromptTitle,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Startup session recovery did not open the exact stopped entry with its returning-user heading.");
                    }

                    _detailsWindow.CloseWithoutSaving();
                    await _controller.ReviewRecoveredSessionForPreviewAsync(remove: false);
                    if (_controller.RunningEntry?.Id != runningEntryId ||
                        await _store.GetEntryExcludedSecondsAsync(runningEntryId) != 0 ||
                        !string.IsNullOrWhiteSpace(await _store.GetSettingAsync(
                            SessionTrackingSettings.ResumeMarkerKey)))
                    {
                        throw new InvalidOperationException(
                            "Keeping the recovered inactive interval did not preserve the running entry correctly.");
                    }

                    await _controller.StopTimerAsync();
                    _detailsWindow?.CloseWithoutSaving();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_NO_STARTUP_POPUP"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(1_000);
                    if (Windows.OfType<Window>().Any(window =>
                            window.IsVisible &&
                            window is ReminderWindow or EntryDetailsWindow or ProjectChooserWindow) ||
                        (await _store.GetPendingEntriesAsync()).Count == 0)
                    {
                        throw new InvalidOperationException(
                            "A startup recognition or pending-details popup appeared automatically.");
                    }

                    startupSmokeMonitor!.RaiseActivity(
                        startupSmokeActivity! with { ObservedUtc = DateTimeOffset.UtcNow });
                    for (var attempt = 0;
                         attempt < 30 && !Windows.OfType<ReminderWindow>().Any(window => window.IsVisible);
                         attempt++)
                    {
                        await Task.Delay(100);
                    }

                    var reminder = Windows
                        .OfType<ReminderWindow>()
                        .FirstOrDefault(window => window.IsVisible)
                        ?? throw new InvalidOperationException(
                            "Recognition did not resume after the first real foreground-window change.");
                    reminder.Close();
                    await Task.Delay(100);
                }

                if (verifyRecognitionSwitch)
                {
                    var currentProjectId = recognitionSwitchCurrentProjectId
                        ?? throw new InvalidOperationException("The current recognition project was not seeded.");
                    var targetProjectId = recognitionSwitchTargetProjectId
                        ?? throw new InvalidOperationException("The target recognition project was not seeded.");
                    var targetTaskId = recognitionSwitchTargetTaskId
                        ?? throw new InvalidOperationException("The target recognition task was not seeded.");
                    var currentEntryId = recognitionSwitchCurrentEntryId
                        ?? throw new InvalidOperationException("The current recognition entry was not seeded.");

                    _controller.ObserveActivityForPreview(new WindowActivity(
                        101,
                        "Current project smoke window",
                        "current-smoke-app",
                        DateTimeOffset.UtcNow));
                    await Task.Delay(750);
                    if (Windows.OfType<ReminderWindow>().Any(window => window.IsVisible))
                    {
                        throw new InvalidOperationException(
                            "Recognition prompted while the recognized window still belonged to the running project.");
                    }

                    _controller.ObserveActivityForPreview(new WindowActivity(
                        102,
                        "Target project smoke window - Recognized task",
                        "target-smoke-app",
                        DateTimeOffset.UtcNow));
                    for (var attempt = 0;
                         attempt < 30 && !Windows.OfType<ReminderWindow>().Any(window => window.IsVisible);
                         attempt++)
                    {
                        await Task.Delay(100);
                    }

                    var switchReminder = Windows
                        .OfType<ReminderWindow>()
                        .FirstOrDefault(window => window.IsVisible)
                        ?? throw new InvalidOperationException(
                            "A different recognized project did not open a switch reminder.");
                    if (!switchReminder.IsProjectSwitch)
                    {
                        throw new InvalidOperationException(
                            "The recognition popup did not present the project-switch action.");
                    }

                    if (switchReminder.TargetWindowHandleForPreview != (nint)102)
                    {
                        throw new InvalidOperationException(
                            "The recognition popup was not targeted to the foreground software window's monitor.");
                    }

                    if (switchReminder.SelectedTaskId != targetTaskId)
                    {
                        throw new InvalidOperationException(
                            "The recognition popup did not prefill the saved task matched in the active window title.");
                    }

                    switchReminder.SetDetailsForPreview(
                        targetTaskId,
                        taskName: null,
                        "Details for the recognized project");
                    switchReminder.StartForPreview();
                    for (var attempt = 0;
                         attempt < 30 && _controller.RunningEntry?.ProjectId != targetProjectId;
                         attempt++)
                    {
                        await Task.Delay(100);
                    }

                    var newEntry = _controller.RunningEntry
                        ?? throw new InvalidOperationException(
                            "Accepting the project-switch reminder did not leave a running timer.");
                    var oldEntry = (await _store.GetEntriesAsync(
                            newEntry.StartUtc.AddMinutes(-5),
                            newEntry.StartUtc.AddMinutes(1)))
                        .Single(entry => entry.Id == currentEntryId);
                    if (newEntry.ProjectId != targetProjectId ||
                        newEntry.TaskId != targetTaskId ||
                        newEntry.Source != TrackingSource.WindowReminder ||
                        !string.Equals(
                            newEntry.Description,
                            "Details for the recognized project",
                            StringComparison.Ordinal) ||
                        oldEntry.EndUtc != newEntry.StartUtc ||
                        oldEntry.ProjectId != currentProjectId ||
                        !string.Equals(
                            oldEntry.Description,
                            "Details that must stay on the old entry",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The accepted switch did not stop and start entries at one boundary with isolated details.");
                    }

                    if (_detailsWindow?.IsVisible == true)
                    {
                        throw new InvalidOperationException(
                            "Accepting a recognition switch opened a redundant task-details popup.");
                    }

                    _detailsWindow?.CloseWithoutSaving();
                    await _controller.StopTimerAsync();
                    _detailsWindow?.CloseWithoutSaving();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_TIMER_TASK_SEARCH"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync(
                        $"Timer search client {Guid.NewGuid():N}",
                        "#766F80");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"Timer search project {Guid.NewGuid():N}",
                        "#0D8F68");
                    var prefixTask = await _store.AddTaskAsync(project.Id, "Animation polish");
                    var containsTask = await _store.AddTaskAsync(project.Id, "Character animation");
                    _ = await _store.AddTaskAsync(project.Id, "Rigging setup");
                    await _mainWindow.RefreshAllAsync();

                    var projectCombo = (ComboBox)_mainWindow.FindName("TimerProjectCombo");
                    var taskCombo = (ComboBox)_mainWindow.FindName("TimerTaskCombo");
                    projectCombo.SelectedValue = project.Id;
                    for (var attempt = 0; attempt < 50 && taskCombo.Items.Count != 3; attempt++)
                    {
                        await Task.Delay(100);
                    }

                    taskCombo.ApplyTemplate();
                    var taskEditor = taskCombo.Template.FindName(
                            "PART_EditableTextBox",
                            taskCombo) as TextBox
                        ?? throw new InvalidOperationException(
                            "The tracker task search is missing its editable textbox.");
                    _mainWindow.Activate();
                    Keyboard.Focus(taskEditor);
                    taskEditor.Text = "anim";
                    for (var attempt = 0;
                         attempt < 30 && (!taskCombo.IsDropDownOpen || taskCombo.Items.Count != 2);
                         attempt++)
                    {
                        await Task.Delay(100);
                    }

                    var matches = taskCombo.Items.OfType<SavedTask>().ToArray();
                    if (!taskCombo.IsDropDownOpen ||
                        !string.Equals(taskCombo.Text, "anim", StringComparison.Ordinal) ||
                        matches.Length != 2 ||
                        matches[0].Id != prefixTask.Id ||
                        !matches.Any(task => task.Id == containsTask.Id) ||
                        matches.Any(task =>
                            string.Equals(task.Name, "Rigging setup", StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            "Typing in the tracker task field did not open the correctly ranked matching tasks.");
                    }

                    taskEditor.Text = "no existing task matches this";
                    await Task.Delay(100);
                    if (taskCombo.IsDropDownOpen ||
                        taskCombo.Items.Count != 0 ||
                        !string.Equals(
                            taskCombo.Text,
                            "no existing task matches this",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "An unmatched tracker task search did not remain available as a new task name.");
                    }

                    taskEditor.Text = string.Empty;
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_HISTORY_DESCRIPTION_FILTER"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync(
                        $"History description client {Guid.NewGuid():N}",
                        "#766F80");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"History description project {Guid.NewGuid():N}",
                        "#0D8F68");
                    var task = await _store.AddTaskAsync(project.Id, "Description search task");
                    const string token = "Blue Orchard";
                    var endUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                    await _store.AddManualEntryAsync(
                        project.Id,
                        task.Id,
                        $"Layout polish for {token}",
                        endUtc.AddMinutes(-20),
                        endUtc.AddMinutes(-10));
                    await _store.AddManualEntryAsync(
                        project.Id,
                        task.Id,
                        "Unrelated description",
                        endUtc.AddMinutes(-10),
                        endUtc);
                    var seededEntries = await _store.GetEntriesAsync(
                        endUtc.AddMinutes(-21),
                        endUtc.AddMinutes(1));
                    var matchingEntryId = seededEntries.Single(entry =>
                        string.Equals(
                            entry.Description,
                            $"Layout polish for {token}",
                            StringComparison.Ordinal)).Id;
                    var otherEntryId = seededEntries.Single(entry =>
                        string.Equals(
                            entry.Description,
                            "Unrelated description",
                            StringComparison.Ordinal)).Id;

                    await _mainWindow.RefreshAllAsync();
                    var descriptionFilter = (TextBox)_mainWindow.FindName("HistoryDescriptionFilterText");
                    var historyGrid = (DataGrid)_mainWindow.FindName("HistoryGrid");
                    descriptionFilter.Text = token.ToLowerInvariant();
                    await Task.Delay(100);

                    var filteredRows = historyGrid.Items
                        .OfType<ViewModels.TimeEntryRow>()
                        .ToArray();
                    if (filteredRows.Length != 1 ||
                        filteredRows[0].Entry.Id != matchingEntryId ||
                        filteredRows.Any(row => row.Entry.Id == otherEntryId))
                    {
                        throw new InvalidOperationException(
                            "History description search did not apply a case-insensitive partial-text filter.");
                    }

                    descriptionFilter.Clear();
                    await Task.Delay(100);
                    var restoredIds = historyGrid.Items
                        .OfType<ViewModels.TimeEntryRow>()
                        .Select(row => row.Entry.Id)
                        .ToHashSet();
                    if (!restoredIds.Contains(matchingEntryId) || !restoredIds.Contains(otherEntryId))
                    {
                        throw new InvalidOperationException(
                            "Clearing the History description search did not restore the entries.");
                    }

                    descriptionFilter.Text = token.ToLowerInvariant();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_HISTORY_CONTINUE"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync(
                        $"History continue client {Guid.NewGuid():N}",
                        "#766F80");
                    var sourceProject = await _store.AddProjectAsync(
                        client.Id,
                        $"History continue project {Guid.NewGuid():N}",
                        "#0D8F68");
                    var sourceTask = await _store.AddTaskAsync(sourceProject.Id, "Continue source task");
                    var otherProject = await _store.AddProjectAsync(
                        client.Id,
                        $"Existing timer project {Guid.NewGuid():N}",
                        "#7B8495");
                    var otherTask = await _store.AddTaskAsync(otherProject.Id, "Existing timer task");
                    const string sourceDescription = "Continue source work #animation #review";
                    var nowUtc = DateTimeOffset.UtcNow;
                    await _store.AddManualEntryAsync(
                        sourceProject.Id,
                        sourceTask.Id,
                        sourceDescription,
                        nowUtc.AddMinutes(-40),
                        nowUtc.AddMinutes(-30));
                    var sourceEntry = (await _store.GetEntriesAsync(
                            nowUtc.AddHours(-1),
                            nowUtc.AddMinutes(-20)))
                        .Single(entry => string.Equals(
                            entry.Description,
                            sourceDescription,
                            StringComparison.Ordinal));

                    var previousRunning = await _controller.StartTimerAsync(
                        otherProject.Id,
                        TrackingSource.Manual,
                        showDetails: false,
                        initialDescription: "Existing work before Continue",
                        initialTaskId: otherTask.Id);
                    await _mainWindow.RefreshAllAsync();
                    if (((DataGrid)_mainWindow.FindName("HistoryGrid")).ContextMenu?.Items
                            .OfType<MenuItem>()
                            .All(item => !string.Equals(item.Header as string, "Continue", StringComparison.Ordinal)) != false)
                    {
                        throw new InvalidOperationException(
                            "History does not expose the Continue context-menu command.");
                    }

                    await _mainWindow.ContinueHistoryEntryForPreviewAsync(sourceEntry.Id);
                    var continued = _controller.RunningEntry
                        ?? throw new InvalidOperationException(
                            "Continuing a History entry did not start a timer.");
                    var continuedViews = await _store.GetEntriesAsync(
                        nowUtc.AddHours(-1),
                        DateTimeOffset.UtcNow.AddMinutes(1));
                    var storedSource = continuedViews.Single(entry => entry.Id == sourceEntry.Id);
                    var stoppedPrevious = continuedViews.SingleOrDefault(entry => entry.Id == previousRunning.Id);
                    var continuedView = continuedViews.Single(entry => entry.Id == continued.Id);
                    if (continued.ProjectId != sourceEntry.ProjectId ||
                        continued.TaskId != sourceEntry.TaskId ||
                        !string.Equals(continued.Description, sourceEntry.Description, StringComparison.Ordinal) ||
                        continued.Source != TrackingSource.Manual ||
                        stoppedPrevious is not null && stoppedPrevious.EndUtc != continued.StartUtc ||
                        storedSource.EndUtc != sourceEntry.EndUtc ||
                        !string.Equals(continuedView.ClientName, sourceEntry.ClientName, StringComparison.Ordinal) ||
                        !TagParser.Extract(continued.Description)
                            .SequenceEqual(
                                TagParser.Extract(sourceEntry.Description),
                                StringComparer.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "History Continue did not atomically copy the source project, client, task, description, and tags.");
                    }

                    for (var attempt = 0;
                         attempt < 30 &&
                         (((ComboBox)_mainWindow.FindName("TimerTaskCombo")).SelectedValue as Guid? != sourceTask.Id ||
                          !string.Equals(
                              ((Controls.TagDescriptionEditor)_mainWindow.FindName("TimerDescriptionText")).Text,
                              sourceDescription,
                              StringComparison.Ordinal));
                         attempt++)
                    {
                        await Task.Delay(100);
                    }

                    if (((ComboBox)_mainWindow.FindName("TimerProjectCombo")).SelectedValue as Guid? != sourceProject.Id ||
                        ((ComboBox)_mainWindow.FindName("TimerTaskCombo")).SelectedValue as Guid? != sourceTask.Id ||
                        !string.Equals(
                            ((Controls.TagDescriptionEditor)_mainWindow.FindName("TimerDescriptionText")).Text,
                            sourceDescription,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The timer bar did not update to the continued History entry.");
                    }

                    await _controller.StopTimerAsync();
                }

                if (verifySessionBehavior)
                {
                    if (_mainWindow.FindName("SessionBehaviorCombo") is not ComboBox sessionBehaviorCombo ||
                        _mainWindow.FindName("SessionBehaviorDescriptionText") is not TextBlock sessionDescription)
                    {
                        throw new InvalidOperationException(
                            "Settings is missing the Windows session behavior selector or explanation.");
                    }

                    var client = await _store.AddClientAsync(
                        $"Session behavior client {Guid.NewGuid():N}",
                        "#766F80");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"Session behavior project {Guid.NewGuid():N}",
                        "#0D8F68");
                    var task = await _store.AddTaskAsync(project.Id, "Session behavior task");

                    sessionBehaviorCombo.SelectedIndex = 0;
                    await Task.Delay(100);
                    if (_controller.SessionTrackingBehavior != SessionTrackingBehavior.StopTimer)
                    {
                        throw new InvalidOperationException(
                            "The Settings selector did not persist the stop-on-lock behavior.");
                    }

                    var stoppedEntry = await _controller.StartTimerAsync(
                        project.Id,
                        TrackingSource.Manual,
                        showDetails: false,
                        initialDescription: "Stop when Windows locks",
                        initialTaskId: task.Id);
                    var stoppedAt = stoppedEntry.StartUtc.AddMinutes(2);
                    await _controller.HandleSessionChangedForPreviewAsync(
                        SystemSessionEvent.Locked,
                        stoppedAt);
                    if (_controller.RunningEntry is not null)
                    {
                        throw new InvalidOperationException(
                            "The stop session behavior left the timer running after Windows locked.");
                    }

                    var storedStoppedEntry = (await _store.GetEntriesAsync(
                            stoppedEntry.StartUtc.AddMinutes(-1),
                            stoppedAt.AddMinutes(1)))
                        .Single(entry => entry.Id == stoppedEntry.Id);
                    if (storedStoppedEntry.EndUtc != stoppedAt)
                    {
                        throw new InvalidOperationException(
                            "The stop session behavior did not close the entry at the lock timestamp.");
                    }

                    await _controller.HandleSessionChangedForPreviewAsync(
                        SystemSessionEvent.Unlocked,
                        stoppedAt.AddMinutes(1));
                    if (_detailsWindow?.EntryId != stoppedEntry.Id || !_detailsWindow.IsVisible)
                    {
                        throw new InvalidOperationException(
                            "Returning after a session-caused stop did not open that entry's details popup.");
                    }

                    _detailsWindow.CloseWithoutSaving();
                    sessionBehaviorCombo.SelectedIndex = 1;
                    await Task.Delay(100);
                    if (_controller.SessionTrackingBehavior != SessionTrackingBehavior.KeepRunningAndExclude ||
                        !sessionDescription.Text.Contains("asks whether", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The Settings selector did not persist or explain the inactive-time review choice.");
                    }

                    var continuedEntry = await _controller.StartTimerAsync(
                        project.Id,
                        TrackingSource.Manual,
                        showDetails: false,
                        initialDescription: "Continue and subtract sleep",
                        initialTaskId: task.Id);
                    var unavailableAt = continuedEntry.StartUtc.AddMinutes(1);
                    var resumedAt = unavailableAt.AddMinutes(9);
                    await _controller.HandleSessionChangedForPreviewAsync(
                        SystemSessionEvent.Suspending,
                        unavailableAt);
                    await _controller.ResumeUnavailableSessionForPreviewAsync(
                        resumedAt,
                        remove: false);
                    if (_controller.RunningEntry?.Id != continuedEntry.Id ||
                        await _store.GetEntryExcludedSecondsAsync(continuedEntry.Id) != 0)
                    {
                        throw new InvalidOperationException(
                            "Keeping inactive time changed the running timer or created an exclusion.");
                    }

                    var secondUnavailableAt = resumedAt.AddMinutes(1);
                    var secondResumedAt = secondUnavailableAt.AddMinutes(2);
                    await _controller.HandleSessionChangedForPreviewAsync(
                        SystemSessionEvent.Locked,
                        secondUnavailableAt);
                    await _controller.ResumeUnavailableSessionForPreviewAsync(
                        secondResumedAt,
                        remove: true);
                    if (_controller.RunningEntry?.Id != continuedEntry.Id ||
                        await _store.GetEntryExcludedSecondsAsync(continuedEntry.Id) != 2 * 60)
                    {
                        throw new InvalidOperationException(
                            "Cutting reviewed inactive time did not preserve the timer and create the chosen exclusion.");
                    }

                    sessionBehaviorCombo.SelectedIndex = 0;
                    await Task.Delay(100);
                    await _controller.HandleSessionChangedForPreviewAsync(
                        SystemSessionEvent.Locked,
                        secondResumedAt.AddMinutes(2));
                    if (_controller.RunningEntry is not null)
                    {
                        throw new InvalidOperationException(
                            "The session behavior smoke timer was not stopped during cleanup.");
                    }

                    await _controller.HandleSessionChangedForPreviewAsync(
                        SystemSessionEvent.Unlocked,
                        secondResumedAt.AddMinutes(3));
                    _detailsWindow?.CloseWithoutSaving();
                }

                var smokeView = Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VIEW");
                if ((string.Equals(smokeView, "Clients", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(smokeView, "ClientsExpanded", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(smokeView, "Projects", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(smokeView, "Targets", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(smokeView, "TargetsContextMenu", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(smokeView, "Tasks", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(smokeView, "Tags", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(smokeView, "Software", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(smokeView, "Rules", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(smokeView, "RuleDialog", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(smokeView, "BulkEditProjects", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(smokeView, "Reports", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(smokeView, "ReportTaskHistory", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(smokeView, "Settings", StringComparison.OrdinalIgnoreCase)) &&
                    _mainWindow.FindName("MainTabs") is TabControl mainTabs &&
                    _mainWindow.FindName("ManagementTabs") is TabControl managementTabs)
                {
                    mainTabs.SelectedIndex = string.Equals(smokeView, "Settings", StringComparison.OrdinalIgnoreCase)
                        ? 3
                        : string.Equals(smokeView, "Reports", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(smokeView, "ReportTaskHistory", StringComparison.OrdinalIgnoreCase)
                            ? 2
                            : 1;
                    managementTabs.SelectedIndex = string.Equals(smokeView, "Projects", StringComparison.OrdinalIgnoreCase)
                        ? 1
                        : string.Equals(smokeView, "Targets", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(smokeView, "TargetsContextMenu", StringComparison.OrdinalIgnoreCase)
                            ? 2
                        : string.Equals(smokeView, "Tasks", StringComparison.OrdinalIgnoreCase)
                            ? 3
                            : string.Equals(smokeView, "Tags", StringComparison.OrdinalIgnoreCase)
                                ? 4
                                : string.Equals(smokeView, "Software", StringComparison.OrdinalIgnoreCase)
                                    ? 5
                                    : string.Equals(smokeView, "Rules", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(smokeView, "RuleDialog", StringComparison.OrdinalIgnoreCase)
                                    ? 6
                                    : 0;
                    _mainWindow.UpdateLayout();
                    if (string.Equals(smokeView, "ClientsExpanded", StringComparison.OrdinalIgnoreCase) &&
                        _mainWindow.FindName("ClientsGrid") is ListBox clientsGrid &&
                        FindVisualDescendant<Expander>(clientsGrid) is { } firstClient)
                    {
                        firstClient.IsExpanded = true;
                    }
                }

                if (string.Equals(smokeView, "ReportTaskHistory", StringComparison.OrdinalIgnoreCase))
                {
                    await _mainWindow.ShowFirstReportTaskInHistoryForPreviewAsync();
                }

                if (Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_DESCRIPTION") is { Length: > 0 } smokeDescription &&
                    _mainWindow.FindName("TimerDescriptionText") is Controls.TagDescriptionEditor descriptionEditor)
                {
                    descriptionEditor.Text = smokeDescription;
                }

                var smokeTaskName = Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_TASK");
                if (!string.IsNullOrWhiteSpace(smokeTaskName) &&
                    _mainWindow.FindName("TimerTaskCombo") is ComboBox timerTaskCombo)
                {
                    timerTaskCombo.SelectedIndex = -1;
                    timerTaskCombo.Text = smokeTaskName;
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_START_TYPED_TASK"),
                        "true",
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(smokeTaskName) &&
                    _mainWindow.FindName("StartStopButton") is Button startStopButton)
                {
                    startStopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (var attempt = 0; attempt < 50 && _controller.RunningEntry?.TaskId is null; attempt++)
                    {
                        await Task.Delay(100);
                    }

                    var running = _controller.RunningEntry;
                    var savedTasks = running is null
                        ? []
                        : await _store.GetTasksAsync(running.ProjectId);
                    if (running?.TaskId is not { } runningTaskId ||
                        !savedTasks.Any(task =>
                            task.Id == runningTaskId &&
                            string.Equals(task.Name, smokeTaskName.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException("The typed timer task was not saved when the timer started.");
                    }

                    startStopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (var attempt = 0; attempt < 50 && _controller.RunningEntry is not null; attempt++)
                    {
                        await Task.Delay(100);
                    }
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_TAB_FILTER_RESETS"),
                        "true",
                        StringComparison.OrdinalIgnoreCase) &&
                    _mainWindow.FindName("MainTabs") is TabControl filterTabs)
                {
                    var today = DateTime.Today;
                    var historyMonthStart = new DateTime(today.Year, today.Month, 1);
                    var historyMonthEnd = historyMonthStart.AddMonths(1).AddDays(-1);
                    filterTabs.SelectedIndex = 2;
                    await Task.Delay(350);
                    ((DateRangePicker)_mainWindow.FindName("ReportRangePicker"))
                        .SetRange(today.AddMonths(-2), today.AddMonths(-1));
                    ((ComboBox)_mainWindow.FindName("ReportPaidCombo")).SelectedIndex = 2;
                    filterTabs.SelectedIndex = 3;
                    filterTabs.SelectedIndex = 2;
                    await Task.Delay(500);

                    var reportRange = (DateRangePicker)_mainWindow.FindName("ReportRangePicker");
                    var reportCombos = new[]
                    {
                        (ComboBox)_mainWindow.FindName("ReportClientCombo"),
                        (ComboBox)_mainWindow.FindName("ReportProjectCombo"),
                        (ComboBox)_mainWindow.FindName("ReportTaskCombo"),
                        (ComboBox)_mainWindow.FindName("ReportTagCombo"),
                        (ComboBox)_mainWindow.FindName("ReportPaidCombo"),
                    };
                    if (reportRange.StartDate != new DateTime(today.Year, today.Month, 1) ||
                        reportRange.EndDate != today ||
                        reportCombos.Any(combo => combo.SelectedIndex != 0))
                    {
                        throw new InvalidOperationException("Report filters did not reset when the tab was re-entered.");
                    }

                    var historyRange = (DateRangePicker)_mainWindow.FindName("HistoryRangePicker");
                    var historyProject = (ComboBox)_mainWindow.FindName("HistoryProjectCombo");
                    var historyTask = (ComboBox)_mainWindow.FindName("HistoryTaskCombo");
                    var historyTag = (ComboBox)_mainWindow.FindName("HistoryTagCombo");
                    var historyDescription = (TextBox)_mainWindow.FindName("HistoryDescriptionFilterText");
                    historyRange.SetRange(today.AddMonths(-3), today.AddMonths(-2));
                    historyProject.SelectedIndex = historyProject.Items.Count > 1 ? 1 : 0;
                    historyTag.SelectedIndex = historyTag.Items.Count > 1 ? 1 : 0;
                    historyDescription.Text = "reset this description search";
                    filterTabs.SelectedIndex = 3;
                    filterTabs.SelectedIndex = 0;
                    await Task.Delay(500);

                    if (historyRange.StartDate != historyMonthStart ||
                        historyRange.EndDate != historyMonthEnd ||
                        historyProject.SelectedIndex != 0 ||
                        historyTask.SelectedIndex != 0 ||
                        historyTag.SelectedIndex != 0 ||
                        historyDescription.Text.Length != 0)
                    {
                        throw new InvalidOperationException("History filters did not reset when the tab was re-entered.");
                    }
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_DATE_RANGES"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var historyRange = (DateRangePicker)_mainWindow.FindName("HistoryRangePicker");
                    var today = DateTime.Today;
                    var defaultMonthStart = new DateTime(today.Year, today.Month, 1);
                    var defaultMonthEnd = defaultMonthStart.AddMonths(1).AddDays(-1);
                    var historyStart = historyRange.StartDate ?? defaultMonthStart;
                    var historyEnd = historyRange.EndDate ?? defaultMonthEnd;
                    if (!historyRange.SetTextForPreview("15.07.2026 - 02.07.2026") ||
                        historyRange.StartDate != new DateTime(2026, 7, 2) ||
                        historyRange.EndDate != new DateTime(2026, 7, 15) ||
                        !string.Equals(historyRange.TextForPreview, "02.07.2026 - 15.07.2026", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Manual date-range input was not parsed and normalized.");
                    }

                    historyRange.SetRange(historyStart, historyEnd, notify: false);

                    if (_mainWindow.FindName("MainTabs") is TabControl dateRangeTabs)
                    {
                        dateRangeTabs.SelectedIndex = 2;
                        await Task.Delay(150);
                    }

                    var reportRange = (DateRangePicker)_mainWindow.FindName("ReportRangePicker");
                    var reportStart = reportRange.StartDate ?? DateTime.Today;
                    var reportEnd = reportRange.EndDate ?? reportStart;
                    reportRange.IsCalendarOpen = true;
                    reportRange.SelectCalendarDateForPreview(new DateTime(2026, 7, 2));
                    if (reportRange.StartDate != new DateTime(2026, 7, 2) ||
                        reportRange.EndDate != new DateTime(2026, 7, 2) ||
                        reportRange.SelectedDateCountForPreview != 1 ||
                        !reportRange.IsCalendarOpen)
                    {
                        throw new InvalidOperationException("The first calendar click did not immediately apply a single-day range.");
                    }

                    reportRange.SelectCalendarDateForPreview(new DateTime(2026, 7, 15));
                    if (reportRange.StartDate != new DateTime(2026, 7, 2) ||
                        reportRange.EndDate != new DateTime(2026, 7, 15) ||
                        reportRange.SelectedDateCountForPreview != 14 ||
                        !reportRange.IsCalendarOpen)
                    {
                        throw new InvalidOperationException("The second calendar click did not expand the single day into a range.");
                    }

                    reportRange.IsCalendarOpen = false;
                    if (reportRange.StartDate != new DateTime(2026, 7, 2) ||
                        reportRange.EndDate != new DateTime(2026, 7, 15))
                    {
                        throw new InvalidOperationException("The selected date range did not persist after the calendar closed.");
                    }

                    var reportThisWeek = (Button)_mainWindow.FindName("ReportThisWeekButton");
                    reportThisWeek.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var expectedWeek = CalendarDateRangePresets.Resolve(
                        today,
                        CalendarDateRangePreset.ThisWeek);
                    if (reportRange.StartDate != expectedWeek.Start ||
                        reportRange.EndDate != expectedWeek.End ||
                        !ProjectTimeTracker.Windows.MainWindow.GetDateRangeShortcutActive(reportThisWeek) ||
                        ProjectTimeTracker.Windows.MainWindow.GetDateRangeShortcutActive((Button)_mainWindow.FindName("ReportThisMonthButton")) ||
                        ProjectTimeTracker.Windows.MainWindow.GetDateRangeShortcutActive((Button)_mainWindow.FindName("ReportTodayButton")))
                    {
                        throw new InvalidOperationException(
                            "The Reports This week shortcut did not apply Monday through Sunday as the sole active neutral preset.");
                    }

                    var historyToday = (Button)_mainWindow.FindName("HistoryTodayButton");
                    historyToday.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (historyRange.StartDate != today ||
                        historyRange.EndDate != today ||
                        !ProjectTimeTracker.Windows.MainWindow.GetDateRangeShortcutActive(historyToday))
                    {
                        throw new InvalidOperationException(
                            "The History Today shortcut did not apply and indicate a single-day range.");
                    }

                    var historyThisMonth = (Button)_mainWindow.FindName("HistoryThisMonthButton");
                    historyThisMonth.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (historyRange.StartDate != defaultMonthStart ||
                        historyRange.EndDate != defaultMonthEnd ||
                        !ProjectTimeTracker.Windows.MainWindow.GetDateRangeShortcutActive(historyThisMonth) ||
                        ProjectTimeTracker.Windows.MainWindow.GetDateRangeShortcutActive(historyToday))
                    {
                        throw new InvalidOperationException(
                            "The History This month shortcut did not apply and indicate the complete calendar month.");
                    }

                    reportRange.SetRange(reportStart, reportEnd, notify: false);
                    historyRange.SetRange(historyStart, historyEnd, notify: false);
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_OBJECT_INTERACTIONS"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _mainWindow.VerifyObjectInteractionContractForPreview();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_SETTINGS_CATEGORIES"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _mainWindow.VerifySettingsCategoriesForPreview();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_TRELLO_UI"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _mainWindow.VerifyTrelloUiForPreview();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_BRANDING"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _mainWindow.VerifyBrandingForPreview();
                }

                if (Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_EXPECT_HISTORY_MONTH") is { Length: 7 } expectedHistoryMonth &&
                    DateTime.TryParseExact(
                        expectedHistoryMonth,
                        "yyyy-MM",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var expectedHistoryDate))
                {
                    _mainWindow.VerifyHistoryDefaultMonthForPreview(
                        expectedHistoryDate.Year,
                        expectedHistoryDate.Month);
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_RECENT_ENTRY_RESUME"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync(
                        $"Recent resume client {Guid.NewGuid():N}",
                        "#766F80");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"Recent resume project {Guid.NewGuid():N}",
                        "#0D8F68");
                    var task = await _store.AddTaskAsync(project.Id, "Resume matching task");
                    const string description = "Resume matching details #animation";
                    var stoppedAt = _controller.UtcNow.AddSeconds(-30);
                    await _store.AddManualEntryAsync(
                        project.Id,
                        task.Id,
                        description,
                        stoppedAt.AddMinutes(-10),
                        stoppedAt);
                    var previous = (await _store.GetEntriesAsync(
                            stoppedAt.AddMinutes(-11),
                            stoppedAt.AddMinutes(1)))
                        .Single(entry => entry.ProjectId == project.Id);

                    await _mainWindow.VerifyExcludedSoftwareReviewSettingForPreviewAsync();
                    var resumed = await _controller.StartTimerAsync(
                        project.Id,
                        TrackingSource.Manual,
                        showDetails: false,
                        initialDescription: description,
                        initialTaskId: task.Id);
                    if (resumed.Id != previous.Id ||
                        resumed.StartUtc != previous.StartUtc ||
                        !resumed.IsRunning ||
                        await _store.GetSettingAsync(
                            RecentEntryResumeSettings.MaximumGapMinutesKey) != "2")
                    {
                        throw new InvalidOperationException(
                            "A matching recent entry was not resumed with the configured Settings threshold.");
                    }

                    await _controller.StopTimerAsync();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_TIMER_BAR_INTERACTIONS"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await _mainWindow.VerifyTimerBarInteractionsForPreviewAsync();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_IDLE_PROTECTION"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await _mainWindow.VerifyIdleProtectionSettingsForPreviewAsync();
                    await _controller.SetCallsIdleProtectionEnabledAsync(false);
                    await _controller.SetVideoIdleProtectionEnabledAsync(false);
                    var client = await _store.AddClientAsync(
                        $"Idle protection client {Guid.NewGuid():N}",
                        "#766F80");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"Idle protection project {Guid.NewGuid():N}",
                        "#339CFF");
                    await _controller.StartTimerAsync(
                        project.Id,
                        TrackingSource.Manual,
                        showDetails: false);
                    var previousAccumulated =
                        _controller.PendingAccumulatedAwaySecondsForPreview;
                    var idleStarted = _controller.UtcNow;
                    _controller.BeginIdleForPreview(idleStarted);
                    await _controller.ApplyIdleProtectionStateForPreviewAsync(
                        new IdleProtectionState(
                            IdleProtectionReason.CommunicationAudio,
                            CallsAvailable: true,
                            VideoAvailable: true,
                            IsInitialized: true,
                            idleStarted.AddSeconds(20)));
                    await _controller.ApplyIdleProtectionStateForPreviewAsync(
                        new IdleProtectionState(
                            IdleProtectionReason.None,
                            CallsAvailable: true,
                            VideoAvailable: true,
                            IsInitialized: true,
                            idleStarted.AddSeconds(50)));
                    if (_controller.PendingAccumulatedAwaySecondsForPreview -
                            previousAccumulated != 20 ||
                        await _store.GetEntryExcludedSecondsAsync(
                            _controller.RunningEntry!.Id) != 0)
                    {
                        throw new InvalidOperationException(
                            "Mid-idle protection did not retain only the real idle portion without creating a protected-time exclusion.");
                    }

                    await _controller.StopTimerAsync();
                    await _store.ArchiveProjectAsync(project.Id);
                    await _store.ArchiveClientAsync(client.Id);
                    await _controller.SetCallsIdleProtectionEnabledAsync(true);
                    await _controller.SetVideoIdleProtectionEnabledAsync(true);
                    await _mainWindow.RefreshAllAsync();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_SIDEBAR_TARGET_RESIZE"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await _mainWindow.VerifySidebarTargetsPanelResizeForPreviewAsync();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_SIDEBAR_TARGET_SELECTION"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _mainWindow.VerifySidebarTargetSelectionStyleForPreview();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_TARGET_PROGRESS_RING"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _mainWindow.VerifySidebarTargetProgressRingForPreview();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_UNIFIED_TARGET_VIEWS"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync(
                        $"Unified target client {Guid.NewGuid():N}",
                        "#766F80");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"Unified target project {Guid.NewGuid():N}",
                        "#339CFF");
                    await _store.UpdateProjectSettingsAsync(project.Id, 1, 10, 40, null, "PLN");
                    var otherProject = await _store.AddProjectAsync(
                        client.Id,
                        $"Other monthly target project {Guid.NewGuid():N}",
                        "#766F80");
                    await _store.UpdateProjectSettingsAsync(
                        otherProject.Id,
                        null,
                        null,
                        20,
                        null,
                        "PLN");
                    var scopedMonthly = await _store.AddCustomTargetAsync(
                        "Scoped monthly delivery",
                        project.Id,
                        CustomTargetCadence.Monthly,
                        12);
                    var globalMonthly = await _store.AddCustomTargetAsync(
                        "Global monthly delivery",
                        projectId: null,
                        CustomTargetCadence.Monthly,
                        160);
                    var scopedOneTime = await _store.AddCustomTargetAsync(
                        "Scoped one-time delivery",
                        project.Id,
                        CustomTargetCadence.OneTime,
                        4);
                    var expiredOneTime = await _store.AddCustomTargetAsync(
                        "Expired one-time delivery",
                        project.Id,
                        CustomTargetCadence.OneTime,
                        2);
                    await _store.SetCustomTargetCompletionAsync(
                        expiredOneTime.Id,
                        _controller.UtcNow.AddDays(-8));
                    var endUtc = DateTimeOffset.UtcNow;
                    await _store.AddManualEntryAsync(
                        project.Id,
                        taskId: null,
                        "Unified target smoke entry",
                        endUtc.AddMinutes(-10),
                        endUtc);
                    await _mainWindow.RefreshAllAsync();
                    _mainWindow.VerifyUnifiedTargetViewsForPreview(
                        project.Id,
                        otherProject.Id,
                        scopedMonthly.Id,
                        globalMonthly.Id,
                        scopedOneTime.Id,
                        expiredOneTime.Id);
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_DEBT_CANCELLATION"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    ProjectTimeTracker.Windows.MainWindow.VerifyTargetDebtReductionInputForPreview();
                    var client = await _store.AddClientAsync(
                        $"Debt cancel client {Guid.NewGuid():N}",
                        "#766F80");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"Debt cancel project {Guid.NewGuid():N}",
                        "#339CFF");
                    await _store.UpdateProjectSettingsAsync(project.Id, null, null, 160, null, "PLN", true);
                    var currentMonth = TrackingPeriodCalculator.CurrentMonth(
                        _controller.UtcNow,
                        TimeZoneInfo.Local);
                    var previousMonthDate = TimeZoneInfo.ConvertTime(currentMonth.StartUtc, TimeZoneInfo.Local)
                        .Date
                        .AddMonths(-1);
                    var previousMonth = TrackingPeriodCalculator.MonthContaining(
                        previousMonthDate,
                        TimeZoneInfo.Local);
                    await _store.AddManualEntryAsync(
                        project.Id,
                        taskId: null,
                        "Debt cancellation smoke entry",
                        previousMonth.StartUtc.AddHours(1),
                        previousMonth.StartUtc.AddHours(155));
                    var debt = (await _store.GetProjectTargetDebtsAsync(
                            _controller.UtcNow,
                            TimeZoneInfo.Local))
                        .Single(item => item.ProjectId == project.Id);
                    if (debt.OutstandingSeconds != 6 * 3600)
                    {
                        throw new InvalidOperationException("The debt cancellation smoke setup did not create six hours of debt.");
                    }

                    var canceledAt = _controller.UtcNow;
                    _ = await _store.CancelProjectTargetDebtAsync(project.Id, 2 * 3600, canceledAt);
                    await _mainWindow.RefreshAllAsync();
                    var targetsGrid = (DataGrid)_mainWindow.FindName("CustomTargetsGrid");
                    var partiallyLoweredTarget = targetsGrid.ItemsSource
                        .OfType<CustomTargetRow>()
                        .Single(row => row.Target.ProjectId == project.Id &&
                            row.Target.Cadence == CustomTargetCadence.Monthly);
                    var partiallyLoweredSidebarTarget = ((ListBox)_mainWindow.FindName("TargetsGrid"))
                        .ItemsSource
                        .OfType<ProjectTargetRow>()
                        .Single(row => row.Project.Id == project.Id &&
                            row.CustomTarget?.Cadence == CustomTargetCadence.Monthly);
                    if (!partiallyLoweredTarget.CanCancelDebt ||
                        !partiallyLoweredTarget.Debt.Contains("+4h", StringComparison.Ordinal) ||
                        !partiallyLoweredTarget.Debt.Contains("lowered by +2h", StringComparison.OrdinalIgnoreCase) ||
                        !partiallyLoweredTarget.Debt.Contains(
                            AppTextCulture.FormatShortDate(canceledAt.ToLocalTime()),
                            StringComparison.Ordinal) ||
                        !partiallyLoweredSidebarTarget.HasCanceledDebt ||
                        !partiallyLoweredSidebarTarget.HasDebt ||
                        !partiallyLoweredSidebarTarget.CanceledDebt.Contains(
                            "Lowered by +2h",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Partially lowered debt does not show its remaining amount, reduction, and date.");
                    }

                    var firstAdjustments = await _store.GetProjectTargetDebtCancellationsAsync(project.Id);
                    if (firstAdjustments.Count != 1)
                    {
                        throw new InvalidOperationException(
                            "The partial debt reduction did not create one dated adjustment.");
                    }

                    var firstAdjustment = firstAdjustments[0];
                    if (firstAdjustment.CanceledSeconds != 2 * 3600 ||
                        firstAdjustment.CanceledUtc != canceledAt)
                    {
                        throw new InvalidOperationException(
                            "The partial debt reduction did not retain its amount and timestamp.");
                    }

                    _ = await _store.CancelProjectTargetDebtAsync(project.Id, 4 * 3600, canceledAt);
                    await _mainWindow.RefreshAllAsync();
                    var monthlyTarget = targetsGrid.ItemsSource
                        .OfType<CustomTargetRow>()
                        .Single(row => row.Target.ProjectId == project.Id &&
                            row.Target.Cadence == CustomTargetCadence.Monthly);
                    var sidebarTarget = ((ListBox)_mainWindow.FindName("TargetsGrid"))
                        .ItemsSource
                        .OfType<ProjectTargetRow>()
                        .Single(row => row.Project.Id == project.Id &&
                            row.CustomTarget?.Cadence == CustomTargetCadence.Monthly);
                    if (monthlyTarget.CanCancelDebt ||
                        !monthlyTarget.Debt.Contains("Canceled", StringComparison.Ordinal) ||
                        !sidebarTarget.HasCanceledDebt ||
                        sidebarTarget.HasDebt)
                    {
                        throw new InvalidOperationException(
                            "Canceled debt is not reflected in the Targets tab and sidebar.");
                    }

                    var cancellations = await _store.GetProjectTargetDebtCancellationsAsync(project.Id);
                    if (cancellations.Count != 2 || cancellations.Sum(item => item.CanceledSeconds) != 6 * 3600)
                    {
                        throw new InvalidOperationException(
                            "The partial reduction and final cancellation were not retained as reversible adjustments.");
                    }
                    var currentProject = (await _store.GetProjectsAsync())
                        .Single(item => item.Id == project.Id);
                    var currentTargets = (await _store.GetCustomTargetsAsync())
                        .Where(item => item.ProjectId == project.Id)
                        .ToArray();
                    var dialog = new ProjectSettingsWindow(
                        currentProject,
                        client.Name,
                        [client],
                        cancellations,
                        currentTargets)
                    {
                        Owner = _mainWindow,
                    };
                    dialog.Show();
                    dialog.UpdateLayout();
                    if (dialog.CanceledDebtPanel.Visibility != Visibility.Visible ||
                        !dialog.CanceledDebtText.Text.Contains(
                            AppTextCulture.FormatShortDate(canceledAt.ToLocalTime()),
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Project target editing does not show the remembered debt cancellation date.");
                    }

                    dialog.RestoreCanceledDebtForPreview();
                    dialog.SubmitForPreview();
                    var restoreRequested = dialog.Result?.RestoreCanceledDebt == true;
                    dialog.Close();
                    if (!restoreRequested)
                    {
                        throw new InvalidOperationException(
                            "Bring debt back was not retained by the target editor.");
                    }

                    await _store.RestoreProjectTargetDebtAsync(project.Id, _controller.UtcNow);
                    var restoredDebt = (await _store.GetProjectTargetDebtsAsync(
                            _controller.UtcNow,
                            TimeZoneInfo.Local))
                        .Single(item => item.ProjectId == project.Id);
                    if (restoredDebt.OutstandingSeconds != 6 * 3600 || restoredDebt.HasCanceledDebt)
                    {
                        throw new InvalidOperationException(
                            "Restoring canceled debt did not bring the original debt back.");
                    }
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_INACTIVE_EDITOR_FOCUS"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _mainWindow.VerifyInactiveSurfaceClearsTimerEditorFocusForPreview();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_HISTORY_FILTERS"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await _mainWindow.VerifyHistoryFiltersForPreviewAsync();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_TOPMOST_TIME_REVIEW"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var review = ThemedMessageBox.CreateTopmostTimeReviewForPreview();
                    review.Show();
                    review.UpdateLayout();
                    if (!review.Topmost)
                    {
                        throw new InvalidOperationException(
                            "The away-time review did not open as an always-on-top window.");
                    }

                    review.Close();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_IDLE_REVIEW_QUEUE"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync(
                        $"Idle queue client {Guid.NewGuid():N}",
                        "#687582");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"Idle queue project {Guid.NewGuid():N}",
                        "#339CFF");
                    var task = await _store.AddTaskAsync(project.Id, "Queued idle review");
                    var runningEntry = await _controller.StartTimerAsync(
                        project.Id,
                        TrackingSource.Manual,
                        showDetails: false,
                        initialDescription: "Queue smoke test",
                        initialTaskId: task.Id);
                    var promptTitles = new List<string>();
                    var promptMessages = new List<string>();
                    var queuedReviews = new List<Task>();
                    var promptDepth = 0;
                    var maximumPromptDepth = 0;
                    var queueBaseUtc = _controller.UtcNow.AddSeconds(1);

                    _controller.SetIdleReviewPromptForPreview((message, title) =>
                    {
                        promptDepth++;
                        maximumPromptDepth = Math.Max(maximumPromptDepth, promptDepth);
                        try
                        {
                            promptMessages.Add(message);
                            promptTitles.Add(title);
                            if (promptTitles.Count < 3)
                            {
                                var nextIdleStartUtc = queueBaseUtc.AddMinutes(
                                    promptTitles.Count * 10);
                                _controller.BeginIdleForPreview(nextIdleStartUtc);
                                queuedReviews.Add(
                                    _controller.CompleteIdleForPreviewAsync(
                                        nextIdleStartUtc.AddMinutes(6)));
                            }

                            return MessageBoxResult.No;
                        }
                        finally
                        {
                            promptDepth--;
                        }
                    });

                    try
                    {
                        _controller.BeginIdleForPreview(queueBaseUtc);
                        await _controller.CompleteIdleForPreviewAsync(
                            queueBaseUtc.AddMinutes(6));
                        while (queuedReviews.Any(review => !review.IsCompleted))
                        {
                            await Task.WhenAll(queuedReviews.ToArray());
                        }
                    }
                    finally
                    {
                        _controller.SetIdleReviewPromptForPreview(null);
                    }

                    if (promptTitles.Count != 3 ||
                        promptTitles[0] != "Review idle time" ||
                        promptTitles.Skip(1).Any(title =>
                            title != AppController.RepeatedIdlePromptTitle) ||
                        promptMessages.Any(message =>
                            !message.Contains("6 minutes", StringComparison.Ordinal)) ||
                        maximumPromptDepth != 1 ||
                        await _store.GetEntryExcludedSecondsAsync(runningEntry.Id) != 0)
                    {
                        throw new InvalidOperationException(
                            "Repeated long-idle reviews were lost, stacked, mistitled, or measured incorrectly.");
                    }

                    await _controller.StopTimerAsync();
                    _detailsWindow?.CloseWithoutSaving();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_HISTORY_VIEW"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await _mainWindow.VerifyHistoryViewForPreviewAsync();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_REPORT_VIEW"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await _mainWindow.VerifyReportViewForPreviewAsync();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_HISTORY_OVERLAP"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync(
                        $"Overlap client {Guid.NewGuid():N}",
                        "#766F80");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"Overlap project {Guid.NewGuid():N}",
                        "#FA423E");
                    var task = await _store.AddTaskAsync(project.Id, "Overlap check");
                    var day = new DateTimeOffset(
                        DateTime.UtcNow.Date.AddHours(8),
                        TimeSpan.Zero);
                    await _store.AddManualEntryAsync(
                        project.Id,
                        task.Id,
                        "Outer interval",
                        day,
                        day.AddHours(2).AddSeconds(45));
                    await _store.AddManualEntryAsync(
                        project.Id,
                        task.Id,
                        "Nested interval",
                        day.AddMinutes(30),
                        day.AddHours(1));
                    await _store.AddManualEntryAsync(
                        project.Id,
                        task.Id,
                        "Touching interval",
                        day.AddHours(2),
                        day.AddHours(3));
                    await _mainWindow.RefreshAllAsync();
                    var overlapRows = (await _store.GetEntriesAsync(
                            day.AddMinutes(-1),
                            day.AddHours(4)))
                        .Where(entry => entry.ProjectId == project.Id)
                        .ToArray();
                    var outer = overlapRows.Single(entry =>
                        string.Equals(entry.Description, "Outer interval", StringComparison.Ordinal));
                    var nested = overlapRows.Single(entry =>
                        string.Equals(entry.Description, "Nested interval", StringComparison.Ordinal));
                    var touching = overlapRows.Single(entry =>
                        string.Equals(entry.Description, "Touching interval", StringComparison.Ordinal));
                    _mainWindow.VerifyHistoryOverlapForPreview(
                        [outer.Id, nested.Id],
                        touching.Id);
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_ENTRY_SELECT_ALL"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var dialog = new EntryEditorWindow(_store)
                    {
                        Owner = _mainWindow,
                    };
                    dialog.Show();
                    for (var attempt = 0;
                         attempt < 50 && !dialog.IsReadyForSelectionPreview;
                         attempt++)
                    {
                        await Task.Delay(100);
                    }

                    if (!dialog.IsReadyForSelectionPreview)
                    {
                        throw new InvalidOperationException(
                            "The new-entry editor did not finish loading its default date and time values.");
                    }

                    await dialog.VerifyEditableValuesSelectOnFocusForPreviewAsync();
                    dialog.Close();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_ENTRY_SCROLL"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var dialog = new EntryEditorWindow(_store)
                    {
                        Owner = _mainWindow,
                    };
                    dialog.Show();
                    for (var attempt = 0;
                         attempt < 50 && !dialog.IsReadyForSelectionPreview;
                         attempt++)
                    {
                        await Task.Delay(100);
                    }

                    if (!dialog.IsReadyForSelectionPreview)
                    {
                        throw new InvalidOperationException(
                            "The scrollable time-entry editor did not finish loading.");
                    }

                    dialog.VerifyScrollableLayoutForPreview();
                    dialog.Close();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_MANUAL_ENTRY_INPUT"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync(
                        $"Manual entry client {Guid.NewGuid():N}",
                        "#766F80");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"Manual entry project {Guid.NewGuid():N}",
                        "#0D8F68");
                    var suggestedTask = await _store.AddTaskAsync(
                        project.Id,
                        $"Animation history suggestion {Guid.NewGuid():N}");
                    var taskName = $"Typed manual task {Guid.NewGuid():N}";
                    var dialog = new EntryEditorWindow(_store)
                    {
                        Owner = _mainWindow,
                    };
                    dialog.Show();
                    for (var attempt = 0;
                         attempt < 50 && !dialog.IsReadyForSelectionPreview;
                         attempt++)
                    {
                        await Task.Delay(100);
                    }

                    if (!dialog.IsReadyForSelectionPreview ||
                        dialog.FindName("TaskCombo") is not ComboBox { IsEditable: true } taskCombo ||
                        taskCombo.IsTextSearchEnabled ||
                        !taskCombo.StaysOpenOnEdit)
                    {
                        throw new InvalidOperationException(
                            "The History entry editor did not expose an editable task chooser.");
                    }

                    await dialog.VerifyTaskSearchForPreviewAsync(
                        suggestedTask.Id,
                        suggestedTask.Name);
                    await dialog.SetManualValuesForPreviewAsync(
                        project.Id,
                        taskName,
                        startTime: "1303",
                        endTime: "1312");
                    await dialog.SubmitForPreviewAsync();
                    var result = dialog.Result
                        ?? throw new InvalidOperationException(
                            "Compact manual entry times were not accepted.");
                    var startLocal = result.StartUtc.ToLocalTime();
                    var endLocal = result.EndUtc.ToLocalTime();
                    var savedTask = (await _store.GetTasksAsync(project.Id))
                        .SingleOrDefault(task =>
                            string.Equals(task.Name, taskName, StringComparison.Ordinal));
                    if (result.ProjectId != project.Id ||
                        result.TaskId is not { } resultTaskId ||
                        savedTask?.Id != resultTaskId ||
                        startLocal.Hour != 13 ||
                        startLocal.Minute != 3 ||
                        endLocal.Hour != 13 ||
                        endLocal.Minute != 12)
                    {
                        throw new InvalidOperationException(
                            "The manual entry did not normalize compact times or create its typed task.");
                    }

                    dialog.Close();
                    await _store.AddManualEntryAsync(
                        result.ProjectId,
                        result.TaskId,
                        result.Description,
                        result.StartUtc,
                        result.EndUtc,
                        result.IsPaid);
                    var storedEntry = (await _store.GetEntriesAsync(
                            result.StartUtc.AddMinutes(-1),
                            result.EndUtc.AddMinutes(1)))
                        .SingleOrDefault(entry => entry.TaskId == result.TaskId);
                    if (storedEntry is null)
                    {
                        throw new InvalidOperationException(
                            "The manual entry with its newly created task was not persisted.");
                    }
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_HISTORY_GROUP_DATE"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync(
                        $"History group client {Guid.NewGuid():N}",
                        "#766F80");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"History group project {Guid.NewGuid():N}",
                        "#0D8F68");
                    var clickedDate = DateTime.Today;
                    var localStart = DateTime.SpecifyKind(
                        clickedDate.AddHours(9),
                        DateTimeKind.Local);
                    var startUtc = new DateTimeOffset(localStart).ToUniversalTime();
                    await _store.AddManualEntryAsync(
                        project.Id,
                        taskId: null,
                        "History group date source",
                        startUtc,
                        startUtc.AddHours(1));
                    await _mainWindow.RefreshAllAsync();

                    var dialog = _mainWindow.CreateHistoryGroupEntryEditorForPreview(clickedDate);
                    dialog.Show();
                    for (var attempt = 0;
                         attempt < 50 && !dialog.IsReadyForSelectionPreview;
                         attempt++)
                    {
                        await Task.Delay(100);
                    }

                    if (dialog.StartDateForPreview != clickedDate ||
                        dialog.EndDateForPreview != clickedDate)
                    {
                        throw new InvalidOperationException(
                            "Adding from a History day group did not prefill that group's date.");
                    }

                    var changedDate = clickedDate.AddDays(-1);
                    dialog.SetDatesForPreview(changedDate, changedDate);
                    if (dialog.StartDateForPreview != changedDate ||
                        dialog.EndDateForPreview != changedDate)
                    {
                        throw new InvalidOperationException(
                            "The History group-prefilled dates could not be edited afterward.");
                    }

                    dialog.Close();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_ENTRY_DATE_SYNC"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var dialog = new EntryEditorWindow(_store)
                    {
                        Owner = _mainWindow,
                    };
                    dialog.Show();
                    for (var attempt = 0;
                         attempt < 50 && !dialog.IsReadyForSelectionPreview;
                         attempt++)
                    {
                        await Task.Delay(100);
                    }

                    var changedStart = DateTime.Today.AddDays(-3);
                    dialog.SetEndDateForPreview(DateTime.Today.AddDays(2));
                    dialog.SetStartDateForPreview(changedStart);
                    if (dialog.StartDateForPreview != changedStart ||
                        dialog.EndDateForPreview != changedStart)
                    {
                        throw new InvalidOperationException(
                            "Changing a new entry's start date did not copy it to the end date.");
                    }

                    var independentlyChangedEnd = changedStart.AddDays(2);
                    dialog.SetEndDateForPreview(independentlyChangedEnd);
                    if (dialog.StartDateForPreview != changedStart ||
                        dialog.EndDateForPreview != independentlyChangedEnd)
                    {
                        throw new InvalidOperationException(
                            "The new entry's end date could not be changed independently afterward.");
                    }

                    dialog.SetDatesForPreview(changedStart, changedStart);
                    if (!dialog.ApplyOvernightEndDateForPreview("23:30", "01:15") ||
                        dialog.StartDateForPreview != changedStart ||
                        dialog.EndDateForPreview != changedStart.AddDays(1))
                    {
                        throw new InvalidOperationException(
                            "An overnight new entry did not advance its end date to the next day.");
                    }

                    dialog.SetDatesForPreview(changedStart, changedStart);
                    if (dialog.ApplyOvernightEndDateForPreview("09:00", "17:00") ||
                        dialog.StartDateForPreview != changedStart ||
                        dialog.EndDateForPreview != changedStart)
                    {
                        throw new InvalidOperationException(
                            "A same-day new entry changed its end date even though its end time followed its start time.");
                    }

                    dialog.Close();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_REPORT_TASK_DATE_SORT"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync(
                        $"Report sort client {Guid.NewGuid():N}",
                        "#766F80");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"Report sort project {Guid.NewGuid():N}",
                        "#0D8F68");
                    var olderTask = await _store.AddTaskAsync(project.Id, "Older long task");
                    var newerTask = await _store.AddTaskAsync(project.Id, "Newest short task");
                    var now = DateTimeOffset.UtcNow;
                    var olderStart = now.AddDays(-2);
                    var newerStart = now.AddDays(-1);
                    await _store.AddManualEntryAsync(
                        project.Id,
                        olderTask.Id,
                        null,
                        olderStart,
                        olderStart.AddHours(4));
                    await _store.AddManualEntryAsync(
                        project.Id,
                        newerTask.Id,
                        null,
                        newerStart,
                        newerStart.AddMinutes(10));
                    await _mainWindow.RefreshAllAsync();
                    _mainWindow.VerifyReportTaskDateSortingForPreview(
                        project.Id,
                        newerTask.Id,
                        olderTask.Id);
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_REPORT_SINGLE_SELECTION"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync(
                        $"Report selection client {Guid.NewGuid():N}",
                        "#766F80");
                    var firstProject = await _store.AddProjectAsync(
                        client.Id,
                        $"Report selection first {Guid.NewGuid():N}",
                        "#0D8F68");
                    var secondProject = await _store.AddProjectAsync(
                        client.Id,
                        $"Report selection second {Guid.NewGuid():N}",
                        "#339CFF");
                    var firstTask = await _store.AddTaskAsync(firstProject.Id, "First report selection task");
                    var secondTask = await _store.AddTaskAsync(secondProject.Id, "Second report selection task");
                    var now = DateTimeOffset.UtcNow;
                    await _store.AddManualEntryAsync(
                        firstProject.Id,
                        firstTask.Id,
                        null,
                        now.AddMinutes(-24),
                        now.AddMinutes(-12));
                    await _store.AddManualEntryAsync(
                        secondProject.Id,
                        secondTask.Id,
                        null,
                        now.AddMinutes(-10),
                        now.AddMinutes(-2));
                    await _mainWindow.RefreshAllAsync();
                    _mainWindow.VerifySingleReportObjectSelectionForPreview(
                        firstProject.Id,
                        secondProject.Id);
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_REPORT_CHART_DURATION"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    global::ProjectTimeTracker.Windows.MainWindow
                        .VerifyReportChartDurationFormattingForPreview();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_REPORT_CLIENT_CHART"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var firstClient = await _store.AddClientAsync(
                        $"Client chart A {Guid.NewGuid():N}",
                        "#766F80");
                    var firstProject = await _store.AddProjectAsync(
                        firstClient.Id,
                        $"Client chart project A {Guid.NewGuid():N}",
                        "#339CFF");
                    var firstTask = await _store.AddTaskAsync(
                        firstProject.Id,
                        "Client chart work A");
                    var secondClient = await _store.AddClientAsync(
                        $"Client chart B {Guid.NewGuid():N}",
                        "#687582");
                    var secondProject = await _store.AddProjectAsync(
                        secondClient.Id,
                        $"Client chart project B {Guid.NewGuid():N}",
                        "#40C977");
                    var secondTask = await _store.AddTaskAsync(
                        secondProject.Id,
                        "Client chart work B");
                    var month = TrackingPeriodCalculator.CurrentMonth(
                        _controller.UtcNow,
                        TimeZoneInfo.Local);
                    var firstStartUtc = month.StartUtc.AddHours(2);
                    await _store.AddManualEntryAsync(
                        firstProject.Id,
                        firstTask.Id,
                        "First client chart entry",
                        firstStartUtc,
                        firstStartUtc.AddMinutes(90));
                    var secondStartUtc = firstStartUtc.AddHours(2);
                    await _store.AddManualEntryAsync(
                        secondProject.Id,
                        secondTask.Id,
                        "Second client chart entry",
                        secondStartUtc,
                        secondStartUtc.AddMinutes(30));
                    await _mainWindow.RefreshAllAsync();
                    _mainWindow.VerifyReportClientChartForPreview(
                        new Dictionary<string, long>(StringComparer.Ordinal)
                        {
                            [firstClient.Name] = 90 * 60,
                            [secondClient.Name] = 30 * 60,
                        });
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_POPUP_TASK_SYNC"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = (await _store.GetClientsAsync()).FirstOrDefault()
                        ?? await _store.AddClientAsync("Popup sync client", "#766F80");
                    var project = (await _store.GetProjectsAsync()).FirstOrDefault()
                        ?? await _store.AddProjectAsync(client.Id, "Popup sync project", "#0D8F68");
                    var task = (await _store.GetTasksAsync(project.Id)).FirstOrDefault()
                        ?? await _store.AddTaskAsync(project.Id, "Popup-selected task");
                    var searchableTask = await _store.AddTaskAsync(
                        project.Id,
                        $"Needle popup task {Guid.NewGuid():N}");
                    _controller.NotifyDataChanged();
                    await Task.Delay(250);

                    await _controller.StartTimerAsync(project.Id, TrackingSource.WindowReminder, showDetails: true);
                    for (var attempt = 0; attempt < 50 && _detailsWindow?.IsLoaded != true; attempt++)
                    {
                        await Task.Delay(100);
                    }

                    if (_detailsWindow is not { } detailsWindow)
                    {
                        throw new InvalidOperationException("The automatic details popup did not open.");
                    }

                    var popupTaskCombo = (ComboBox)detailsWindow.FindName("TaskCombo");
                    if (!popupTaskCombo.IsEditable ||
                        popupTaskCombo.IsTextSearchEnabled ||
                        !popupTaskCombo.StaysOpenOnEdit ||
                        detailsWindow.FindName("NewTaskButton") is not null)
                    {
                        throw new InvalidOperationException(
                            "The automatic details popup does not use the editable timer-bar task chooser.");
                    }

                    var popupRipButton = (Button)detailsWindow.FindName("RipButton");
                    var popupStopButton = (Button)detailsWindow.FindName("StopTimerButton");
                    var popupActionsPanel = (StackPanel)detailsWindow.FindName("RunningActionsPanel");
                    var popupStartTimePanel = (StackPanel)detailsWindow.FindName("StartTimePanel");
                    if (popupRipButton.Visibility != Visibility.Collapsed ||
                        popupStopButton.Visibility != Visibility.Collapsed ||
                        popupActionsPanel.Visibility != Visibility.Collapsed ||
                        popupStartTimePanel.Visibility != Visibility.Visible ||
                        popupActionsPanel.Children
                            .OfType<Button>()
                            .Any(button => string.Equals(
                                button.Content as string,
                                "Done",
                                StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            "The automatic start popup exposed running actions or the removed Done button.");
                    }

                    _tray?.SingleLeftClickForPreview();
                    await Task.Delay(System.Windows.Forms.SystemInformation.DoubleClickTime + 250);
                    if (!ReferenceEquals(_detailsWindow, detailsWindow) ||
                        popupRipButton.Visibility != Visibility.Visible ||
                        popupStopButton.Visibility != Visibility.Visible ||
                        popupActionsPanel.Visibility != Visibility.Visible)
                    {
                        throw new InvalidOperationException(
                            "A tray click did not add Rip and Stop timer to the existing running-entry popup.");
                    }

                    popupTaskCombo.SelectedIndex = -1;
                    popupTaskCombo.Text = task.Name[..Math.Min(3, task.Name.Length)];
                    popupTaskCombo.SelectedValue = task.Id;
                    var popupTimerTaskCombo = (ComboBox)_mainWindow.FindName("TimerTaskCombo");
                    for (var attempt = 0; attempt < 50 &&
                         (_controller.RunningEntry?.TaskId != task.Id ||
                          popupTaskCombo.SelectedValue is not Guid popupSelectedTaskId ||
                          popupSelectedTaskId != task.Id ||
                          !string.Equals(popupTaskCombo.Text, task.Name, StringComparison.Ordinal) ||
                          popupTimerTaskCombo.SelectedValue is not Guid selectedTaskId ||
                          selectedTaskId != task.Id ||
                          !string.Equals(popupTimerTaskCombo.Text, task.Name, StringComparison.Ordinal)); attempt++)
                    {
                        await Task.Delay(100);
                    }

                    var persistedEntry = await _store.GetRunningEntryAsync();
                    if (_controller.RunningEntry?.TaskId != task.Id ||
                        persistedEntry?.TaskId != task.Id ||
                        popupTaskCombo.SelectedValue is not Guid popupVisibleTaskId ||
                        popupVisibleTaskId != task.Id ||
                        !string.Equals(popupTaskCombo.Text, task.Name, StringComparison.Ordinal) ||
                        popupTimerTaskCombo.SelectedValue is not Guid visibleTaskId ||
                        visibleTaskId != task.Id ||
                        !string.Equals(popupTimerTaskCombo.Text, task.Name, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Choosing a popup task did not replace typed text and update the top timer bar.");
                    }

                    await detailsWindow.VerifyTaskSearchAfterClearingSelectionForPreviewAsync(
                        searchableTask.Id,
                        searchableTask.Name,
                        "Needle");
                    for (var attempt = 0; attempt < 50 &&
                         (_controller.RunningEntry?.TaskId != searchableTask.Id ||
                          popupTimerTaskCombo.SelectedValue is not Guid searchedVisibleTaskId ||
                          searchedVisibleTaskId != searchableTask.Id ||
                          !string.Equals(
                              popupTimerTaskCombo.Text,
                              searchableTask.Name,
                              StringComparison.Ordinal)); attempt++)
                    {
                        await Task.Delay(100);
                    }

                    persistedEntry = await _store.GetRunningEntryAsync();
                    if (_controller.RunningEntry?.TaskId != searchableTask.Id ||
                        persistedEntry?.TaskId != searchableTask.Id ||
                        popupTimerTaskCombo.SelectedValue is not Guid visibleSearchedTaskId ||
                        visibleSearchedTaskId != searchableTask.Id ||
                        !string.Equals(
                            popupTimerTaskCombo.Text,
                            searchableTask.Name,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Searching after clearing an existing popup task did not autosave or update the timer bar.");
                    }

                    const string typedTaskName = "Popup typed task";
                    await detailsWindow.TypeTaskForPreviewAsync(typedTaskName);
                    var typedTask = (await _store.GetTasksAsync(project.Id))
                        .SingleOrDefault(item => string.Equals(
                            item.Name,
                            typedTaskName,
                            StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException(
                            "Typing a task in the automatic details popup did not create it.");
                    for (var attempt = 0; attempt < 50 &&
                         (_controller.RunningEntry?.TaskId != typedTask.Id ||
                          popupTimerTaskCombo.SelectedValue is not Guid typedVisibleTaskId ||
                          typedVisibleTaskId != typedTask.Id ||
                          !string.Equals(
                              popupTimerTaskCombo.Text,
                              typedTask.Name,
                              StringComparison.Ordinal)); attempt++)
                    {
                        await Task.Delay(100);
                    }

                    persistedEntry = await _store.GetRunningEntryAsync();
                    if (_controller.RunningEntry?.TaskId != typedTask.Id ||
                        persistedEntry?.TaskId != typedTask.Id ||
                        popupTimerTaskCombo.SelectedValue is not Guid visibleTypedTaskId ||
                        visibleTypedTaskId != typedTask.Id ||
                        !string.Equals(
                            popupTimerTaskCombo.Text,
                            typedTask.Name,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The popup-typed task was not persisted and reflected in the top timer bar.");
                    }

                    var popupDescription = (Controls.TagDescriptionEditor)detailsWindow.FindName("DescriptionText");
                    popupDescription.Focus();
                    var popupInputSource = PresentationSource.FromVisual(popupDescription)
                        ?? throw new InvalidOperationException("The tracker details popup has no presentation source.");
                    var popupEnterEvent = new KeyEventArgs(
                        Keyboard.PrimaryDevice,
                        popupInputSource,
                        Environment.TickCount,
                        Key.Enter)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    };
                    popupDescription.RaiseEvent(popupEnterEvent);
                    for (var attempt = 0; attempt < 50 && detailsWindow.IsLoaded; attempt++)
                    {
                        await Task.Delay(100);
                    }

                    if (!popupEnterEvent.Handled || detailsWindow.IsLoaded)
                    {
                        throw new InvalidOperationException("Pressing Enter did not apply and close the tracker details popup.");
                    }

                    await _controller.StopTimerAsync();
                    _detailsWindow?.Close();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_RUNNING_START_EDIT"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = (await _store.GetClientsAsync()).FirstOrDefault()
                        ?? await _store.AddClientAsync("Start edit client", "#766F80");
                    var project = (await _store.GetProjectsAsync()).FirstOrDefault()
                        ?? await _store.AddProjectAsync(client.Id, "Start edit project", "#0D8F68");
                    await _controller.StartTimerAsync(
                        project.Id,
                        TrackingSource.Manual,
                        showDetails: true);
                    for (var attempt = 0;
                         attempt < 50 && _detailsWindow?.IsLoaded != true;
                         attempt++)
                    {
                        await Task.Delay(100);
                    }

                    var detailsWindow = _detailsWindow
                        ?? throw new InvalidOperationException(
                            "The running-entry popup did not open for the Start-time check.");
                    if (detailsWindow.StartTimeVisibilityForPreview != Visibility.Visible ||
                        _mainWindow.TimerStartTimeVisibilityForPreview != Visibility.Visible)
                    {
                        throw new InvalidOperationException(
                            "The editable Start time was not visible in both running-timer editors.");
                    }

                    var startBeforePopupEdit = _controller.RunningEntry?.StartUtc
                        ?? throw new InvalidOperationException(
                            "The Start-time check did not have a running timer.");
                    var popupRequestedLocal = startBeforePopupEdit
                        .ToLocalTime()
                        .AddMinutes(-12);
                    var popupUpdatedStart = await detailsWindow
                        .SetStartTimeForPreviewAsync(
                            TimeOfDayText.Format(popupRequestedLocal.TimeOfDay));
                    var popupPersistedEntry = await _store.GetRunningEntryAsync();
                    if (popupUpdatedStart is not { } popupStart ||
                        popupStart >= startBeforePopupEdit ||
                        popupStart.ToLocalTime().Hour != popupRequestedLocal.Hour ||
                        popupStart.ToLocalTime().Minute != popupRequestedLocal.Minute ||
                        _controller.RunningEntry?.StartUtc != popupStart ||
                        popupPersistedEntry?.StartUtc != popupStart ||
                        !string.Equals(
                            _mainWindow.TimerStartTimeTextForPreview,
                            TimeOfDayText.Format(popupStart.ToLocalTime().TimeOfDay),
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Editing Start in the popup did not update the timer, database, and main timer bar.");
                    }

                    var mainRequestedLocal = popupStart.ToLocalTime().AddMinutes(-3);
                    var mainUpdatedStart = await _mainWindow
                        .SetTimerStartTimeForPreviewAsync(
                            TimeOfDayText.Format(mainRequestedLocal.TimeOfDay));
                    var mainPersistedEntry = await _store.GetRunningEntryAsync();
                    if (mainUpdatedStart is not { } mainStart ||
                        mainStart >= popupStart ||
                        mainStart.ToLocalTime().Hour != mainRequestedLocal.Hour ||
                        mainStart.ToLocalTime().Minute != mainRequestedLocal.Minute ||
                        _controller.RunningEntry?.StartUtc != mainStart ||
                        mainPersistedEntry?.StartUtc != mainStart ||
                        !string.Equals(
                            detailsWindow.StartTimeTextForPreview,
                            TimeOfDayText.Format(mainStart.ToLocalTime().TimeOfDay),
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Editing Start in the main timer bar did not update the timer, database, and open popup.");
                    }

                    detailsWindow.CloseWithoutSaving();
                    await _controller.StopTimerAsync();
                    _detailsWindow?.CloseWithoutSaving();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_TRAY_START"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = (await _store.GetClientsAsync()).FirstOrDefault()
                        ?? await _store.AddClientAsync("Tray start client", "#766F80");
                    var project = (await _store.GetProjectsAsync()).FirstOrDefault()
                        ?? await _store.AddProjectAsync(client.Id, "Tray start project", "#0D8F68");
                    await RefreshTrayProjectsAsync();

                    _tray?.StartUnassignedForPreview();
                    for (var attempt = 0; attempt < 50 &&
                         (_controller.RunningEntry?.ProjectId != SystemEntityIds.UnassignedProjectId ||
                          _detailsWindow?.IsLoaded != true); attempt++)
                    {
                        await Task.Delay(100);
                    }

                    if (_controller.RunningEntry is not { IsRunning: true } unassignedEntry ||
                        unassignedEntry.ProjectId != SystemEntityIds.UnassignedProjectId ||
                        _detailsWindow is not { } unassignedDetails ||
                        ((FrameworkElement)unassignedDetails.FindName("ProjectChooserPanel")).Visibility != Visibility.Visible ||
                        ((ComboBox)unassignedDetails.FindName("ProjectCombo")).SelectedValue is not null)
                    {
                        throw new InvalidOperationException(
                            "The tray's primary Start timer action did not begin an unassigned timer with a project chooser.");
                    }

                    const string deferredTaskName = "Deferred tray task";
                    await unassignedDetails.TypeTaskForPreviewAsync(deferredTaskName);
                    if (_controller.RunningEntry?.ProjectId != SystemEntityIds.UnassignedProjectId ||
                        _controller.RunningEntry.TaskId is null ||
                        !_controller.RunningEntry.DetailsPending)
                    {
                        throw new InvalidOperationException(
                            "A task typed before choosing the tray-start project was not preserved as pending.");
                    }

                    await unassignedDetails.SelectProjectForPreviewAsync(project.Id);
                    var assignedTask = (await _store.GetTasksAsync(project.Id))
                        .SingleOrDefault(task => string.Equals(
                            task.Name,
                            deferredTaskName,
                            StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException(
                            "The deferred tray task was not created in the selected project.");
                    for (var attempt = 0; attempt < 50 &&
                         (_controller.RunningEntry?.ProjectId != project.Id ||
                          _controller.RunningEntry.TaskId != assignedTask.Id); attempt++)
                    {
                        await Task.Delay(100);
                    }

                    var persistedAssignedEntry = await _store.GetRunningEntryAsync();
                    if (_controller.RunningEntry?.ProjectId != project.Id ||
                        _controller.RunningEntry.TaskId != assignedTask.Id ||
                        persistedAssignedEntry?.ProjectId != project.Id ||
                        persistedAssignedEntry.TaskId != assignedTask.Id ||
                        persistedAssignedEntry.DetailsPending)
                    {
                        throw new InvalidOperationException(
                            "Choosing the popup project did not reassign the already-running tray timer.");
                    }

                    unassignedDetails.Close();
                    await _controller.StopTimerAsync();
                    _detailsWindow?.Close();

                    if (_tray?.StartProjectForPreview(project.Id) != true)
                    {
                        throw new InvalidOperationException("The tray Start for project submenu did not contain the project.");
                    }

                    for (var attempt = 0; attempt < 50 &&
                         (_controller.RunningEntry?.ProjectId != project.Id || _detailsWindow?.IsLoaded != true); attempt++)
                    {
                        await Task.Delay(100);
                    }

                    var persistedEntry = await _store.GetRunningEntryAsync();
                    if (_controller.RunningEntry is not { IsRunning: true } runningEntry ||
                        runningEntry.ProjectId != project.Id ||
                        persistedEntry is not { IsRunning: true } ||
                        persistedEntry.ProjectId != project.Id ||
                        _detailsWindow?.IsLoaded != true)
                    {
                        throw new InvalidOperationException("The tray project action did not start the timer before opening details.");
                    }

                    _detailsWindow.Close();
                    await _controller.StopTimerAsync();
                    _detailsWindow?.Close();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_TRAY_CLICKS"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = (await _store.GetClientsAsync()).FirstOrDefault()
                        ?? await _store.AddClientAsync("Tray click client", "#766F80");
                    var project = (await _store.GetProjectsAsync()).FirstOrDefault()
                        ?? await _store.AddProjectAsync(client.Id, "Tray click project", "#0D8F68");
                    var task = (await _store.GetTasksAsync(project.Id)).FirstOrDefault()
                        ?? await _store.AddTaskAsync(project.Id, "Tray click task");
                    const string description = "Remembered tray-click details";
                    await _controller.StartTimerAsync(project.Id, TrackingSource.Manual, showDetails: false);
                    await _controller.SaveRunningDetailsAsync(task.Id, description);
                    var originalEntryId = _controller.RunningEntry?.Id
                        ?? throw new InvalidOperationException("The tray rip test did not start a timer.");
                    var expectedTooltipPrefix = $"{task.Name} · {project.Name} — ";
                    for (var attempt = 0; attempt < 50 &&
                         (_tray is null ||
                          !_tray.CurrentTooltipForPreview.StartsWith(
                              expectedTooltipPrefix,
                              StringComparison.Ordinal)); attempt++)
                    {
                        await Task.Delay(100);
                    }

                    if (_tray is null ||
                        !_tray.CurrentTooltipForPreview.StartsWith(
                            expectedTooltipPrefix,
                            StringComparison.Ordinal) ||
                        _tray.CurrentTooltipForPreview.Contains(
                            client.Name,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"The tray tooltip was not task-first and project-only. Actual: '{_tray?.CurrentTooltipForPreview}'.");
                    }

                    _tray?.SingleLeftClickForPreview();
                    for (var attempt = 0; attempt < 50 && _detailsWindow?.IsLoaded != true; attempt++)
                    {
                        await Task.Delay(100);
                    }

                    if (_detailsWindow is not { } trayDetailsWindow ||
                        ((ComboBox)trayDetailsWindow.FindName("TaskCombo")).SelectedValue is not Guid selectedTaskId ||
                        selectedTaskId != task.Id ||
                        !string.Equals(
                            ((Controls.TagDescriptionEditor)trayDetailsWindow.FindName("DescriptionText")).Text,
                            description,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "A tray single-click did not reopen the running entry with its remembered task and description.");
                    }

                    var trayRipButton = (Button)trayDetailsWindow.FindName("RipButton");
                    var trayStopButton = (Button)trayDetailsWindow.FindName("StopTimerButton");
                    var trayActionsPanel = (StackPanel)trayDetailsWindow.FindName("RunningActionsPanel");
                    var trayStartTimePanel = (StackPanel)trayDetailsWindow.FindName("StartTimePanel");
                    if (trayRipButton.Visibility != Visibility.Visible ||
                        trayStopButton.Visibility != Visibility.Visible ||
                        trayActionsPanel.Visibility != Visibility.Visible ||
                        trayStartTimePanel.Visibility != Visibility.Visible ||
                        _mainWindow.TimerStartTimeVisibilityForPreview != Visibility.Visible ||
                        trayActionsPanel.Children
                            .OfType<Button>()
                            .Any(button => string.Equals(
                                button.Content as string,
                                "Done",
                                StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            "The running-entry tray popup did not expose Rip and Stop without the removed Done button.");
                    }

                    const string updatedBeforeRip = "Updated before rip #animation";
                    ((Controls.TagDescriptionEditor)trayDetailsWindow.FindName("DescriptionText")).Text = updatedBeforeRip;
                    await trayDetailsWindow.RipForPreviewAsync();

                    var rippedEntry = _controller.RunningEntry;
                    var persistedRippedEntry = await _store.GetRunningEntryAsync();
                    if (rippedEntry is null ||
                        rippedEntry.Id == originalEntryId ||
                        trayDetailsWindow.EntryId != rippedEntry.Id ||
                        rippedEntry.TaskId != task.Id ||
                        !string.Equals(rippedEntry.Description, updatedBeforeRip, StringComparison.Ordinal) ||
                        persistedRippedEntry?.Id != rippedEntry.Id ||
                        !string.Equals(persistedRippedEntry.Description, updatedBeforeRip, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Rip did not start a copied entry or retarget the popup to the new running entry.");
                    }

                    const string changedAfterRip = "Changed only on the new segment #rigging";
                    ((Controls.TagDescriptionEditor)trayDetailsWindow.FindName("DescriptionText")).Text = changedAfterRip;
                    await trayDetailsWindow.PersistForPreviewAsync();
                    persistedRippedEntry = await _store.GetRunningEntryAsync();
                    if (_controller.RunningEntry?.Id != rippedEntry.Id ||
                        !string.Equals(_controller.RunningEntry.Description, changedAfterRip, StringComparison.Ordinal) ||
                        !string.Equals(persistedRippedEntry?.Description, changedAfterRip, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Edits after Rip were not confined to the new running entry.");
                    }

                    trayDetailsWindow.CloseWithoutSaving();
                    _mainWindow.Hide();
                    _tray?.SingleLeftClickForPreview();
                    _tray?.DoubleLeftClickForPreview();
                    await Task.Delay(System.Windows.Forms.SystemInformation.DoubleClickTime + 250);
                    if (!_mainWindow.IsVisible || _detailsWindow?.IsLoaded == true)
                    {
                        throw new InvalidOperationException(
                            "A tray double-click did not cancel the pending entry popup and open the main app.");
                    }

                    _tray?.SingleLeftClickForPreview();
                    for (var attempt = 0; attempt < 50 && _detailsWindow?.IsLoaded != true; attempt++)
                    {
                        await Task.Delay(100);
                    }

                    var stopDetailsWindow = _detailsWindow
                        ?? throw new InvalidOperationException(
                            "The tray popup did not reopen for the Stop timer check.");
                    if (((Button)stopDetailsWindow.FindName("StopTimerButton")).Visibility != Visibility.Visible)
                    {
                        throw new InvalidOperationException(
                            "The reopened running-entry tray popup did not show Stop timer.");
                    }

                    await stopDetailsWindow.StopForPreviewAsync();
                    for (var attempt = 0; attempt < 50 && _controller.RunningEntry is not null; attempt++)
                    {
                        await Task.Delay(100);
                    }

                    if (_controller.RunningEntry is not null)
                    {
                        throw new InvalidOperationException(
                            "Stop timer in the running-entry tray popup did not stop tracking.");
                    }

                    _detailsWindow?.CloseWithoutSaving();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_RUNNING_TASK_ENTER"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = (await _store.GetClientsAsync()).FirstOrDefault()
                        ?? await _store.AddClientAsync("Running task client", "#766F80");
                    var project = (await _store.GetProjectsAsync()).FirstOrDefault()
                        ?? await _store.AddProjectAsync(client.Id, "Running task project", "#0D8F68");
                    _controller.NotifyDataChanged();
                    await Task.Delay(300);

                    var projectCombo = (ComboBox)_mainWindow.FindName("TimerProjectCombo");
                    var runningTaskCombo = (ComboBox)_mainWindow.FindName("TimerTaskCombo");
                    var runningStartStopButton = (Button)_mainWindow.FindName("StartStopButton");
                    projectCombo.SelectedValue = project.Id;
                    runningTaskCombo.SelectedIndex = -1;
                    runningTaskCombo.Text = string.Empty;
                    runningStartStopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (var attempt = 0; attempt < 50 && _controller.RunningEntry is null; attempt++)
                    {
                        await Task.Delay(100);
                    }

                    var originalEntry = _controller.RunningEntry
                        ?? throw new InvalidOperationException("The timer bar did not start a timer for the Enter-key test.");
                    const string enteredTaskName = "Enter-committed task";
                    runningTaskCombo.SelectedIndex = -1;
                    runningTaskCombo.Text = enteredTaskName;
                    var inputSource = PresentationSource.FromVisual(runningTaskCombo)
                        ?? throw new InvalidOperationException("The timer task field has no presentation source.");
                    var enterEvent = new KeyEventArgs(
                        Keyboard.PrimaryDevice,
                        inputSource,
                        Environment.TickCount,
                        Key.Enter)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    };
                    runningTaskCombo.RaiseEvent(enterEvent);

                    for (var attempt = 0; attempt < 50 &&
                         (_controller.RunningEntry?.TaskId is null ||
                          !string.Equals(runningTaskCombo.Text, enteredTaskName, StringComparison.Ordinal)); attempt++)
                    {
                        await Task.Delay(100);
                    }

                    var updatedEntry = _controller.RunningEntry;
                    var persistedEntry = await _store.GetRunningEntryAsync();
                    var savedTasks = await _store.GetTasksAsync(project.Id);
                    var savedTask = savedTasks.FirstOrDefault(task =>
                        string.Equals(task.Name, enteredTaskName, StringComparison.Ordinal));
                    if (!enterEvent.Handled ||
                        savedTask is null ||
                        updatedEntry?.Id != originalEntry.Id ||
                        updatedEntry.StartUtc != originalEntry.StartUtc ||
                        updatedEntry.TaskId != savedTask.Id ||
                        persistedEntry?.TaskId != savedTask.Id ||
                        runningTaskCombo.SelectedValue is not Guid visibleTaskId ||
                        visibleTaskId != savedTask.Id ||
                        !string.Equals(runningTaskCombo.Text, enteredTaskName, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Pressing Enter did not update the current timer task in place. " +
                            $"Handled={enterEvent.Handled}; SavedTask={savedTask?.Id}; " +
                            $"Original={originalEntry.Id}/{originalEntry.StartUtc:O}; " +
                            $"Updated={updatedEntry?.Id}/{updatedEntry?.StartUtc:O}/{updatedEntry?.TaskId}; " +
                            $"Persisted={persistedEntry?.Id}/{persistedEntry?.TaskId}; " +
                            $"Selected={runningTaskCombo.SelectedValue}; Text='{runningTaskCombo.Text}'.");
                    }

                    await _controller.StopTimerAsync();
                    for (var attempt = 0;
                         attempt < 50 &&
                         (runningTaskCombo.SelectedItem is not null ||
                          !string.IsNullOrEmpty(runningTaskCombo.Text));
                         attempt++)
                    {
                        await Task.Delay(20);
                    }

                    if (runningTaskCombo.SelectedItem is not null ||
                        !string.IsNullOrEmpty(runningTaskCombo.Text))
                    {
                        throw new InvalidOperationException(
                            "Stopping tracking did not clear the task field in the timer bar.");
                    }

                    _detailsWindow?.Close();

                    const string enterStartedTaskName = "Enter-started task";
                    projectCombo.SelectedValue = project.Id;
                    runningTaskCombo.SelectedIndex = -1;
                    runningTaskCombo.Text = enterStartedTaskName;
                    var startInputSource = PresentationSource.FromVisual(runningTaskCombo)
                        ?? throw new InvalidOperationException("The idle timer task field has no presentation source.");
                    var startEnterEvent = new KeyEventArgs(
                        Keyboard.PrimaryDevice,
                        startInputSource,
                        Environment.TickCount,
                        Key.Enter)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    };
                    runningTaskCombo.RaiseEvent(startEnterEvent);

                    for (var attempt = 0; attempt < 50 &&
                         (_controller.RunningEntry?.TaskId is null ||
                          !string.Equals(runningTaskCombo.Text, enterStartedTaskName, StringComparison.Ordinal)); attempt++)
                    {
                        await Task.Delay(100);
                    }

                    var enterStartedEntry = _controller.RunningEntry;
                    var enterStartedPersistedEntry = await _store.GetRunningEntryAsync();
                    var enterStartedTask = (await _store.GetTasksAsync(project.Id)).FirstOrDefault(task =>
                        string.Equals(task.Name, enterStartedTaskName, StringComparison.Ordinal));
                    if (!startEnterEvent.Handled ||
                        enterStartedTask is null ||
                        enterStartedEntry is not { IsRunning: true } ||
                        enterStartedEntry.ProjectId != project.Id ||
                        enterStartedEntry.TaskId != enterStartedTask.Id ||
                        enterStartedPersistedEntry?.TaskId != enterStartedTask.Id)
                    {
                        throw new InvalidOperationException(
                            $"Pressing Enter with a project and task did not start tracking. " +
                            $"Handled={startEnterEvent.Handled}; SavedTask={enterStartedTask?.Id}; " +
                            $"Running={enterStartedEntry?.Id}/{enterStartedEntry?.ProjectId}/{enterStartedEntry?.TaskId}; " +
                            $"Persisted={enterStartedPersistedEntry?.Id}/{enterStartedPersistedEntry?.TaskId}.");
                    }

                    await _controller.StopTimerAsync();
                    _detailsWindow?.Close();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_NEW_PROJECT_COLOR"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = (await _store.GetClientsAsync()).FirstOrDefault()
                        ?? await _store.AddClientAsync("New project color client", "#766F80");
                    const string projectName = "Color-configured project";
                    const string projectColor = "#3A7BD5";
                    var dialog = new NewProjectWindow([client], client.Id)
                    {
                        Owner = _mainWindow,
                    };
                    dialog.Show();
                    await Task.Delay(150);
                    dialog.SetProjectForPreview(client.Id, projectName, projectColor);
                    dialog.SubmitForPreview();
                    if (dialog.Result is not { } result)
                    {
                        throw new InvalidOperationException("The New Project dialog did not return its selected color.");
                    }

                    dialog.Close();
                    await _store.AddProjectAsync(result.ClientId, result.ProjectName, result.Color);
                    var createdProject = (await _store.GetProjectsAsync())
                        .FirstOrDefault(project => string.Equals(project.Name, projectName, StringComparison.Ordinal));
                    if (createdProject is null ||
                        !string.Equals(createdProject.Color, projectColor, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("The project was not created with the color selected in the popup.");
                    }
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_PROJECT_CLIENT_CHANGE"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var sourceClient = await _store.AddClientAsync("Original project client", "#766F80");
                    var destinationClient = await _store.AddClientAsync("Updated project client", "#3A7BD5");
                    var project = await _store.AddProjectAsync(sourceClient.Id, "Reassigned project", "#0D8F68");
                    var dialog = new ProjectSettingsWindow(project, sourceClient.Name, [sourceClient, destinationClient])
                    {
                        Owner = _mainWindow,
                    };
                    dialog.Show();
                    await Task.Delay(150);
                    dialog.SetClientForPreview(destinationClient.Id);
                    dialog.SetTargetsForPreview(8, 40, 160);
                    dialog.SetCarryOverTargetDebtForPreview(true);
                    dialog.SubmitForPreview();
                    if (dialog.Result is not { } result)
                    {
                        throw new InvalidOperationException("The Project Settings dialog did not return the selected client.");
                    }

                    dialog.Close();
                    await _store.UpdateProjectSettingsAsync(
                        project.Id,
                        result.ClientId,
                        result.DailyTargetHours,
                        result.WeeklyTargetHours,
                        result.MonthlyTargetHours,
                        result.HourlyRate,
                        result.Currency,
                        result.CarryOverTargetDebtEnabled);
                    var updatedProject = (await _store.GetProjectsAsync()).First(item => item.Id == project.Id);
                    if (updatedProject.ClientId != destinationClient.Id ||
                        !updatedProject.CarryOverTargetDebtEnabled)
                    {
                        throw new InvalidOperationException("The edited project did not retain its client and target-debt settings.");
                    }
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_MINIMUM_DURATION"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = (await _store.GetClientsAsync()).FirstOrDefault()
                        ?? await _store.AddClientAsync("Minimum duration client", "#766F80");
                    var project = (await _store.GetProjectsAsync()).FirstOrDefault()
                        ?? await _store.AddProjectAsync(client.Id, "Minimum duration project", "#0D8F68");
                    var start = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

                    await _store.StartTimerAsync(project.Id, TrackingSource.Manual, start);
                    if (await _store.StopRunningTimerAsync(start.AddSeconds(59)) is not null)
                    {
                        throw new InvalidOperationException("A 59-second timer entry was not removed.");
                    }

                    var retained = await _store.StartTimerAsync(project.Id, TrackingSource.Manual, start.AddMinutes(5));
                    if (await _store.StopRunningTimerAsync(start.AddMinutes(6)) is null)
                    {
                        throw new InvalidOperationException("An exactly one-minute timer entry was removed.");
                    }

                    await _store.DeleteTimeEntryAsync(retained.Id);
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_IDLE_EDIT"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = (await _store.GetClientsAsync()).FirstOrDefault()
                        ?? await _store.AddClientAsync("Idle edit client", "#766F80");
                    var project = (await _store.GetProjectsAsync()).FirstOrDefault()
                        ?? await _store.AddProjectAsync(client.Id, "Idle edit project", "#0D8F68");
                    var end = DateTimeOffset.UtcNow.AddMinutes(-5);
                    var start = end.AddHours(-2);
                    await _store.AddManualEntryAsync(project.Id, null, "Idle edit preview", start, end);
                    var entry = (await _store.GetEntriesAsync(start.AddMinutes(-1), end.AddMinutes(1)))
                        .Single(item => item.Description == "Idle edit preview");
                    await _store.AddExclusionAsync(
                        entry.Id,
                        end.AddMinutes(-15),
                        end,
                        "Idle or locked");
                    entry = (await _store.GetEntriesAsync(start.AddMinutes(-1), end.AddMinutes(1)))
                        .Single(item => item.Id == entry.Id);
                    var row = new ViewModels.TimeEntryRow(entry, end, await _store.GetTagsAsync());
                    if (!string.Equals(row.Duration, "01:45:00", StringComparison.Ordinal) ||
                        !string.Equals(row.ExcludedDuration, "− 00:15:00 idle", StringComparison.Ordinal) ||
                        !row.HasExcludedTime)
                    {
                        throw new InvalidOperationException(
                            "History did not expose the net duration and its subtracted idle time.");
                    }

                    var dialog = new EntryEditorWindow(_store, entry)
                    {
                        Owner = _mainWindow,
                    };
                    dialog.Show();
                    for (var attempt = 0; attempt < 50 &&
                         !string.Equals(dialog.ExcludedTimeForPreview, "00:15:00", StringComparison.Ordinal); attempt++)
                    {
                        await Task.Delay(100);
                    }

                    if (!string.Equals(dialog.ExcludedTimeForPreview, "00:15:00", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The entry editor did not load the existing subtracted idle time.");
                    }

                    dialog.SetExcludedTimeForPreview("00:30:00");
                    await dialog.SubmitForPreviewAsync();
                    if (dialog.Result is not { ExcludedSeconds: 1_800 } result)
                    {
                        throw new InvalidOperationException(
                            "The entry editor did not return the edited idle duration.");
                    }

                    dialog.Close();
                    await _store.UpdateTimeEntryAsync(
                        entry.Id,
                        result.ProjectId,
                        result.TaskId,
                        result.Description,
                        result.StartUtc,
                        result.EndUtc,
                        result.IsPaid,
                        result.ExcludedSeconds);
                    var updated = (await _store.GetEntriesAsync(start.AddMinutes(-1), end.AddMinutes(1)))
                        .Single(item => item.Id == entry.Id);
                    if (updated.ExcludedSeconds != 1_800 ||
                        updated.NetDurationSeconds(end) != 5_400)
                    {
                        throw new InvalidOperationException(
                            "The edited idle duration was not persisted into the history log.");
                    }

                    _controller.NotifyDataChanged();
                    await Task.Delay(500);
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_REMOVED_FILTER_OPTIONS"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var activeClient = await _store.AddClientAsync("Active filter client", "#52677A");
                    var activeProject = await _store.AddProjectAsync(activeClient.Id, "Active filter project", "#339CFF");
                    _ = await _store.AddTaskAsync(activeProject.Id, "Active filter task");
                    var removedTask = await _store.AddTaskAsync(activeProject.Id, "Removed filter task");

                    var removedProject = await _store.AddProjectAsync(activeClient.Id, "Removed filter project", "#FA423E");
                    var removedProjectTask = await _store.AddTaskAsync(removedProject.Id, "Removed project task");
                    var removedProjectRule = (await _store.GetRulesAsync(removedProject.Id)).Single();

                    var removedClient = await _store.AddClientAsync("Removed filter client", "#725B7A");
                    var removedClientProject = await _store.AddProjectAsync(removedClient.Id, "Removed client project", "#FB6A22");
                    var removedClientTask = await _store.AddTaskAsync(removedClientProject.Id, "Removed client task");
                    var removedClientRule = (await _store.GetRulesAsync(removedClientProject.Id)).Single();

                    var now = DateTimeOffset.UtcNow;
                    await _store.AddManualEntryAsync(
                        activeProject.Id,
                        removedTask.Id,
                        "Retained removed-task history",
                        now.AddMinutes(-18),
                        now.AddMinutes(-16));
                    await _store.AddManualEntryAsync(
                        removedProject.Id,
                        removedProjectTask.Id,
                        "Retained removed-project history",
                        now.AddMinutes(-14),
                        now.AddMinutes(-12));
                    await _store.AddManualEntryAsync(
                        removedClientProject.Id,
                        removedClientTask.Id,
                        "Retained removed-client history",
                        now.AddMinutes(-10),
                        now.AddMinutes(-8));

                    const string removedTagName = "removed-filter-tag";
                    await _store.AddManualEntryAsync(
                        activeProject.Id,
                        null,
                        $"Tag removal #{removedTagName}",
                        now.AddMinutes(-6),
                        now.AddMinutes(-4));
                    var removedTag = (await _store.GetTagsAsync())
                        .Single(tag => string.Equals(tag.Name, removedTagName, StringComparison.OrdinalIgnoreCase));
                    await _store.DeleteTagAsync(removedTag.Id);

                    await _store.ArchiveTaskAsync(removedTask.Id);
                    await _store.ArchiveProjectAsync(removedProject.Id);
                    await _store.ArchiveClientAsync(removedClient.Id);
                    await _mainWindow.RefreshAllAsync();
                    _mainWindow.VerifyRemovedFilterOptionsForPreview(
                        [removedClient.Id],
                        [removedProject.Id, removedClientProject.Id],
                        [removedTask.Id, removedProjectTask.Id, removedClientTask.Id],
                        [removedProjectRule.Id, removedClientRule.Id],
                        [removedTagName]);
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_RULE_GROUPING"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync("Rule grouping client", "#687582");
                    var firstProject = await _store.AddProjectAsync(client.Id, "Grouped alpha", "#339CFF");
                    var secondProject = await _store.AddProjectAsync(client.Id, "Grouped beta", "#40C977");
                    _ = await _store.AddRuleAsync(firstProject.Id, "Alpha secondary window", "alpha.exe");
                    _ = await _store.AddRuleAsync(secondProject.Id, "Beta secondary window", "beta.exe");
                    await _mainWindow.RefreshAllAsync();
                    _mainWindow.VerifyRuleGroupingForPreview(firstProject.Id);
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_TASK_PROJECT_FILTER"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync("Task filter client", "#687582");
                    var firstProject = await _store.AddProjectAsync(client.Id, "Task filter alpha", "#339CFF");
                    var secondProject = await _store.AddProjectAsync(client.Id, "Task filter beta", "#40C977");
                    var expectedTask = await _store.AddTaskAsync(firstProject.Id, "Alpha filtered task");
                    _ = await _store.AddTaskAsync(firstProject.Id, "Alpha second task");
                    _ = await _store.AddTaskAsync(secondProject.Id, "Beta hidden task");
                    await _mainWindow.RefreshAllAsync();
                    _mainWindow.VerifyTaskProjectFilterForPreview(firstProject.Id, expectedTask.Id);
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_SOFTWARE"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync("Software smoke client", "#687582");
                    var project = await _store.AddProjectAsync(client.Id, "Software smoke project", "#339CFF");
                    var alternateProject = await _store.AddProjectAsync(
                        client.Id,
                        "Software alternate project",
                        "#40C977");
                    var end = DateTimeOffset.UtcNow;
                    await _store.AddManualEntryAsync(
                        project.Id,
                        null,
                        "Software capture verification #animation #rigging",
                        end.AddHours(-1),
                        end);
                    var entry = (await _store.GetEntriesAsync(end.AddHours(-2), end.AddMinutes(1)))
                        .Single(item => string.Equals(
                            item.Description,
                            "Software capture verification #animation #rigging",
                            StringComparison.Ordinal));
                    var blender = await _store.AddSoftwareAsync(
                        "blender.exe",
                        "blender",
                        project.Id,
                        isExcluded: false,
                        tagIds: []);
                    _ = await _store.AddSoftwareAsync(
                        "maya",
                        "maya",
                        project.Id,
                        isExcluded: false,
                        tagIds: []);
                    _ = await _store.RecordSoftwareUsageAsync(entry.Id, "blender.exe");
                    _ = await _store.RecordSoftwareUsageAsync(entry.Id, "maya");
                    var correlatedTags = (await _store.GetTagsAsync())
                        .Where(tag => tag.Name is "animation" or "rigging")
                        .ToArray();
                    var globalSoftwareTag = await _store.AddTagAsync(
                        "global-pause",
                        "#B57CFF",
                        projectId: null);
                    await _store.UpdateSoftwareAsync(
                        blender.Id,
                        project.Id,
                        "Blender 4.5",
                        isExcluded: false,
                        tagIds: correlatedTags.Select(tag => tag.Id).ToArray());
                    var excludedSoftware = await _store.AddSoftwareAsync(
                        "discord.exe",
                        "Discord",
                        project.Id,
                        isExcluded: true,
                        tagIds: []);
                    var globalSoftware = await _store.AddSoftwareAsync(
                        "global-pause.exe",
                        "Global pause",
                        SystemEntityIds.GlobalSoftwareScopeId,
                        isExcluded: true,
                        tagIds: [globalSoftwareTag.Id]);
                    await _controller.ReloadSoftwareSettingsAsync();
                    await _mainWindow.RefreshAllAsync();
                    if (!_controller.IsSoftwareExcludedForPreview(project.Id, "global-pause") ||
                        !_controller.IsSoftwareExcludedForPreview(alternateProject.Id, "GLOBAL-PAUSE.EXE") ||
                        (await _store.GetSoftwareTagsByProcessAsync(project.Id, "global-pause")).Single().Id != globalSoftwareTag.Id ||
                        (await _store.GetSoftwareTagsByProcessAsync(alternateProject.Id, "global-pause.exe")).Single().Id != globalSoftwareTag.Id)
                    {
                        throw new InvalidOperationException(
                            "Global software did not share exclusion and correlated tags across projects.");
                    }

                    var capturedActivity = new WindowActivity(
                        77,
                        "Captured software window",
                        "captured-tool",
                        DateTimeOffset.UtcNow);
                    var addSoftwareDialog = new SoftwareSettingsWindow(
                        setting: null,
                        await _store.GetTagsAsync(),
                        await _store.GetProjectOptionsAsync(),
                        project.Id,
                        () => capturedActivity)
                    {
                        Owner = _mainWindow,
                    };
                    addSoftwareDialog.Show();
                    await Task.Delay(100);
                    await addSoftwareDialog.CaptureActiveProcessForPreviewAsync();
                    if (!addSoftwareDialog.IsCaptureProcessAvailableForPreview ||
                        !addSoftwareDialog.HasGlobalScopeOptionForPreview ||
                        !string.Equals(
                            addSoftwareDialog.CapturedProcessForPreview,
                            capturedActivity.ProcessName,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            addSoftwareDialog.CapturedLabelForPreview,
                            capturedActivity.ProcessName,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Add Software did not expose global scope or capture the active process into its editable fields.");
                    }

                    addSoftwareDialog.Close();
                    _mainWindow.VerifySoftwareForPreview(
                        entry.Id,
                        blender.Id,
                        project.Id,
                        "Blender 4.5",
                        ["animation", "rigging"]);
                    _mainWindow.VerifySoftwareForPreview(
                        entry.Id,
                        excludedSoftware.Id,
                        project.Id,
                        "Discord",
                        expectedExcluded: true);
                    _mainWindow.VerifySoftwareForPreview(
                        entry.Id,
                        globalSoftware.Id,
                        SystemEntityIds.GlobalSoftwareScopeId,
                        "Global pause",
                        ["global-pause"],
                        expectedExcluded: true);

                    var excludedSetting = (await _store.GetProjectSoftwareAsync(project.Id))
                        .Single(setting => setting.Software.Id == excludedSoftware.Id);
                    var excludedDialog = new SoftwareSettingsWindow(
                        excludedSetting,
                        await _store.GetTagsAsync(),
                        await _store.GetProjectOptionsAsync())
                    {
                        Owner = _mainWindow,
                    };
                    excludedDialog.Show();
                    await Task.Delay(100);
                    excludedDialog.VerifyExcludedToggleVisualStateForPreview();
                    if (excludedDialog.IsCorrelatedTagsEditorEnabledForPreview ||
                        !excludedDialog.AreCorrelatedTagsPanelsHiddenForPreview)
                    {
                        throw new InvalidOperationException(
                            "An excluded software row left its correlated-tags editor visible or enabled.");
                    }

                    excludedDialog.SetExcludedForPreview(isExcluded: false);
                    if (!excludedDialog.IsCorrelatedTagsEditorEnabledForPreview)
                    {
                        throw new InvalidOperationException(
                            "Unchecking software exclusion did not restore correlated-tag editing.");
                    }

                    excludedDialog.Close();

                    var blenderSetting = (await _store.GetProjectSoftwareAsync(project.Id))
                        .Single(setting => setting.Software.Id == blender.Id);
                    var softwareDialog = new SoftwareSettingsWindow(
                        blenderSetting,
                        await _store.GetTagsAsync(),
                        await _store.GetProjectOptionsAsync())
                    {
                        Owner = _mainWindow,
                    };
                    softwareDialog.Show();
                    await Task.Delay(100);
                    softwareDialog.UpdateLayout();
                    if (!softwareDialog.HasThreeRowTagViewportForPreview)
                    {
                        throw new InvalidOperationException(
                            "Software settings did not provide a three-row correlated-tag viewport with vertical overflow.");
                    }

                    softwareDialog.VerifyTagColorStatesForPreview(correlatedTags[0].Id);
                    softwareDialog.TypeNewTagsForPreview("layout, animation,");
                    if (!softwareDialog.PrepareSaveForPreview())
                    {
                        throw new InvalidOperationException(
                            "Software settings could not prepare comma-created tags for saving.");
                    }

                    var dialogTagNames = softwareDialog.SelectedTagNames;
                    if (!dialogTagNames.Contains("layout", StringComparer.OrdinalIgnoreCase) ||
                        dialogTagNames.Count(name =>
                            string.Equals(name, "animation", StringComparison.OrdinalIgnoreCase)) != 1)
                    {
                        throw new InvalidOperationException(
                            "Comma-completed Software tags did not become selected chips or reuse an existing tag.");
                    }

                    softwareDialog.VerifyTagColorStatesForPreview(
                        softwareDialog.GetTagIdForPreview("layout"));
                    var resolvedTagIds = await _mainWindow.ResolveSoftwareTagNamesForPreviewAsync(
                        dialogTagNames,
                        project.Id);
                    await _store.UpdateSoftwareAsync(
                        blender.Id,
                        project.Id,
                        "Blender 4.5",
                        isExcluded: false,
                        resolvedTagIds);
                    softwareDialog.Close();

                    var persistedLayoutTag = (await _store.GetTagsAsync()).Single(tag =>
                        string.Equals(tag.Name, "layout", StringComparison.OrdinalIgnoreCase));
                    if (!(await _store.GetSoftwareTagsByProcessAsync(project.Id, "blender"))
                        .Any(tag => tag.Id == persistedLayoutTag.Id))
                    {
                        throw new InvalidOperationException(
                            "A tag created in Software settings was not persisted and correlated with the project.");
                    }

                    var reminder = new ReminderWindow(
                        client.Name,
                        project.Name,
                        project.Color,
                        await _store.GetTasksAsync(project.Id),
                        await _store.GetSoftwareTagsByProcessAsync(project.Id, "blender.exe"),
                        await _store.GetTagsAsync());
                    if ((await _store.GetSoftwareTagsByProcessAsync(
                            alternateProject.Id,
                            "blender.exe")).Count != 0)
                    {
                        throw new InvalidOperationException(
                            "Software tags configured for one project leaked into another project.");
                    }

                    reminder.SelectTagsForPreview("animation", "rigging");
                    var reminderDescription = TagParser.AppendBracketedTags(null, reminder.SelectedTags);
                    if (!string.Equals(
                            reminderDescription,
                            "[#animation #rigging]",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The recognition reminder did not turn selected software tags into a bracketed description suffix.");
                    }

                    reminder.Close();
                    var taggedEntry = await _controller.StartTimerAsync(
                        project.Id,
                        TrackingSource.WindowReminder,
                        showDetails: false,
                        initialDescription: reminderDescription);
                    await _mainWindow.VerifyExcludedSoftwareReviewSettingForPreviewAsync();
                    if (!string.Equals(
                            await _store.GetSettingAsync(
                                ExcludedSoftwareReviewSettings.MinimumMinutesKey),
                            "5",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The excluded-software review threshold was not persisted.");
                    }
                    if (!string.Equals(
                            await _store.GetSettingAsync(
                                RecentEntryResumeSettings.MaximumGapMinutesKey),
                            "2",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The recent-entry resume threshold was not persisted.");
                    }

                    var persistedTaggedEntry = await _store.GetRunningEntryAsync();
                    if (!string.Equals(
                            persistedTaggedEntry?.Description,
                            "[#animation #rigging]",
                            StringComparison.Ordinal) ||
                        persistedTaggedEntry?.DetailsPending != false)
                    {
                        throw new InvalidOperationException(
                            "Selected reminder tags were not persisted before the automatic details flow.");
                    }

                    var excludedStart = DateTimeOffset.UtcNow;
                    _controller.ObserveActivityForPreview(new WindowActivity(
                        nint.Zero,
                        "Discord",
                        "discord",
                        excludedStart));
                    if (!_controller.HasExcludedSoftwareCandidateForPreview)
                    {
                        throw new InvalidOperationException(
                            "Excluded foreground software did not start a reviewable interval.");
                    }

                    var firstVisitEnd = excludedStart.AddMinutes(2);
                    await _controller.CompleteExcludedSoftwareVisitForPreviewAsync(
                        firstVisitEnd,
                        remove: true);
                    if (_controller.ExcludedSoftwarePromptCountForPreview != 0 ||
                        _controller.GetPendingExcludedSoftwareSecondsForPreview("discord.exe") != 120 ||
                        _controller.PendingAccumulatedAwaySecondsForPreview != 0 ||
                        await _store.GetEntryExcludedSecondsAsync(taggedEntry.Id) != 0)
                    {
                        throw new InvalidOperationException(
                            "Excluded software incorrectly entered the short-idle accumulator or prompted before its own threshold.");
                    }

                    var secondVisitStart = excludedStart.AddMinutes(3);
                    _controller.ObserveActivityForPreview(new WindowActivity(
                        nint.Zero,
                        "Discord again",
                        "discord.exe",
                        secondVisitStart));
                    var secondVisitEnd = excludedStart.AddMinutes(7);
                    await _controller.CompleteExcludedSoftwareVisitForPreviewAsync(
                        secondVisitEnd,
                        remove: true);
                    if (_controller.ExcludedSoftwarePromptCountForPreview != 1 ||
                        _controller.GetPendingExcludedSoftwareSecondsForPreview("discord") != 0 ||
                        _controller.PendingAccumulatedAwaySecondsForPreview != 0 ||
                        await _store.GetEntryExcludedSecondsAsync(taggedEntry.Id) != 360)
                    {
                        throw new InvalidOperationException(
                            "Separate excluded-software visits were not summed into one review.");
                    }

                    var thirdVisitStart = excludedStart.AddMinutes(8);
                    _controller.ObserveActivityForPreview(new WindowActivity(
                        nint.Zero,
                        "Discord once more",
                        "discord",
                        thirdVisitStart));
                    var excludedEnd = excludedStart.AddMinutes(9);
                    await _controller.CompleteExcludedSoftwareVisitForPreviewAsync(
                        excludedEnd,
                        remove: false);
                    var excludedEntry = (await _store.GetEntriesAsync(
                            taggedEntry.StartUtc.AddMinutes(-1),
                            excludedEnd.AddMinutes(1)))
                        .Single(item => item.Id == taggedEntry.Id);
                    if (_controller.ExcludedSoftwarePromptCountForPreview != 1 ||
                        excludedEntry.ExcludedSeconds != 420 ||
                        excludedEntry.SoftwareLabels.Contains("Discord", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The first excluded-software decision did not apply to later visits without another prompt.");
                    }

                    var taggedEnd = excludedEnd.AddMinutes(58);
                    await _store.UpdateTimeEntryAsync(
                        taggedEntry.Id,
                        project.Id,
                        taskId: null,
                        reminderDescription,
                        taggedEnd.AddHours(-1),
                        taggedEnd);
                    await _controller.StopTimerAsync();

                    _ = await _controller.StartTimerAsync(
                        alternateProject.Id,
                        TrackingSource.Manual,
                        showDetails: false);
                    _controller.ObserveActivityForPreview(new WindowActivity(
                        nint.Zero,
                        "Discord",
                        "discord",
                        DateTimeOffset.UtcNow));
                    if (_controller.HasExcludedSoftwareCandidateForPreview)
                    {
                        throw new InvalidOperationException(
                            "Software excluded in one project was incorrectly excluded in another project.");
                    }

                    await _controller.StopTimerAsync();
                    _detailsWindow?.CloseWithoutSaving();

                    var monthlyPath = Path.Combine(
                        _store.MonthlyLogDirectory,
                        $"TimeTracker-Logs-{end.ToLocalTime():yyyy-MM}.csv");
                    var monthlyText = await File.ReadAllTextAsync(monthlyPath);
                    if (!monthlyText.Contains("Blender 4.5", StringComparison.Ordinal) ||
                        !monthlyText.Contains("maya", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The monthly log did not contain the resolved software labels.");
                    }

                    await _mainWindow.RemoveSoftwareFromListForPreviewAsync(blender.Id);
                    if ((await _store.GetSoftwareAsync()).Any(item => item.Id == blender.Id) ||
                        (await _store.GetProjectSoftwareAsync()).Any(item => item.Software.Id == blender.Id) ||
                        (await _store.GetSoftwareTagsByProcessAsync(project.Id, "blender")).Count != 0)
                    {
                        throw new InvalidOperationException(
                            "Remove from list did not hide software or clear its project settings.");
                    }

                    var clearedEntry = (await _store.GetEntriesAsync(end.AddHours(-2), end.AddMinutes(1)))
                        .Single(item => item.Id == entry.Id);
                    if (clearedEntry.SoftwareLabels.Contains("Blender 4.5", StringComparison.Ordinal) ||
                        (await File.ReadAllTextAsync(monthlyPath)).Contains("Blender 4.5", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Removing software from the list did not clear it from historical entries and monthly logs.");
                    }

                    var restored = await _store.AddSoftwareAsync(
                        "BLENDER.EXE",
                        "Blender restored",
                        project.Id,
                        isExcluded: false,
                        tagIds: [correlatedTags[0].Id]);
                    if (restored.Id != blender.Id ||
                        (await _store.GetSoftwareAsync()).All(item => item.Id != blender.Id) ||
                        (await File.ReadAllTextAsync(monthlyPath)).Contains("Blender restored", StringComparison.Ordinal) ||
                        (await _store.GetEntriesAsync(end.AddHours(-2), end.AddMinutes(1)))
                            .Single(item => item.Id == entry.Id)
                            .SoftwareLabels.Contains("Blender restored", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Adding a previously removed process restored deleted historical software associations.");
                    }

                    await _controller.ReloadSoftwareSettingsAsync();
                    await _mainWindow.RefreshAllAsync();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_ACCUMULATED_AWAY"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync(
                        $"Accumulated away client {Guid.NewGuid():N}",
                        "#687582");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"Accumulated away project {Guid.NewGuid():N}",
                        "#339CFF");
                    var task = await _store.AddTaskAsync(project.Id, "Short interruptions");
                    await _controller.SetAccumulatedAwayReviewMinimumMinutesAsync(5);
                    var accumulatedNow = _controller.UtcNow;
                    var firstEntryStart = accumulatedNow.AddHours(-3).AddMinutes(-45);
                    var secondEntryStart = accumulatedNow.AddHours(-2);
                    await _store.AddManualEntryAsync(
                        project.Id,
                        task.Id,
                        "First short-idle entry",
                        firstEntryStart,
                        firstEntryStart.AddMinutes(90));
                    await _store.AddManualEntryAsync(
                        project.Id,
                        task.Id,
                        "Second short-idle entry",
                        secondEntryStart,
                        secondEntryStart.AddMinutes(90));
                    var recentEntries = await _store.GetEntriesAsync(
                        accumulatedNow.AddHours(-5),
                        accumulatedNow);
                    var firstEntry = recentEntries.Single(item =>
                        item.Description == "First short-idle entry");
                    var secondEntry = recentEntries.Single(item =>
                        item.Description == "Second short-idle entry");
                    var runningEntry = await _controller.StartTimerAsync(
                        project.Id,
                        TrackingSource.Manual,
                        showDetails: false,
                        initialTaskId: task.Id);

                    var firstIdleStart = firstEntryStart.AddMinutes(10);
                    await _controller.AddIdleIntervalForPreviewAsync(
                        firstIdleStart,
                        firstIdleStart.AddMinutes(2),
                        remove: false,
                        firstEntry.Id);
                    if (_controller.AccumulatedAwayPromptCountForPreview != 0 ||
                        _controller.PendingAccumulatedAwaySecondsForPreview != 120 ||
                        await _store.GetEntryExcludedSecondsAsync(firstEntry.Id) != 0)
                    {
                        throw new InvalidOperationException(
                            "A short inactive interval prompted before the accumulated-away threshold.");
                    }

                    await _controller.AddIdleIntervalForPreviewAsync(
                        secondEntryStart.AddMinutes(10),
                        secondEntryStart.AddMinutes(13),
                        remove: false,
                        secondEntry.Id);
                    if (_controller.AccumulatedAwayPromptCountForPreview != 1 ||
                        _controller.PendingAccumulatedAwaySecondsForPreview != 300 ||
                        _controller.AccumulatedAwayNextPromptMultiplierForPreview != 3 ||
                        await _store.GetEntryExcludedSecondsAsync(firstEntry.Id) != 0 ||
                        await _store.GetEntryExcludedSecondsAsync(secondEntry.Id) != 0)
                    {
                        throw new InvalidOperationException(
                            "Declining the first short-idle review did not retain its cross-entry rolling total or skip to 3x.");
                    }

                    var persistedState = await _store.GetSettingAsync(
                        AccumulatedAwayReviewSettings.DailyStateKey);
                    if (persistedState is null ||
                        !persistedState.Contains(firstEntry.Id.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                        !persistedState.Contains(secondEntry.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The rolling short-idle total was not persisted across entry boundaries.");
                    }

                    await _controller.ReloadAccumulatedAwayReviewForPreviewAsync();
                    if (_controller.PendingAccumulatedAwaySecondsForPreview != 5 * 60 ||
                        _controller.AccumulatedAwayNextPromptMultiplierForPreview != 3)
                    {
                        throw new InvalidOperationException(
                            "The persisted rolling short-idle total and declined reminder schedule were not restored.");
                    }

                    await _controller.AddIdleIntervalForPreviewAsync(
                        firstEntryStart.AddMinutes(20),
                        firstEntryStart.AddMinutes(24),
                        remove: false,
                        firstEntry.Id);
                    await _controller.AddIdleIntervalForPreviewAsync(
                        secondEntryStart.AddMinutes(20),
                        secondEntryStart.AddMinutes(24),
                        remove: false,
                        secondEntry.Id);
                    if (_controller.AccumulatedAwayPromptCountForPreview != 1 ||
                        _controller.PendingAccumulatedAwaySecondsForPreview != 13 * 60)
                    {
                        throw new InvalidOperationException(
                            "A declined short-idle review repeated before reaching the 3x threshold.");
                    }

                    await _controller.AddIdleIntervalForPreviewAsync(
                        firstEntryStart.AddMinutes(30),
                        firstEntryStart.AddMinutes(32),
                        remove: false,
                        firstEntry.Id);
                    if (_controller.AccumulatedAwayPromptCountForPreview != 2 ||
                        _controller.AccumulatedAwayNextPromptMultiplierForPreview != 4 ||
                        _controller.PendingAccumulatedAwaySecondsForPreview != 15 * 60)
                    {
                        throw new InvalidOperationException(
                            "The declined short-idle total did not prompt at 3x and advance to 4x.");
                    }

                    await _controller.AddIdleIntervalForPreviewAsync(
                        secondEntryStart.AddMinutes(30),
                        secondEntryStart.AddMinutes(34),
                        remove: true,
                        secondEntry.Id);
                    if (_controller.AccumulatedAwayPromptCountForPreview != 2 ||
                        _controller.PendingAccumulatedAwaySecondsForPreview != 19 * 60)
                    {
                        throw new InvalidOperationException(
                            "The short-idle review repeated before reaching the 4x threshold.");
                    }

                    await _controller.AddIdleIntervalForPreviewAsync(
                        firstEntryStart.AddMinutes(40),
                        firstEntryStart.AddMinutes(41),
                        remove: true,
                        firstEntry.Id);
                    if (_controller.AccumulatedAwayPromptCountForPreview != 3 ||
                        _controller.PendingAccumulatedAwaySecondsForPreview != 0 ||
                        _controller.AccumulatedAwayNextPromptMultiplierForPreview != 1 ||
                        await _store.GetEntryExcludedSecondsAsync(firstEntry.Id) != 9 * 60 ||
                        await _store.GetEntryExcludedSecondsAsync(secondEntry.Id) != 11 * 60)
                    {
                        throw new InvalidOperationException(
                            "Accepting the 4x review did not cut every retained interval from its original entry and reset the schedule.");
                    }

                    await _controller.AddIdleIntervalForPreviewAsync(
                        firstEntryStart.AddMinutes(45),
                        firstEntryStart.AddMinutes(47),
                        remove: true,
                        firstEntry.Id);
                    await _controller.AddIdleIntervalForPreviewAsync(
                        secondEntryStart.AddMinutes(45),
                        secondEntryStart.AddMinutes(48),
                        remove: true,
                        secondEntry.Id);
                    if (_controller.AccumulatedAwayPromptCountForPreview != 4 ||
                        _controller.PendingAccumulatedAwaySecondsForPreview != 0 ||
                        await _store.GetEntryExcludedSecondsAsync(firstEntry.Id) != 11 * 60 ||
                        await _store.GetEntryExcludedSecondsAsync(secondEntry.Id) != 14 * 60)
                    {
                        throw new InvalidOperationException(
                            "A new rolling short-idle batch did not prompt again at the base threshold after an accepted cut.");
                    }

                    await _controller.AddIdleIntervalForPreviewAsync(
                        runningEntry.StartUtc.AddMinutes(-5),
                        runningEntry.StartUtc,
                        remove: true);
                    if (_controller.AccumulatedAwayPromptCountForPreview != 4 ||
                        _controller.PendingAccumulatedAwaySecondsForPreview != 0 ||
                        await _store.GetEntryExcludedSecondsAsync(runningEntry.Id) != 5 * 60)
                    {
                        throw new InvalidOperationException(
                            "A five-minute idle interval was accumulated instead of being reviewed independently.");
                    }

                    await _controller.AddIdleIntervalForPreviewAsync(
                        firstEntryStart.AddMinutes(50),
                        firstEntryStart.AddMinutes(52),
                        remove: false,
                        firstEntry.Id);
                    await _controller.PruneAccumulatedAwayReviewForPreviewAsync(
                        accumulatedNow
                            .Add(ShortIdleReviewPolicy.AccumulationWindow)
                            .AddMinutes(1));
                    if (_controller.PendingAccumulatedAwaySecondsForPreview != 0 ||
                        _controller.AccumulatedAwayNextPromptMultiplierForPreview != 1)
                    {
                        throw new InvalidOperationException(
                            "Short-idle time older than the rolling four-hour window was retained instead of expiring.");
                    }

                    await _controller.StopTimerAsync();
                    _detailsWindow?.CloseWithoutSaving();

                    await _controller.AddIdleIntervalForPreviewAsync(
                        firstEntryStart.AddMinutes(55),
                        firstEntryStart.AddMinutes(57),
                        remove: false,
                        firstEntry.Id);
                    if (_controller.PendingAccumulatedAwaySecondsForPreview != 0)
                    {
                        throw new InvalidOperationException(
                            "Short idle time accumulated while no timer was running.");
                    }
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_REMINDER_TASK_SEARCH"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync(
                        $"Reminder search client {Guid.NewGuid():N}",
                        "#766F80");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"Reminder search project {Guid.NewGuid():N}",
                        "#0D8F68");
                    var animation = await _store.AddTaskAsync(project.Id, "Animation cleanup");
                    var characterAnimation = await _store.AddTaskAsync(project.Id, "Character animation");
                    var layout = await _store.AddTaskAsync(project.Id, "Layout blocking");
                    var tag = await _store.AddTagAsync("animation", "#766F80", project.Id);
                    await _store.AddSoftwareAsync(
                        "reminder-search.exe",
                        "Reminder search",
                        project.Id,
                        isExcluded: false,
                        tagIds: [tag.Id]);
                    var reminder = new ReminderWindow(
                        client.Name,
                        project.Name,
                        project.Color,
                        await _store.GetTasksAsync(project.Id),
                        [tag],
                        await _store.GetTagsAsync())
                    {
                        Owner = _mainWindow,
                    };
                    reminder.Show();
                    await Task.Delay(100);
                    await reminder.VerifyTaskSearchForPreviewAsync(
                        "anim",
                        [animation.Id, characterAnimation.Id],
                        layout.Id);
                    reminder.Close();

                    var suggestedReminder = new ReminderWindow(
                        client.Name,
                        project.Name,
                        project.Color,
                        await _store.GetTasksAsync(project.Id),
                        [tag],
                        await _store.GetTagsAsync(),
                        isProjectSwitch: false,
                        suggestedTaskId: animation.Id)
                    {
                        Owner = _mainWindow,
                    };
                    suggestedReminder.Show();
                    await Task.Delay(100);
                    if (suggestedReminder.SelectedTaskId != animation.Id ||
                        !string.Equals(
                            suggestedReminder.TaskName,
                            animation.Name,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "An unambiguous recognized task was not prefilled in the project reminder.");
                    }

                    suggestedReminder.Close();
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_REMINDER_CLICK_AWAY"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var project = (await _store.GetProjectOptionsAsync()).FirstOrDefault()
                        ?? throw new InvalidOperationException(
                            "The reminder click-away smoke check needs a seeded project.");
                    var reminder = new ReminderWindow(
                        project.ClientName,
                        project.ProjectName,
                        project.Color,
                        await _store.GetTasksAsync(project.ProjectId),
                        [],
                        await _store.GetTagsAsync());
                    reminder.Show();
                    reminder.UpdateLayout();
                    var outsidePoint = reminder.PointToScreen(new Point(-24, -24));
                    if (reminder.TryDismissForOutsideClickForPreview(outsidePoint))
                    {
                        throw new InvalidOperationException(
                            "The recognition reminder accepted a click-away before its three-second guard elapsed.");
                    }

                    await Task.Delay(3150);
                    if (!reminder.IsClickAwayArmedForPreview || !reminder.IsVisible)
                    {
                        throw new InvalidOperationException(
                            "The recognition reminder did not remain visible while waiting for user input.");
                    }

                    var insidePoint = reminder.PointToScreen(
                        new Point(reminder.ActualWidth / 2, reminder.ActualHeight / 2));
                    if (reminder.TryDismissForOutsideClickForPreview(insidePoint) || !reminder.IsVisible)
                    {
                        throw new InvalidOperationException(
                            "A click inside the recognition reminder incorrectly dismissed it.");
                    }

                    if (!reminder.TryDismissForOutsideClickForPreview(outsidePoint) || reminder.IsVisible)
                    {
                        throw new InvalidOperationException(
                            "A click outside the recognition reminder did not dismiss it after three seconds.");
                    }
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_RECOGNITION_START"),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var client = await _store.AddClientAsync(
                        $"Recognition start client {Guid.NewGuid():N}",
                        "#766F80");
                    var project = await _store.AddProjectAsync(
                        client.Id,
                        $"Recognition start project {Guid.NewGuid():N}",
                        "#0D8F68");
                    var task = await _store.AddTaskAsync(project.Id, "Selected reminder task");
                    var markerTime = DateTimeOffset.UtcNow.AddMinutes(-5);
                    await _store.AddManualEntryAsync(
                        project.Id,
                        task.Id,
                        "Tag seed #recognition",
                        markerTime.AddMinutes(-2),
                        markerTime);
                    var recognitionTag = (await _store.GetTagsAsync())
                        .Single(tag => string.Equals(
                            tag.Name,
                            "recognition",
                            StringComparison.OrdinalIgnoreCase));
                    var reminder = new ReminderWindow(
                        client.Name,
                        project.Name,
                        project.Color,
                        await _store.GetTasksAsync(project.Id),
                        [recognitionTag],
                        await _store.GetTagsAsync());
                    reminder.Show();
                    reminder.UpdateLayout();
                    reminder.SetDetailsForPreview(
                        taskId: null,
                        taskName: "Typed value to replace",
                        description: "Recognition popup description");
                    reminder.SelectTaskForPreview(task.Id);
                    reminder.VerifyTagColorStatesForPreview(recognitionTag.Name);
                    reminder.StartForPreview();
                    if (!reminder.Started ||
                        reminder.SelectedTaskId != task.Id ||
                        !string.Equals(reminder.TaskName, task.Name, StringComparison.Ordinal) ||
                        !string.Equals(
                            reminder.Description,
                            "Recognition popup description",
                            StringComparison.Ordinal) ||
                        !reminder.SelectedTags.Contains(
                            recognitionTag.Name,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"The recognition reminder did not retain its task, description, tags, and Start action " +
                            $"(started={reminder.Started}, selectedTask={reminder.SelectedTaskId}, " +
                            $"task='{reminder.TaskName}', description='{reminder.Description}', " +
                            $"tags='{string.Join(",", reminder.SelectedTags)}').");
                    }

                    var acceptedResponse = new ReminderResponse(
                        ReminderResult.Started,
                        reminder.SelectedTags,
                        reminder.SelectedTaskId,
                        reminder.TaskName,
                        reminder.Description);
                    var recognizedEntry = await _controller.StartRecognizedTimerForPreviewAsync(
                        project.Id,
                        acceptedResponse);
                    var persistedEntry = await _store.GetRunningEntryAsync();
                    const string expectedDescription =
                        "Recognition popup description [#recognition]";
                    if (_controller.RunningEntry?.Id != recognizedEntry.Id ||
                        recognizedEntry.ProjectId != project.Id ||
                        recognizedEntry.TaskId != task.Id ||
                        persistedEntry?.TaskId != task.Id ||
                        recognizedEntry.Source != TrackingSource.WindowReminder ||
                        !string.Equals(
                            persistedEntry?.Description,
                            expectedDescription,
                            StringComparison.Ordinal) ||
                        persistedEntry?.DetailsPending != false)
                    {
                        throw new InvalidOperationException(
                            "Accepting the recognition reminder did not start a timer with its selected details.");
                    }

                    if (_detailsWindow?.IsVisible == true)
                    {
                        throw new InvalidOperationException(
                            "Accepting the recognition reminder opened a redundant task-details popup.");
                    }

                    await _controller.StopTimerAsync();
                    _detailsWindow?.CloseWithoutSaving();

                    var breakReminder = new ReminderWindow(
                        client.Name,
                        project.Name,
                        project.Color,
                        await _store.GetTasksAsync(project.Id),
                        [],
                        await _store.GetTagsAsync());
                    breakReminder.Show();
                    breakReminder.UpdateLayout();
                    breakReminder.VerifySnoozeButtonForPreview();

                    const string typedTaskName = "Typed reminder task";
                    var typedReminder = new ReminderWindow(
                        client.Name,
                        project.Name,
                        project.Color,
                        await _store.GetTasksAsync(project.Id),
                        [],
                        await _store.GetTagsAsync());
                    typedReminder.Show();
                    typedReminder.UpdateLayout();
                    typedReminder.SetDetailsForPreview(
                        taskId: null,
                        taskName: typedTaskName,
                        description: null);
                    typedReminder.StartForPreview();
                    var typedEntry = await _controller.StartRecognizedTimerForPreviewAsync(
                        project.Id,
                        new ReminderResponse(
                            ReminderResult.Started,
                            [],
                            typedReminder.SelectedTaskId,
                            typedReminder.TaskName,
                            typedReminder.Description));
                    var createdTask = (await _store.GetTasksAsync(project.Id))
                        .SingleOrDefault(item => string.Equals(
                            item.Name,
                            typedTaskName,
                            StringComparison.OrdinalIgnoreCase));
                    if (!typedReminder.Started ||
                        typedReminder.SelectedTaskId is not null ||
                        createdTask is null ||
                        typedEntry.TaskId != createdTask.Id)
                    {
                        throw new InvalidOperationException(
                            "A task typed directly in the recognition reminder was not created and assigned.");
                    }

                    if (_detailsWindow?.IsVisible == true)
                    {
                        throw new InvalidOperationException(
                            "A typed-task recognition start opened a redundant details popup.");
                    }

                    await _controller.StopTimerAsync();
                    _detailsWindow?.CloseWithoutSaving();
                }

                _mainWindow.UpdateLayout();
                if (Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_SCREENSHOT") is { Length: > 0 } screenshotPath)
                {
                    var calendarPopupPreview = string.Equals(
                         smokeView,
                         "CalendarPopup",
                         StringComparison.OrdinalIgnoreCase);
                    if (string.Equals(smokeView, "DateRangeCalendar", StringComparison.OrdinalIgnoreCase))
                    {
                        var rangePicker = (DateRangePicker)_mainWindow.FindName("HistoryRangePicker");
                        rangePicker.SetRange(new DateTime(2026, 7, 2), new DateTime(2026, 7, 15), notify: false);
                        rangePicker.IsCalendarOpen = true;
                        await Task.Delay(150);
                        rangePicker.CalendarForPreview.UpdateLayout();
                        SaveElementPreview(rangePicker.CalendarForPreview, screenshotPath);
                        rangePicker.IsCalendarOpen = false;
                    }
                    else if (calendarPopupPreview ||
                        string.Equals(smokeView, "EntryEditor", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(smokeView, "EntryEditorIdle", StringComparison.OrdinalIgnoreCase))
                    {
                        TimeEntryView? editorEntry = null;
                        if (string.Equals(smokeView, "EntryEditorIdle", StringComparison.OrdinalIgnoreCase))
                        {
                            editorEntry = (await _store.GetEntriesAsync(
                                    DateTimeOffset.UtcNow.AddDays(-7),
                                    DateTimeOffset.UtcNow.AddDays(1)))
                                .FirstOrDefault(entry => entry.ExcludedSeconds > 0);
                        }

                        var dialog = new EntryEditorWindow(_store, editorEntry)
                        {
                            Owner = _mainWindow,
                        };
                        dialog.Show();
                        await Task.Delay(250);
                        dialog.UpdateLayout();
                        if (dialog.FindName("StartDatePicker") is DatePicker datePicker)
                        {
                            datePicker.IsDropDownOpen = true;
                            await Task.Delay(100);
                            var calendarPopup = datePicker.Template.FindName("PART_Popup", datePicker)
                                as System.Windows.Controls.Primitives.Popup;
                            if (calendarPopup is not { IsOpen: true, Child: System.Windows.Controls.Calendar calendar })
                            {
                                throw new InvalidOperationException("The date-picker calendar popup did not open.");
                            }

                            if (calendarPopupPreview)
                            {
                                calendar.UpdateLayout();
                                SaveElementPreview(calendar, screenshotPath);
                            }

                            datePicker.IsDropDownOpen = false;
                        }
                        if (string.Equals(
                                Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_VERIFY_ENTRY_SELECT_ALL"),
                                "true",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            await dialog.FocusEndTimeForSelectionPreviewAsync();
                        }
                        if (!calendarPopupPreview)
                        {
                            SaveWindowPreview(dialog, screenshotPath);
                        }
                        dialog.Close();
                    }
                    else if (string.Equals(smokeView, "TargetsContextMenu", StringComparison.OrdinalIgnoreCase))
                    {
                        var targets = (DataGrid)_mainWindow.FindName("CustomTargetsGrid");
                        if (targets.Items.Count == 0 || targets.ContextMenu is not { } menu)
                        {
                            throw new InvalidOperationException(
                                "The Targets context-menu preview needs a seeded target row.");
                        }

                        targets.SelectedIndex = 0;
                        menu.PlacementTarget = targets;
                        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
                        menu.IsOpen = true;
                        await Task.Delay(150);
                        menu.UpdateLayout();
                        SaveElementPreview(menu, screenshotPath);
                        menu.IsOpen = false;
                    }
                    else if (string.Equals(smokeView, "SoftwareSettings", StringComparison.OrdinalIgnoreCase))
                    {
                        var softwareRows = await _store.GetProjectSoftwareAsync();
                        var software = softwareRows.FirstOrDefault(item => item.IsExcluded)
                            ?? softwareRows.FirstOrDefault()
                            ?? throw new InvalidOperationException(
                                "The Software settings preview needs seeded software usage.");
                        var dialog = new SoftwareSettingsWindow(
                            software,
                            await _store.GetTagsAsync(),
                            await _store.GetProjectOptionsAsync())
                        {
                            Owner = _mainWindow,
                        };
                        dialog.Show();
                        await Task.Delay(150);
                        dialog.UpdateLayout();
                        SaveWindowPreview(dialog, screenshotPath);
                        dialog.Close();
                    }
                    else if (string.Equals(smokeView, "EntryDetailsUnassigned", StringComparison.OrdinalIgnoreCase))
                    {
                        var client = (await _store.GetClientsAsync()).FirstOrDefault()
                            ?? await _store.AddClientAsync("Preview client", "#766F80");
                        _ = (await _store.GetProjectsAsync()).FirstOrDefault()
                            ?? await _store.AddProjectAsync(client.Id, "Preview project", "#339CFF");
                        var entry = await _controller.StartTimerAsync(
                            SystemEntityIds.UnassignedProjectId,
                            TrackingSource.Manual,
                            showDetails: false);
                        var dialog = new EntryDetailsWindow(
                            _store,
                            entry.Id,
                            entry.ProjectId,
                            "Choose a project",
                            taskId: null,
                            description: null,
                            allowProjectSelection: true)
                        {
                            Owner = _mainWindow,
                        };
                        dialog.Show();
                        await Task.Delay(150);
                        dialog.UpdateLayout();
                        SaveWindowPreview(dialog, screenshotPath);
                        dialog.CloseWithoutSaving();
                        await _controller.StopForShutdownAsync();
                    }
                    else if (string.Equals(smokeView, "Reminder", StringComparison.OrdinalIgnoreCase))
                    {
                        var project = (await _store.GetProjectOptionsAsync()).FirstOrDefault()
                            ?? throw new InvalidOperationException(
                                "The reminder preview needs a seeded project.");
                        var software = (await _store.GetProjectSoftwareAsync(project.ProjectId))
                            .FirstOrDefault(item => item.Tags.Count > 0)
                            ?? throw new InvalidOperationException(
                                "The reminder preview needs software with correlated tags.");
                        var reminder = new ReminderWindow(
                            project.ClientName,
                            project.ProjectName,
                            project.Color,
                            await _store.GetTasksAsync(project.ProjectId),
                            software.Tags,
                            await _store.GetTagsAsync());
                        reminder.Show();
                        reminder.SelectTagsForPreview(software.Tags!.First().Name);
                        await Task.Delay(150);
                        if (Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_REMINDER_TASK_QUERY") is
                            { Length: > 0 } taskQuery)
                        {
                            reminder.TypeTaskSearchForPreview(taskQuery);
                            await Task.Delay(100);
                        }

                        reminder.UpdateLayout();
                        SaveWindowPreview(reminder, screenshotPath);
                        reminder.Close();
                    }
                    else if (string.Equals(smokeView, "RuleDialog", StringComparison.OrdinalIgnoreCase))
                    {
                        var projects = await _store.GetProjectOptionsAsync();
                        var dialog = new RuleDialog(
                            projects,
                            projects.FirstOrDefault()?.ProjectId,
                            "Creature animation",
                            "motionbuilder",
                            () => _controller.CurrentActivity,
                            isEditing: false)
                        {
                            Owner = _mainWindow,
                        };
                        dialog.Show();
                        dialog.UpdateLayout();
                        SaveWindowPreview(dialog, screenshotPath);
                        dialog.Close();
                    }
                    else if (string.Equals(smokeView, "BulkEditProjects", StringComparison.OrdinalIgnoreCase))
                    {
                        var bulkPreviewClients = await _store.GetClientsAsync();
                        var firstClient = bulkPreviewClients.FirstOrDefault()
                            ?? await _store.AddClientAsync("First preview client", "#766F80");
                        var secondClient = bulkPreviewClients.FirstOrDefault(client => client.Id != firstClient.Id)
                            ?? await _store.AddClientAsync("Second preview client", "#687582");
                        var first = new Project(
                            Guid.NewGuid(),
                            firstClient.Id,
                            "First preview project",
                            "#339CFF",
                            DailyTargetHours: 2,
                            WeeklyTargetHours: 10,
                            MonthlyTargetHours: 30,
                            HourlyRate: 120,
                            Currency: "PLN");
                        var second = new Project(
                            Guid.NewGuid(),
                            secondClient.Id,
                            "Second preview project",
                            "#40C977",
                            DailyTargetHours: 4,
                            WeeklyTargetHours: 10,
                            MonthlyTargetHours: 40,
                            HourlyRate: 160,
                            Currency: "EUR",
                            CarryOverTargetDebtEnabled: true);
                        var dialog = BulkEditWindow.ForProjects(
                            [first, second],
                            [firstClient, secondClient]);
                        dialog.Owner = _mainWindow;
                        dialog.Show();
                        await Task.Delay(150);
                        dialog.UpdateLayout();
                        dialog.VerifyProjectMixedValuesForPreview();
                        SaveWindowPreview(dialog, screenshotPath);
                        dialog.Close();
                    }
                    else if (string.Equals(smokeView, "ColorDialog", StringComparison.OrdinalIgnoreCase))
                    {
                        var dialog = new ProjectColorWindow("Color interaction", "Smoke-test color", "#339CFF")
                        {
                            Owner = _mainWindow,
                        };
                        dialog.Show();
                        await Task.Delay(150);
                        dialog.UpdateLayout();
                        dialog.VerifyWheelInteractionForPreview();
                        dialog.UpdateLayout();
                        await Task.Delay(50);
                        SaveWindowPreview(dialog, screenshotPath);
                        dialog.Close();
                    }
                    else if (string.Equals(smokeView, "NewProject", StringComparison.OrdinalIgnoreCase))
                    {
                        var previewClients = await _store.GetClientsAsync();
                        var client = previewClients.FirstOrDefault()
                            ?? await _store.AddClientAsync("Preview client", "#766F80");
                        var dialog = new NewProjectWindow([client], client.Id)
                        {
                            Owner = _mainWindow,
                        };
                        dialog.Show();
                        await Task.Delay(150);
                        dialog.SetProjectForPreview(client.Id, "New project preview", "#3A7BD5");
                        dialog.UpdateLayout();
                        SaveWindowPreview(dialog, screenshotPath);
                        dialog.Close();
                    }
                    else if (string.Equals(smokeView, "ProjectSettings", StringComparison.OrdinalIgnoreCase))
                    {
                        var previewClients = await _store.GetClientsAsync();
                        var project = (await _store.GetProjectsAsync()).First();
                        var currentClient = previewClients.First(client => client.Id == project.ClientId);
                        var selectedClient = previewClients.FirstOrDefault(client => client.Id != project.ClientId)
                            ?? currentClient;
                        var dialog = new ProjectSettingsWindow(project, currentClient.Name, previewClients)
                        {
                            Owner = _mainWindow,
                        };
                        dialog.Show();
                        await Task.Delay(150);
                        dialog.SetClientForPreview(selectedClient.Id);
                        dialog.SetTargetsForPreview(2, 10, 40);
                        dialog.UpdateLayout();
                        SaveWindowPreview(dialog, screenshotPath);
                        dialog.Close();
                    }
                    else if (string.Equals(smokeView, "TagSettings", StringComparison.OrdinalIgnoreCase))
                    {
                        var projects = await _store.GetProjectOptionsAsync();
                        var project = projects.First();
                        var dialog = new TagSettingsWindow(
                            projects,
                            suggestedColor: "#40C977")
                        {
                            Owner = _mainWindow,
                        };
                        dialog.Show();
                        await Task.Delay(150);
                        dialog.SetValuesForPreview(
                            "project-tag-preview",
                            project.ProjectId,
                            "#40C977");
                        if (!dialog.SubmitForPreview() ||
                            dialog.Result?.ProjectId != project.ProjectId)
                        {
                            throw new InvalidOperationException(
                                "Tag settings did not preserve the selected project scope.");
                        }

                        dialog.SetValuesForPreview(
                            "global-tag-preview",
                            SystemEntityIds.GlobalTagScopeId,
                            "#339CFF");
                        if (!dialog.SubmitForPreview() || dialog.Result?.ProjectId is not null)
                        {
                            throw new InvalidOperationException(
                                "Tag settings did not expose Global (all projects) scope.");
                        }

                        dialog.UpdateLayout();
                        SaveWindowPreview(dialog, screenshotPath);
                        dialog.Close();
                    }
                    else if (string.Equals(smokeView, "TextInputDialog", StringComparison.OrdinalIgnoreCase))
                    {
                        var dialog = new TextInputDialog("Edit tag", "Tag name", "animation")
                        {
                            Owner = _mainWindow,
                        };
                        dialog.Show();
                        await Task.Delay(150);
                        dialog.UpdateLayout();
                        SaveWindowPreview(dialog, screenshotPath);
                        dialog.Close();
                    }
                    else
                    {
                        SaveWindowPreview(_mainWindow, screenshotPath);
                    }
                    if (string.Equals(
                            Environment.GetEnvironmentVariable("PROJECT_TIME_TRACKER_SMOKE_SCREENSHOT_ALL"),
                            "true",
                            StringComparison.OrdinalIgnoreCase) &&
                        _mainWindow.FindName("MainTabs") is TabControl tabs)
                    {
                        var originalIndex = tabs.SelectedIndex;
                        for (var index = 0; index < tabs.Items.Count; index++)
                        {
                            tabs.SelectedIndex = index;
                            _mainWindow.UpdateLayout();
                            SaveWindowPreview(_mainWindow, AddPreviewSuffix(screenshotPath, index));
                        }

                        tabs.SelectedIndex = originalIndex;
                    }
                }

                _mainWindow.Hide();
                _exiting = true;
                _mainWindow.AllowClose = true;
                _mainWindow.Close();
                await DisposeServicesAsync();
                Shutdown(0);
                return;
            }

            var clients = await _store.GetClientsAsync();
            var background = e.Args.Any(argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
            if (!background || clients.Count == 0)
            {
                OpenMainWindow();
            }
        }
        catch (Exception exception)
        {
            if (smokeTest)
            {
                Console.Error.WriteLine(exception);
                await DisposeServicesAsync();
                Shutdown(1);
                return;
            }

            MessageBox.Show(
                $"Log O'clock could not start.\n\n{exception.Message}",
                "Startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await DisposeServicesAsync();
            Shutdown(1);
        }
    }

    private static void SaveWindowPreview(Window window, string path)
    {
        if (window is not ProjectTimeTracker.Windows.MainWindow && !window.AllowsTransparency)
        {
            VerifyDialogChromeForPreview(window);
        }

        SaveElementPreview(window, path);
    }

    private static Guid? GetRequestedProfileId(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "--profile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= arguments.Count ||
                !Guid.TryParse(arguments[index + 1], out var profileId))
            {
                throw new ArgumentException(
                    "The --profile option must be followed by a valid profile ID.");
            }

            return profileId;
        }

        return null;
    }

    private static void VerifyDialogChromeForPreview(Window window)
    {
        window.ApplyTemplate();
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
        if (window.WindowStyle != WindowStyle.None || chrome is null || Math.Abs(chrome.CaptionHeight - 46) > 0.01)
        {
            throw new InvalidOperationException($"{window.GetType().Name} is missing the shared dark dialog chrome.");
        }

        if (window.Template.FindName("DialogCloseButton", window) is not Button)
        {
            throw new InvalidOperationException($"{window.GetType().Name} is missing its themed close button.");
        }
    }

    private static void SaveElementPreview(FrameworkElement element, string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var dpi = VisualTreeHelper.GetDpi(element);
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static string AddPreviewSuffix(string path, int index) =>
        Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(path)}-{index}{Path.GetExtension(path)}");

    private sealed class FixedForegroundActivityMonitor(WindowActivity activity)
        : IForegroundActivityMonitor
    {
        private WindowActivity _activity = activity;

        public event EventHandler<WindowActivity>? ActivityChanged;

        public WindowActivity? GetCurrentActivity() => _activity;

        public void RaiseActivity(WindowActivity nextActivity)
        {
            _activity = nextActivity;
            ActivityChanged?.Invoke(this, nextActivity);
        }

        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }

    private void OpenMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private async void StopFromTray()
    {
        if (_controller?.RunningEntry is not null)
        {
            await _controller.StopTimerAsync();
        }
    }

    private async void HandleTraySingleClick()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(HandleTraySingleClick);
            return;
        }

        if (_controller?.RunningEntry is null)
        {
            OpenMainWindow();
            return;
        }

        try
        {
            await _controller.ShowRunningEntryDetailsAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                _mainWindow,
                exception.Message,
                "Could not show timer details",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void StartFromTray(Guid projectId)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => StartFromTray(projectId));
            return;
        }

        if (_controller is null)
        {
            return;
        }

        try
        {
            await _controller.StartTimerAsync(projectId, TrackingSource.Manual, showDetails: true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                _mainWindow,
                exception.Message,
                "Could not start timer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void StartUnassignedFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(StartUnassignedFromTray);
            return;
        }

        if (_controller is null)
        {
            return;
        }

        try
        {
            await _controller.StartUnassignedTimerAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                _mainWindow,
                exception.Message,
                "Could not start timer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void RequestExit()
    {
        if (_exiting)
        {
            return;
        }

        if (_controller?.RunningEntry is not null)
        {
            var result = MessageBox.Show(
                "A timer is running. Stop it and exit Log O'clock?",
                "Timer is running",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _exiting = true;
        if (_controller is not null)
        {
            await _controller.StopForShutdownAsync();
        }

        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.Close();
        }

        await DisposeServicesAsync();
        Shutdown(0);
    }

    private async Task<bool> RequestProfileSwitchAsync(
        Guid destinationProfileId,
        Guid? profileToRemoveId)
    {
        if (_exiting ||
            _profileCatalog is null ||
            _activeProfile is null)
        {
            return false;
        }

        _activeProfile = _profileCatalog.ActiveProfile;
        if (profileToRemoveId is null &&
            destinationProfileId == _activeProfile.Id)
        {
            return true;
        }

        TrackerProfile destination;
        try
        {
            destination = _profileCatalog.Profiles
                .Single(profile => profile.Id == destinationProfileId);
        }
        catch (InvalidOperationException)
        {
            MessageBox.Show(
                _mainWindow,
                "The selected profile no longer exists.",
                "Could not switch profile",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        if (_controller?.RunningEntry is not null)
        {
            var result = MessageBox.Show(
                _mainWindow,
                $"A timer is running in {_activeProfile.Name}. Stop it and switch to {destination.Name}?",
                "Switch profile",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
            {
                return false;
            }
        }

        _exiting = true;
        try
        {
            if (_controller is not null)
            {
                await _controller.StopForShutdownAsync();
            }

            if (_mainWindow is not null)
            {
                _mainWindow.AllowClose = true;
                _mainWindow.Close();
            }

            await DisposeServicesAsync();
            _activeProfile = _profileCatalog.SetActive(destinationProfileId);
            if (profileToRemoveId is { } removedProfileId)
            {
                if (_credentialStore is not null)
                {
                    await _credentialStore.DeleteTrelloCredentialsAsync(removedProfileId);
                    await _credentialStore.DeleteGoogleSheetsCredentialsAsync(removedProfileId);
                }
                _profileCatalog.Remove(removedProfileId);
            }

            StartReplacementProcess(_activeProfile.Id);
            Shutdown(0);
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Log O'clock could not switch profiles.\n\n{exception.Message}",
                "Could not switch profile",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return false;
        }
    }

    private static void StartReplacementProcess(Guid profileId)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application executable path is unavailable.");
        var startInfo = new System.Diagnostics.ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
        };
        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            var entryAssemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssemblyPath))
            {
                throw new InvalidOperationException("The application assembly path is unavailable.");
            }

            startInfo.ArgumentList.Add(entryAssemblyPath);
        }

        startInfo.ArgumentList.Add("--profile");
        startInfo.ArgumentList.Add(profileId.ToString("D"));
        _ = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("The replacement application process did not start.");
    }

    private void Controller_DetailsRequested(object? sender, EntryDetailsRequest request)
    {
        _ = sender;
        ShowDetails(request);
    }

    private async void Controller_RunningEntryChanged(object? sender, TimeEntry? entry)
    {
        _ = sender;
        if (entry is not null)
        {
            _detailsWindow?.UpdateRunningStartForExternalChange(entry);
        }

        if (entry is null &&
            _runningEntryId is Guid stoppedEntryId &&
            _detailsWindow?.EntryId == stoppedEntryId)
        {
            _detailsWindow.CloseWithoutSaving();
        }

        _runningEntryId = entry?.Id;
        await RefreshRunningLabelAsync();
        UpdateTray();
    }

    private void Controller_TimerTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateTray();
    }

    private async void Controller_DataChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await RefreshRunningLabelAsync();
        await RefreshTrayProjectsAsync();
        UpdateTray();
        _googleSheetsSync?.QueueSync();
    }

    private async Task RefreshTrayProjectsAsync()
    {
        if (_store is null || _tray is null)
        {
            return;
        }

        _tray.SetProjects(await _store.GetProjectOptionsAsync());
    }

    private async Task RefreshRunningLabelAsync()
    {
        if (_store is null || _controller?.RunningEntry is null)
        {
            _runningLabel = "Tracking";
            return;
        }

        var runningEntry = _controller.RunningEntry;
        var options = await _store.GetProjectOptionsAsync();
        var projectName =
            runningEntry.ProjectId == SystemEntityIds.UnassignedProjectId
                ? "Unassigned"
                : options
                    .FirstOrDefault(option => option.ProjectId == runningEntry.ProjectId)
                    ?.ProjectName ?? "Tracking";
        string? taskName = null;
        if (runningEntry.TaskId is Guid taskId)
        {
            taskName = (await _store.GetTasksAsync(
                    runningEntry.ProjectId,
                    includeArchived: true))
                .FirstOrDefault(task => task.Id == taskId)
                ?.Name;
        }

        _runningLabel = string.IsNullOrWhiteSpace(taskName)
            ? projectName
            : $"{taskName} · {projectName}";
    }

    private void UpdateTray()
    {
        if (_tray is null || _controller is null)
        {
            return;
        }

        if (_controller.RunningEntry is null)
        {
            _tray.Update(false, "Log O'clock — idle");
            return;
        }

        var elapsed = _controller.RunningElapsed;
        var elapsedText =
            $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        const string separator = " — ";
        var suffix = separator + elapsedText;
        var maximumLabelLength = Math.Max(1, 63 - suffix.Length);
        var visibleLabel = _runningLabel.Length <= maximumLabelLength
            ? _runningLabel
            : _runningLabel[..Math.Max(1, maximumLabelLength - 1)] + "…";
        _tray.Update(true, visibleLabel + suffix);
    }

    private void ShowDetails(EntryDetailsRequest request)
    {
        if (_store is null)
        {
            return;
        }

        if (_detailsWindow is { } existingWindow &&
            existingWindow.EntryId == request.EntryId &&
            existingWindow.IsLoaded)
        {
            existingWindow.ApplyHeading(request.Heading);
            if (request.CanRip && _controller is not null)
            {
                existingWindow.EnableRunningActions(
                    (entryId, projectId, taskId, description) =>
                        _controller.RipRunningEntryAsync(
                            entryId,
                            projectId,
                            taskId,
                            description),
                    async () => { await _controller.StopTimerAsync(); });
            }

            existingWindow.Show();
            existingWindow.Activate();
            return;
        }

        _detailsWindow?.Close();
        var window = new EntryDetailsWindow(
            _store,
            request.EntryId,
            request.ProjectId,
            request.DisplayProject,
            request.TaskId,
            request.Description,
            request.CanRip && _controller is not null
                ? (entryId, projectId, taskId, description) =>
                    _controller.RipRunningEntryAsync(
                        entryId,
                        projectId,
                        taskId,
                        description)
                : null,
            request.CanRip && _controller is not null
                ? async () => { await _controller.StopTimerAsync(); }
                : null,
            allowProjectSelection: request.AllowProjectSelection,
            runningStartUtc:
                _controller?.RunningEntry?.Id == request.EntryId
                    ? _controller.RunningEntry.StartUtc
                    : null,
            updateRunningStart:
                _controller?.RunningEntry?.Id == request.EntryId
                    ? (entryId, startUtc) =>
                        _controller.UpdateRunningStartAsync(entryId, startUtc)
                    : null,
            heading: request.Heading);
        _detailsWindow = window;
        window.DetailsSaved += (_, details) =>
            _controller?.NotifyEntryDetailsChanged(
                details.EntryId,
                details.ProjectId,
                details.TaskId,
                details.Description);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_detailsWindow, window))
            {
                _detailsWindow = null;
            }
        };
        window.Show();
        window.Activate();
    }

    private async Task DisposeServicesAsync()
    {
        _detailsWindow?.Close();
        _tray?.Dispose();
        _tray = null;
        if (_trelloSync is not null)
        {
            await _trelloSync.DisposeAsync();
            _trelloSync = null;
        }
        if (_googleSheetsSync is not null)
        {
            await _googleSheetsSync.DisposeAsync();
            _googleSheetsSync = null;
        }
        if (_controller is not null)
        {
            await _controller.DisposeAsync();
            _controller = null;
        }
        else if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        _store = null;
        _trelloApiClient?.Dispose();
        _trelloApiClient = null;
        _googleSheetsApiClient?.Dispose();
        _googleSheetsApiClient = null;
        _singleInstance?.Dispose();
        _singleInstance = null;
    }
}
