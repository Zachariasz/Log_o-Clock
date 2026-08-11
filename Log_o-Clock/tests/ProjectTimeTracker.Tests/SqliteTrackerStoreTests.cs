using System.Globalization;
using Microsoft.Data.Sqlite;
using ProjectTimeTracker.Core;
using ProjectTimeTracker.Infrastructure;

namespace ProjectTimeTracker.Tests;

public sealed class SqliteTrackerStoreTests : IAsyncLifetime
{
    private static int _sqliteInitialized;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ProjectTimeTracker.Tests", Guid.NewGuid().ToString("N"));
    private SqliteTrackerStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        if (Interlocked.Exchange(ref _sqliteInitialized, 1) == 0)
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
            SQLitePCL.raw.FreezeProvider();
        }

        Directory.CreateDirectory(_directory);
        _store = new SqliteTrackerStore(Path.Combine(_directory, "test.db"));
        await _store.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task NamesAreUniqueWithoutCase()
    {
        await _store.AddClientAsync("Acme", "#112233");
        await Assert.ThrowsAsync<SqliteException>(() => _store.AddClientAsync("ACME", "#445566"));
    }

    [Fact]
    public async Task ReportsAddBackOnlyIdleIntervalsWithinConfiguredLimit()
    {
        var client = await _store.AddClientAsync("Idle report client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Idle report project", "#223344");
        var start = new DateTimeOffset(2026, 8, 9, 8, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(
            project.Id,
            null,
            "Idle reporting",
            start,
            start.AddHours(4));
        var entry = Assert.Single(await _store.GetEntriesAsync(start, start.AddHours(4)));
        await _store.AddExclusionsAsync(
            entry.Id,
            [
                new TimeExclusionPeriod(start.AddMinutes(30), start.AddMinutes(60), "Short idle"),
                new TimeExclusionPeriod(start.AddHours(2), start.AddHours(3.5), "Long idle"),
            ]);

        var defaultRow = Assert.Single(await _store.GetReportAsync(start, start.AddHours(4)));
        Assert.Equal(2 * 3600, defaultRow.DurationSeconds);
        Assert.Equal(2 * 3600 + 30 * 60, defaultRow.DurationWithShortIdleSeconds);

        await _store.SetSettingAsync(ShortIdleReportingSettings.MaximumMinutesKey, "20");
        var stricterRow = Assert.Single(await _store.GetReportAsync(start, start.AddHours(4)));
        Assert.Equal(2 * 3600, stricterRow.DurationWithShortIdleSeconds);
    }

    [Fact]
    public async Task TargetDurationMetricPersistsThroughAddUpdateAndProjectReplacement()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var target = await _store.AddCustomTargetAsync(
            "Inclusive daily target",
            project.Id,
            CustomTargetCadence.Daily,
            8,
            TargetDurationMetric.IncludingShortIdle);

        var added = Assert.Single(await _store.GetCustomTargetsAsync(), item => item.Id == target.Id);
        Assert.Equal(TargetDurationMetric.IncludingShortIdle, added.DurationMetric);

        await _store.UpdateCustomTargetAsync(
            target.Id,
            target.Name,
            project.Id,
            CustomTargetCadence.Weekly,
            40,
            TargetDurationMetric.ActiveTime);
        var updated = Assert.Single(await _store.GetCustomTargetsAsync(), item => item.Id == target.Id);
        Assert.Equal(TargetDurationMetric.ActiveTime, updated.DurationMetric);

        await _store.ReplaceProjectTargetsAsync(
            project.Id,
            [new ProjectTargetInput(
                updated.Id,
                "Inclusive monthly target",
                CustomTargetCadence.Monthly,
                160,
                TargetDurationMetric.IncludingShortIdle)]);
        var replaced = Assert.Single(await _store.GetCustomTargetsAsync(), item => item.Id == target.Id);
        Assert.Equal(TargetDurationMetric.IncludingShortIdle, replaced.DurationMetric);
    }

    [Fact]
    public async Task ProfileDirectoriesKeepAllWorkspaceDataSeparate()
    {
        var profileRoot = Path.Combine(_directory, "profile-isolation");
        var catalog = ProfileCatalog.Load(profileRoot);
        var secondaryProfile = catalog.Add("Second person");
        var defaultDirectory = catalog.GetDataDirectory(ProfileCatalog.DefaultProfileId);
        var secondaryDirectory = catalog.GetDataDirectory(secondaryProfile.Id);
        await using var defaultStore = new SqliteTrackerStore(
            Path.Combine(defaultDirectory, "TimeTracker.db"),
            defaultDirectory);
        await using var secondaryStore = new SqliteTrackerStore(
            Path.Combine(secondaryDirectory, "TimeTracker.db"),
            secondaryDirectory);
        await defaultStore.InitializeAsync();
        await secondaryStore.InitializeAsync();

        var defaultClient = await defaultStore.AddClientAsync("Default client", "#112233");
        var defaultProject = await defaultStore.AddProjectAsync(
            defaultClient.Id,
            "Default project",
            "#223344");
        var secondaryClient = await secondaryStore.AddClientAsync("Other client", "#334455");
        var secondaryProject = await secondaryStore.AddProjectAsync(
            secondaryClient.Id,
            "Other project",
            "#445566");
        var started = new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
        await defaultStore.AddManualEntryAsync(
            defaultProject.Id,
            null,
            "Default history",
            started,
            started.AddHours(1));
        await secondaryStore.AddManualEntryAsync(
            secondaryProject.Id,
            null,
            "Other history",
            started,
            started.AddHours(2));
        await defaultStore.SetSettingAsync("profile.marker", "default");
        await secondaryStore.SetSettingAsync("profile.marker", "secondary");

        Assert.Equal(
            ["Default project"],
            (await defaultStore.GetProjectsAsync()).Select(project => project.Name));
        Assert.Equal(
            ["Other project"],
            (await secondaryStore.GetProjectsAsync()).Select(project => project.Name));
        Assert.Equal(
            ["Default history"],
            (await defaultStore.GetEntriesAsync(
                started.AddMinutes(-1),
                started.AddHours(3))).Select(entry => entry.Description));
        Assert.Equal(
            ["Other history"],
            (await secondaryStore.GetEntriesAsync(
                started.AddMinutes(-1),
                started.AddHours(3))).Select(entry => entry.Description));
        Assert.Equal("default", await defaultStore.GetSettingAsync("profile.marker"));
        Assert.Equal("secondary", await secondaryStore.GetSettingAsync("profile.marker"));
        Assert.NotEqual(defaultStore.MonthlyLogDirectory, secondaryStore.MonthlyLogDirectory);
        Assert.True(File.Exists(Path.Combine(
            defaultStore.MonthlyLogDirectory,
            "TimeTracker-Logs-2026-07.csv")));
        Assert.True(File.Exists(Path.Combine(
            secondaryStore.MonthlyLogDirectory,
            "TimeTracker-Logs-2026-07.csv")));
    }

    [Fact]
    public async Task AddingProjectCreatesDefaultRecognitionRule()
    {
        var client = await _store.AddClientAsync("Acme", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Phoenix", "#445566");
        var rules = await _store.GetRulesAsync(project.Id);
        var rule = Assert.Single(rules);
        Assert.Equal("Phoenix", rule.TitlePattern);
    }

    [Fact]
    public async Task GetOrAddTaskReusesNamesWithoutCaseAndRestoresArchivedTasks()
    {
        var client = await _store.AddClientAsync("Acme", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Phoenix", "#445566");
        var original = await _store.AddTaskAsync(project.Id, "Animation polish");

        var reused = await _store.GetOrAddTaskAsync(project.Id, "  ANIMATION POLISH  ");
        Assert.Equal(original.Id, reused.Id);
        Assert.Single(await _store.GetTasksAsync(project.Id));

        await _store.ArchiveTaskAsync(original.Id);
        var restored = await _store.GetOrAddTaskAsync(project.Id, "animation polish");

        Assert.Equal(original.Id, restored.Id);
        Assert.False(restored.IsArchived);
        Assert.Equal(original.Id, Assert.Single(await _store.GetTasksAsync(project.Id)).Id);
    }

    [Fact]
    public async Task RenamingProjectAddsNewNameAsRecognitionAlias()
    {
        var client = await _store.AddClientAsync("Acme", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Phoenix", "#445566");
        await _store.RenameProjectAsync(project.Id, "Apollo");

        var renamed = Assert.Single(await _store.GetProjectsAsync());
        Assert.Equal("Apollo", renamed.Name);
        var rules = await _store.GetRulesAsync(project.Id);
        Assert.Contains(rules, rule => rule.TitlePattern == "Phoenix");
        Assert.Contains(rules, rule => rule.TitlePattern == "Apollo");
    }

    [Fact]
    public async Task RecognitionRuleCanBeEditedAndMovedToAnotherProject()
    {
        var (firstProject, secondProject) = await CreateTwoProjectsAsync();
        var rule = await _store.AddRuleAsync(firstProject.Id, "Old title", "old.exe");

        await _store.UpdateRuleAsync(rule.Id, secondProject.Id, "New title", "chrome.exe");

        Assert.DoesNotContain(await _store.GetRulesAsync(firstProject.Id), item => item.Id == rule.Id);
        var updated = Assert.Single(await _store.GetRulesAsync(secondProject.Id), item => item.Id == rule.Id);
        Assert.Equal("New title", updated.TitlePattern);
        Assert.Equal("chrome", updated.ProcessName);
    }

    [Fact]
    public async Task RemovedProjectsAreDeletedWhileArchivedTasksKeepTheirHistory()
    {
        var activeClient = await _store.AddClientAsync("Active client", "#112233");
        var activeProject = await _store.AddProjectAsync(activeClient.Id, "Active project", "#223344");
        var archivedTask = await _store.AddTaskAsync(activeProject.Id, "Archived task");

        var archivedProject = await _store.AddProjectAsync(activeClient.Id, "Archived project", "#334455");
        var archivedProjectTask = await _store.AddTaskAsync(archivedProject.Id, "Project task");
        var archivedProjectRule = Assert.Single(await _store.GetRulesAsync(archivedProject.Id));

        var archivedClient = await _store.AddClientAsync("Archived client", "#445566");
        var archivedClientProject = await _store.AddProjectAsync(archivedClient.Id, "Client project", "#556677");
        var archivedClientTask = await _store.AddTaskAsync(archivedClientProject.Id, "Client task");

        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(
            activeProject.Id,
            archivedTask.Id,
            "Archived task history",
            start,
            start.AddMinutes(5));
        await _store.AddManualEntryAsync(
            archivedProject.Id,
            archivedProjectTask.Id,
            "Archived project history",
            start.AddMinutes(10),
            start.AddMinutes(15));
        await _store.AddManualEntryAsync(
            archivedClientProject.Id,
            archivedClientTask.Id,
            "Archived client history",
            start.AddMinutes(20),
            start.AddMinutes(25));

        await _store.ArchiveTaskAsync(archivedTask.Id);
        await _store.ArchiveProjectAsync(archivedProject.Id);
        await _store.ArchiveClientAsync(archivedClient.Id);

        Assert.DoesNotContain(await _store.GetClientsAsync(), client => client.Id == archivedClient.Id);
        Assert.DoesNotContain(
            await _store.GetClientsAsync(includeArchived: true),
            client => client.Id == archivedClient.Id);
        Assert.DoesNotContain(await _store.GetProjectsAsync(), project =>
            project.Id == archivedProject.Id || project.Id == archivedClientProject.Id);
        Assert.DoesNotContain(
            await _store.GetProjectsAsync(includeArchived: true),
            project => project.Id == archivedProject.Id || project.Id == archivedClientProject.Id);
        Assert.DoesNotContain(await _store.GetTasksAsync(), task =>
            task.Id == archivedTask.Id ||
            task.Id == archivedProjectTask.Id ||
            task.Id == archivedClientTask.Id);
        Assert.Contains(await _store.GetTasksAsync(includeArchived: true), task => task.Id == archivedTask.Id);
        Assert.DoesNotContain(
            await _store.GetTasksAsync(includeArchived: true),
            task => task.Id == archivedProjectTask.Id || task.Id == archivedClientTask.Id);
        Assert.DoesNotContain(await _store.GetProjectOptionsAsync(), option =>
            option.ProjectId == archivedProject.Id || option.ProjectId == archivedClientProject.Id);
        Assert.DoesNotContain(await _store.GetRulesAsync(), rule => rule.Id == archivedProjectRule.Id);

        var history = await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddMinutes(30));
        Assert.Contains(history, entry => entry.TaskId == archivedTask.Id);
        Assert.DoesNotContain(history, entry => entry.ProjectId == archivedProject.Id);
        Assert.DoesNotContain(history, entry => entry.ProjectId == archivedClientProject.Id);
    }

    [Fact]
    public async Task RenamesRespectCaseInsensitiveUniqueness()
    {
        var first = await _store.AddClientAsync("Acme", "#112233");
        await _store.AddClientAsync("Globex", "#445566");
        await Assert.ThrowsAsync<SqliteException>(() => _store.RenameClientAsync(first.Id, "GLOBEX"));
    }

    [Fact]
    public async Task StartingSecondTimerStopsFirstAtSameBoundary()
    {
        var (firstProject, secondProject) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        var switchAt = start.AddMinutes(20);
        var first = await _store.StartTimerAsync(firstProject.Id, TrackingSource.Manual, start);
        var second = await _store.StartTimerAsync(secondProject.Id, TrackingSource.Manual, switchAt);

        var entries = await _store.GetEntriesAsync(start.AddHours(-1), switchAt.AddHours(1));
        var stoppedFirst = Assert.Single(entries, entry => entry.Id == first.Id);
        Assert.Equal(switchAt, stoppedFirst.EndUtc);
        Assert.Equal(second.Id, (await _store.GetRunningEntryAsync())?.Id);
    }

    [Fact]
    public async Task MatchingRecentEntryIsReopenedAndKeepsItsExistingData()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var task = await _store.AddTaskAsync(project.Id, "Animation");
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        var first = await _store.StartOrResumeTimerAsync(
            project.Id,
            task.Id,
            "Scene polish #animation #review",
            TrackingSource.WindowReminder,
            start,
            TimeSpan.FromMinutes(2));
        await _store.AddExclusionAsync(
            first.Entry.Id,
            start.AddMinutes(2),
            start.AddMinutes(3),
            "Idle");
        var stoppedAt = start.AddMinutes(10);
        await _store.StopRunningTimerAsync(stoppedAt);

        var resumedAt = stoppedAt.AddMinutes(1).AddSeconds(59);
        var resumed = await _store.StartOrResumeTimerAsync(
            project.Id,
            task.Id,
            "Scene polish #animation #review",
            TrackingSource.Manual,
            resumedAt,
            TimeSpan.FromMinutes(2));

        Assert.True(first.ResumedPreviousEntry is false);
        Assert.True(resumed.ResumedPreviousEntry);
        Assert.Equal(first.Entry.Id, resumed.Entry.Id);
        Assert.Equal(start, resumed.Entry.StartUtc);
        Assert.Null(resumed.Entry.EndUtc);
        Assert.Equal(resumedAt, resumed.Entry.LastCheckpointUtc);
        Assert.Equal(TrackingSource.WindowReminder, resumed.Entry.Source);
        Assert.Equal(60, await _store.GetEntryExcludedSecondsAsync(resumed.Entry.Id));
        Assert.Single(await _store.GetEntriesAsync(start.AddMinutes(-1), resumedAt.AddMinutes(1)));
    }

    [Fact]
    public async Task RecentEntryResumeUsesStrictGapAndCanBeDisabled()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var task = await _store.AddTaskAsync(project.Id, "Animation");
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        var original = await _store.StartOrResumeTimerAsync(
            project.Id,
            task.Id,
            "Exact boundary #animation",
            TrackingSource.Manual,
            start,
            TimeSpan.FromMinutes(2));
        var stoppedAt = start.AddMinutes(10);
        await _store.StopRunningTimerAsync(stoppedAt);

        var exactBoundary = await _store.StartOrResumeTimerAsync(
            project.Id,
            task.Id,
            "Exact boundary #animation",
            TrackingSource.Manual,
            stoppedAt.AddMinutes(2),
            TimeSpan.FromMinutes(2));
        Assert.False(exactBoundary.ResumedPreviousEntry);
        Assert.NotEqual(original.Entry.Id, exactBoundary.Entry.Id);
        await _store.StopRunningTimerAsync(stoppedAt.AddMinutes(5));

        var disabled = await _store.StartOrResumeTimerAsync(
            project.Id,
            task.Id,
            "Exact boundary #animation",
            TrackingSource.Manual,
            stoppedAt.AddMinutes(5).AddSeconds(30),
            TimeSpan.Zero);
        Assert.False(disabled.ResumedPreviousEntry);
        Assert.NotEqual(exactBoundary.Entry.Id, disabled.Entry.Id);
    }

    [Fact]
    public async Task RecentEntryResumeRequiresTheImmediatelyPreviousUnpaidExactMatch()
    {
        var (project, otherProject) = await CreateTwoProjectsAsync();
        var task = await _store.AddTaskAsync(project.Id, "Animation");
        var otherTask = await _store.AddTaskAsync(otherProject.Id, "Rigging");
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        var matching = await _store.StartOrResumeTimerAsync(
            project.Id,
            task.Id,
            "Same details #animation",
            TrackingSource.Manual,
            start,
            TimeSpan.FromMinutes(2));
        await _store.StopRunningTimerAsync(start.AddMinutes(10));

        var intervening = await _store.StartOrResumeTimerAsync(
            otherProject.Id,
            otherTask.Id,
            "Different work #rigging",
            TrackingSource.Manual,
            start.AddMinutes(10).AddSeconds(30),
            TimeSpan.FromMinutes(2));
        await _store.StopRunningTimerAsync(start.AddMinutes(12));

        var afterIntervening = await _store.StartOrResumeTimerAsync(
            project.Id,
            task.Id,
            "Same details #animation",
            TrackingSource.Manual,
            start.AddMinutes(12).AddSeconds(30),
            TimeSpan.FromMinutes(2));
        Assert.False(afterIntervening.ResumedPreviousEntry);
        Assert.NotEqual(matching.Entry.Id, afterIntervening.Entry.Id);
        await _store.StopRunningTimerAsync(start.AddMinutes(14));
        await _store.SetEntriesPaidAsync([afterIntervening.Entry.Id], isPaid: true);

        var afterPaid = await _store.StartOrResumeTimerAsync(
            project.Id,
            task.Id,
            "Same details #animation",
            TrackingSource.Manual,
            start.AddMinutes(14).AddSeconds(30),
            TimeSpan.FromMinutes(2));
        Assert.False(afterPaid.ResumedPreviousEntry);
        Assert.NotEqual(afterIntervening.Entry.Id, afterPaid.Entry.Id);
        await _store.StopRunningTimerAsync(start.AddMinutes(16));

        var differentTag = await _store.StartOrResumeTimerAsync(
            project.Id,
            task.Id,
            "Same details #review",
            TrackingSource.Manual,
            start.AddMinutes(16).AddSeconds(30),
            TimeSpan.FromMinutes(2));
        Assert.False(differentTag.ResumedPreviousEntry);
        Assert.NotEqual(afterPaid.Entry.Id, differentTag.Entry.Id);
        Assert.NotEqual(intervening.Entry.Id, differentTag.Entry.Id);
    }

    [Fact]
    public async Task UnassignedTimerIsPersistentHiddenAndCanBeAssignedAfterStarting()
    {
        var client = await _store.AddClientAsync("Acme", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Phoenix", "#445566");
        var start = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);

        Assert.DoesNotContain(
            await _store.GetClientsAsync(includeArchived: true),
            item => item.Id == SystemEntityIds.UnassignedClientId);
        Assert.DoesNotContain(
            await _store.GetProjectsAsync(includeArchived: true),
            item => item.Id == SystemEntityIds.UnassignedProjectId);
        Assert.DoesNotContain(
            await _store.GetProjectOptionsAsync(),
            option => option.ProjectId == SystemEntityIds.UnassignedProjectId);

        var entry = await _store.StartTimerAsync(
            SystemEntityIds.UnassignedProjectId,
            TrackingSource.Manual,
            start);
        await _store.UpdateEntryDetailsAsync(
            entry.Id,
            taskId: null,
            "Draft description",
            start.AddMinutes(1));
        var stillUnassigned = await _store.GetRunningEntryAsync();
        Assert.Equal(SystemEntityIds.UnassignedProjectId, stillUnassigned?.ProjectId);
        Assert.True(stillUnassigned?.DetailsPending);

        var task = await _store.GetOrAddTaskAsync(project.Id, "Animation");
        await _store.UpdateEntryAssignmentAsync(
            entry.Id,
            project.Id,
            task.Id,
            "Draft description",
            start.AddMinutes(2));
        var assigned = await _store.GetRunningEntryAsync();
        Assert.Equal(project.Id, assigned?.ProjectId);
        Assert.Equal(task.Id, assigned?.TaskId);
        Assert.False(assigned?.DetailsPending);

        await _store.StopRunningTimerAsync(start.AddHours(1));
        var stored = Assert.Single(
            await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(2)));
        Assert.Equal("Acme", stored.ClientName);
        Assert.Equal("Phoenix", stored.ProjectName);
        Assert.Equal("Animation", stored.TaskName);
        Assert.Empty(await _store.GetPendingEntriesAsync());
    }

    [Fact]
    public async Task SplittingRunningTimerCopiesUpdatedDetailsAtExactBoundary()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var task = await _store.AddTaskAsync(project.Id, "Animation");
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        var splitAt = start.AddMinutes(20);
        var first = await _store.StartTimerAsync(project.Id, TrackingSource.WindowReminder, start);

        var second = await _store.SplitRunningTimerAsync(
            first.Id,
            task.Id,
            "First segment #animation",
            splitAt);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.ProjectId, second.ProjectId);
        Assert.Equal(task.Id, second.TaskId);
        Assert.Equal("First segment #animation", second.Description);
        Assert.Equal(splitAt, second.StartUtc);
        Assert.Equal(TrackingSource.WindowReminder, second.Source);

        await _store.UpdateEntryDetailsAsync(
            second.Id,
            task.Id,
            "Second segment #rigging",
            splitAt.AddMinutes(1));
        await _store.StopRunningTimerAsync(splitAt.AddMinutes(20));

        var entries = await _store.GetEntriesAsync(start.AddMinutes(-1), splitAt.AddMinutes(21));
        var stoppedFirst = Assert.Single(entries, entry => entry.Id == first.Id);
        var stoppedSecond = Assert.Single(entries, entry => entry.Id == second.Id);
        Assert.Equal(splitAt, stoppedFirst.EndUtc);
        Assert.Equal("First segment #animation", stoppedFirst.Description);
        Assert.Equal(splitAt, stoppedSecond.StartUtc);
        Assert.Equal("Second segment #rigging", stoppedSecond.Description);
    }

    [Fact]
    public async Task UpdatingRunningStartAdjustsCheckpointAndClipsEarlierExclusions()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var originalStart = new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
        var entry = await _store.StartTimerAsync(
            project.Id,
            TrackingSource.Manual,
            originalStart);
        await _store.AddExclusionAsync(
            entry.Id,
            originalStart.AddMinutes(10),
            originalStart.AddMinutes(25),
            "Earlier idle");
        await _store.AddExclusionAsync(
            entry.Id,
            originalStart.AddMinutes(35),
            originalStart.AddMinutes(45),
            "Later idle");

        var changedStart = originalStart.AddMinutes(20);
        var updated = await _store.UpdateRunningEntryStartAsync(
            entry.Id,
            changedStart,
            originalStart.AddHours(1));
        var persisted = await _store.GetRunningEntryAsync();

        Assert.Equal(changedStart, updated.StartUtc);
        Assert.Equal(changedStart, updated.LastCheckpointUtc);
        Assert.Equal(changedStart, persisted?.StartUtc);
        Assert.Equal(15 * 60, await _store.GetEntryExcludedSecondsAsync(entry.Id));

        await _store.StopRunningTimerAsync(originalStart.AddHours(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.UpdateRunningEntryStartAsync(
                entry.Id,
                originalStart,
                originalStart.AddHours(2)));
    }

    [Fact]
    public async Task SwitchingRunningTimerPreservesOldDetailsAndStartsNewProjectAtExactBoundary()
    {
        var (firstProject, secondProject) = await CreateTwoProjectsAsync();
        var firstTask = await _store.AddTaskAsync(firstProject.Id, "Animation");
        var secondTask = await _store.AddTaskAsync(secondProject.Id, "Rigging");
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        var switchAt = start.AddMinutes(20);
        var first = await _store.StartTimerAsync(firstProject.Id, TrackingSource.Manual, start);
        await _store.UpdateEntryDetailsAsync(
            first.Id,
            firstTask.Id,
            "Original project details",
            start.AddMinutes(1));
        await _store.SetEntriesPaidAsync([first.Id], isPaid: true);

        var second = await _store.SwitchRunningTimerAsync(
            first.Id,
            secondProject.Id,
            secondTask.Id,
            "Recognized project details #rigging",
            TrackingSource.WindowReminder,
            switchAt);

        Assert.Equal(secondProject.Id, second.ProjectId);
        Assert.Equal(secondTask.Id, second.TaskId);
        Assert.Equal("Recognized project details #rigging", second.Description);
        Assert.Equal(switchAt, second.StartUtc);
        Assert.Equal(TrackingSource.WindowReminder, second.Source);
        Assert.False(second.IsPaid);
        Assert.False(second.DetailsPending);
        Assert.Equal(second.Id, (await _store.GetRunningEntryAsync())?.Id);

        await _store.StopRunningTimerAsync(switchAt.AddMinutes(20));
        var entries = await _store.GetEntriesAsync(start.AddMinutes(-1), switchAt.AddMinutes(21));
        var stoppedFirst = Assert.Single(entries, entry => entry.Id == first.Id);
        var stoppedSecond = Assert.Single(entries, entry => entry.Id == second.Id);
        Assert.Equal(firstProject.Id, stoppedFirst.ProjectId);
        Assert.Equal(firstTask.Id, stoppedFirst.TaskId);
        Assert.Equal("Original project details", stoppedFirst.Description);
        Assert.Equal(switchAt, stoppedFirst.EndUtc);
        Assert.True(stoppedFirst.IsPaid);
        Assert.Equal(switchAt, stoppedSecond.StartUtc);
    }

    [Fact]
    public async Task StoppingTimerDiscardsEntriesShorterThanOneMinuteButKeepsExactlyOneMinute()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);

        await _store.StartTimerAsync(project.Id, TrackingSource.Manual, start);
        Assert.Null(await _store.StopRunningTimerAsync(start.AddSeconds(59)));
        Assert.Empty(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddMinutes(2)));
        Assert.Empty(Directory.GetFiles(_store.MonthlyLogDirectory, "TimeTracker-Logs-2026-07.csv"));

        var fractionalStart = start.AddMinutes(3).AddMilliseconds(900);
        await _store.StartTimerAsync(project.Id, TrackingSource.Manual, fractionalStart);
        Assert.Null(await _store.StopRunningTimerAsync(fractionalStart.AddMilliseconds(59_900)));

        var retained = await _store.StartTimerAsync(project.Id, TrackingSource.Manual, start.AddMinutes(5).AddMilliseconds(900));
        var stopped = await _store.StopRunningTimerAsync(start.AddMinutes(6).AddMilliseconds(900));
        Assert.Equal(retained.Id, stopped?.Id);
        Assert.Single(await _store.GetEntriesAsync(start.AddMinutes(4), start.AddMinutes(7)));
    }

    [Fact]
    public async Task ManualAndEditedEntriesShorterThanOneMinuteAreDiscarded()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);

        await _store.AddManualEntryAsync(project.Id, null, "Too short #discarded", start, start.AddSeconds(59));
        Assert.Empty(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddMinutes(2)));
        Assert.DoesNotContain(await _store.GetTagsAsync(), tag => tag.Name == "discarded");

        await _store.AddManualEntryAsync(project.Id, null, "Long enough", start, start.AddMinutes(2));
        var entry = Assert.Single(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddMinutes(3)));
        await _store.UpdateTimeEntryAsync(
            entry.Id,
            project.Id,
            null,
            "Edited below minimum",
            start,
            start.AddSeconds(30));
        Assert.Empty(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddMinutes(3)));
    }

    [Fact]
    public async Task ExclusionThatReducesCompletedEntryBelowOneMinuteRemovesIt()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "Short after idle", start, start.AddMinutes(2));
        var entry = Assert.Single(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddMinutes(3)));

        await _store.AddExclusionAsync(entry.Id, start.AddSeconds(40), start.AddSeconds(101), "Idle");

        Assert.Empty(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddMinutes(3)));
        Assert.Empty(await _store.GetReportAsync(start.AddMinutes(-1), start.AddMinutes(3)));
    }

    [Fact]
    public async Task IdleExclusionReducesReportDuration()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        var entry = await _store.StartTimerAsync(project.Id, TrackingSource.Manual, start);
        await _store.StopRunningTimerAsync(start.AddHours(1));
        await _store.AddExclusionAsync(entry.Id, start.AddMinutes(20), start.AddMinutes(30), "Idle");

        var report = await _store.GetReportAsync(start.AddMinutes(-1), start.AddHours(2));
        Assert.Equal(3_000, Assert.Single(report).DurationSeconds);
    }

    [Fact]
    public async Task MultipleExcludedSoftwareVisitsOnlySubtractTheirIndividualIntervals()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        var entry = await _store.StartTimerAsync(project.Id, TrackingSource.Manual, start);
        await _store.StopRunningTimerAsync(start.AddHours(1));

        await _store.AddExclusionsAsync(
            entry.Id,
            [
                new TimeExclusionPeriod(
                    start.AddMinutes(5),
                    start.AddMinutes(7),
                    "Excluded software: Chat"),
                new TimeExclusionPeriod(
                    start.AddMinutes(20),
                    start.AddMinutes(24),
                    "Excluded software: Chat"),
            ]);

        var saved = Assert.Single(
            await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(2)));
        Assert.Equal(6 * 60, saved.ExcludedSeconds);
        Assert.Equal(54 * 60, saved.NetDurationSeconds(saved.EndUtc!.Value));
        Assert.Equal(
            54 * 60,
            Assert.Single(await _store.GetReportAsync(start, start.AddHours(1))).DurationSeconds);
    }

    [Fact]
    public async Task EditingEntryCanChangeAndClearSubtractedIdleTime()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "Editable idle cut", start, start.AddHours(2));
        var entry = Assert.Single(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(3)));
        await _store.AddExclusionAsync(entry.Id, start.AddMinutes(20), start.AddMinutes(30), "Idle or locked");

        await _store.UpdateTimeEntryAsync(
            entry.Id,
            project.Id,
            null,
            entry.Description,
            entry.StartUtc,
            entry.EndUtc!.Value,
            excludedSeconds: 1_800);

        var adjusted = Assert.Single(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(3)));
        Assert.Equal(1_800, adjusted.ExcludedSeconds);
        Assert.Equal(5_400, adjusted.NetDurationSeconds(adjusted.EndUtc!.Value));
        Assert.Equal(5_400, Assert.Single(await _store.GetReportAsync(start, start.AddHours(2))).DurationSeconds);

        await _store.UpdateTimeEntryAsync(
            entry.Id,
            project.Id,
            null,
            entry.Description,
            entry.StartUtc,
            entry.EndUtc.Value,
            excludedSeconds: 0);

        var cleared = Assert.Single(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(3)));
        Assert.Equal(0, cleared.ExcludedSeconds);
        Assert.Equal(7_200, cleared.NetDurationSeconds(cleared.EndUtc!.Value));
    }

    [Fact]
    public async Task ProjectWorkSummaryUsesNetTimeAndFirstToLatestDates()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "First", start, start.AddHours(1));
        var firstEntry = Assert.Single(
            await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(1.5)),
            entry => entry.ProjectId == project.Id);
        await _store.AddExclusionAsync(firstEntry.Id, start.AddMinutes(20), start.AddMinutes(30), "Idle");
        await _store.AddManualEntryAsync(project.Id, null, "Latest", start.AddDays(2), start.AddDays(2).AddMinutes(30));

        var summary = Assert.Single(
            await _store.GetProjectWorkSummariesAsync(start.AddDays(3)),
            item => item.ProjectId == project.Id);

        Assert.Equal(4_800, summary.TotalSeconds);
        Assert.Equal(start, summary.FirstStartUtc);
        Assert.Equal(start.AddDays(2).AddMinutes(30), summary.LastEndUtc);
    }

    [Fact]
    public async Task TaskWorkSummaryUsesNetTimeAndIncludesTasksWithoutEntries()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var trackedTask = await _store.AddTaskAsync(project.Id, "Tracked task");
        var emptyTask = await _store.AddTaskAsync(project.Id, "Empty task");
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, trackedTask.Id, "First", start, start.AddHours(1));
        var firstEntry = Assert.Single(
            await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(1.5)),
            entry => entry.TaskId == trackedTask.Id);
        await _store.AddExclusionAsync(firstEntry.Id, start.AddMinutes(20), start.AddMinutes(30), "Idle");
        await _store.AddManualEntryAsync(
            project.Id,
            trackedTask.Id,
            "Second",
            start.AddHours(2),
            start.AddHours(2.5));

        var summaries = await _store.GetTaskWorkSummariesAsync(start.AddHours(3));

        Assert.Equal(4_800, Assert.Single(summaries, item => item.TaskId == trackedTask.Id).TotalSeconds);
        Assert.Equal(0, Assert.Single(summaries, item => item.TaskId == emptyTask.Id).TotalSeconds);
    }

    [Fact]
    public async Task ProjectColorCanBeChanged()
    {
        var (project, _) = await CreateTwoProjectsAsync();

        await _store.UpdateProjectColorAsync(project.Id, "#ab12ef");

        var updated = Assert.Single(await _store.GetProjectsAsync(), item => item.Id == project.Id);
        Assert.Equal("#AB12EF", updated.Color);
        await Assert.ThrowsAsync<ArgumentException>(() => _store.UpdateProjectColorAsync(project.Id, "violet"));
    }

    [Fact]
    public async Task ReportClipsEntriesAtRequestedDayBoundary()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 13, 23, 30, 0, TimeSpan.Zero);
        await _store.StartTimerAsync(project.Id, TrackingSource.Manual, start);
        await _store.StopRunningTimerAsync(start.AddHours(2));

        var report = await _store.GetReportAsync(
            new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(5_400, Assert.Single(report).DurationSeconds);
    }

    [Fact]
    public async Task TargetPeriodsUseCalendarReportBoundariesAndNetDurations()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var nowUtc = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(
            project.Id,
            taskId: null,
            description: null,
            new DateTimeOffset(2026, 7, 13, 23, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 1, 0, 0, TimeSpan.Zero));
        var currentDayEntry = await _store.StartTimerAsync(
            project.Id,
            TrackingSource.Manual,
            new DateTimeOffset(2026, 7, 14, 2, 0, 0, TimeSpan.Zero));
        await _store.StopRunningTimerAsync(
            new DateTimeOffset(2026, 7, 14, 4, 0, 0, TimeSpan.Zero));
        await _store.AddExclusionAsync(
            currentDayEntry.Id,
            new DateTimeOffset(2026, 7, 14, 2, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 14, 3, 0, 0, TimeSpan.Zero),
            "Idle");

        var day = TrackingPeriodCalculator.CurrentDay(nowUtc, TimeZoneInfo.Utc);
        var week = TrackingPeriodCalculator.CurrentWeek(nowUtc, TimeZoneInfo.Utc);
        var month = TrackingPeriodCalculator.CurrentMonth(nowUtc, TimeZoneInfo.Utc);

        Assert.Equal(9_000, Assert.Single(await _store.GetReportAsync(day.StartUtc, day.EndUtc)).DurationSeconds);
        Assert.Equal(12_600, Assert.Single(await _store.GetReportAsync(week.StartUtc, week.EndUtc)).DurationSeconds);
        Assert.Equal(12_600, Assert.Single(await _store.GetReportAsync(month.StartUtc, month.EndUtc)).DurationSeconds);
    }

    [Fact]
    public async Task ReportRowsExposeLatestActivityForNewestFirstTaskSorting()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var olderTask = await _store.AddTaskAsync(project.Id, "Older long task");
        var newerTask = await _store.AddTaskAsync(project.Id, "Newer short task");
        var start = new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(
            project.Id,
            olderTask.Id,
            null,
            start,
            start.AddHours(4));
        await _store.AddManualEntryAsync(
            project.Id,
            newerTask.Id,
            null,
            start.AddDays(1),
            start.AddDays(1).AddMinutes(10));

        var report = await _store.GetReportAsync(
            start.AddMinutes(-1),
            start.AddDays(2));
        var older = Assert.Single(report, row => row.TaskId == olderTask.Id);
        var newer = Assert.Single(report, row => row.TaskId == newerTask.Id);

        Assert.Equal(start.AddHours(4), older.LatestActivityUtc);
        Assert.Equal(start.AddDays(1).AddMinutes(10), newer.LatestActivityUtc);
        Assert.True(newer.LatestActivityUtc > older.LatestActivityUtc);
        Assert.True(older.DurationSeconds > newer.DurationSeconds);
    }

    [Fact]
    public async Task RecoveryStopsAtLastCheckpointAndLeavesDetailsPending()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        var checkpoint = start.AddMinutes(10);
        var entry = await _store.StartTimerAsync(project.Id, TrackingSource.WindowReminder, start);
        await _store.CheckpointRunningTimerAsync(checkpoint);
        await _store.RecoverInterruptedTimerAsync(start.AddHours(2));

        Assert.Null(await _store.GetRunningEntryAsync());
        var pending = Assert.Single(await _store.GetPendingEntriesAsync());
        Assert.Equal(entry.Id, pending.Id);
        Assert.Equal(checkpoint, pending.EndUtc);
    }

    [Fact]
    public async Task SignOutRecoveryKeepsTimerRunningForInactiveTimeReview()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var task = await _store.AddTaskAsync(project.Id, "Animation");
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        var signedOut = start.AddMinutes(20);
        var signedIn = signedOut.AddHours(8);
        var entry = await _store.StartTimerAsync(project.Id, TrackingSource.Manual, start);
        await _store.UpdateEntryDetailsAsync(
            entry.Id,
            task.Id,
            "Continue after sign-in",
            start.AddMinutes(1));
        await _store.SetSettingAsync(
            SessionTrackingSettings.BehaviorKey,
            SessionTrackingBehavior.KeepRunningAndExclude.ToString());
        await _store.CheckpointRunningTimerAsync(signedOut);
        await _store.SetSettingAsync(
            SessionTrackingSettings.ResumeMarkerKey,
            SessionTrackingSettings.FormatResumeMarker(entry.Id, signedOut));

        await _store.RecoverInterruptedTimerAsync(signedIn);

        var resumed = await _store.GetRunningEntryAsync();
        Assert.Equal(entry.Id, resumed?.Id);
        Assert.Equal(signedIn, resumed?.LastCheckpointUtc);
        Assert.Equal(task.Id, resumed?.TaskId);
        Assert.Equal("Continue after sign-in", resumed?.Description);
        Assert.Equal(0, await _store.GetEntryExcludedSecondsAsync(entry.Id));
        Assert.False(string.IsNullOrWhiteSpace(
            await _store.GetSettingAsync(SessionTrackingSettings.ResumeMarkerKey)));

        await _store.AddExclusionAsync(
            entry.Id,
            signedOut,
            signedIn,
            "Windows signed out");
        await _store.SetSettingAsync(SessionTrackingSettings.ResumeMarkerKey, string.Empty);

        await _store.StopRunningTimerAsync(signedIn.AddMinutes(20));
        var stored = Assert.Single(
            await _store.GetEntriesAsync(start.AddMinutes(-1), signedIn.AddMinutes(21)));
        Assert.Equal((long)(signedIn - signedOut).TotalSeconds, stored.ExcludedSeconds);
        Assert.Equal(40 * 60, stored.NetDurationSeconds(signedIn.AddMinutes(20)));
        Assert.Equal(entry.Id, (await _store.GetTimeEntryAsync(entry.Id))?.Id);
    }

    [Fact]
    public async Task DetailsCanClearPendingStatus()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var task = await _store.AddTaskAsync(project.Id, "Implementation");
        var entry = await _store.StartTimerAsync(project.Id, TrackingSource.WindowReminder, DateTimeOffset.UtcNow);
        await _store.UpdateEntryDetailsAsync(entry.Id, task.Id, "Implement export", DateTimeOffset.UtcNow);
        var stopped = await _store.StopRunningTimerAsync(DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.NotNull(stopped);
        Assert.Equal(task.Id, stopped.TaskId);
        Assert.Equal("Implement export", stopped.Description);
        Assert.False(stopped.DetailsPending);
        Assert.Empty(await _store.GetPendingEntriesAsync());
    }

    [Fact]
    public async Task ProjectTargetsAndBillingSettingsArePersisted()
    {
        var (project, _) = await CreateTwoProjectsAsync();

        await _store.UpdateProjectSettingsAsync(project.Id, 2, 10, 40, 125.50m, "EUR", true);

        var updated = Assert.Single(await _store.GetProjectsAsync(), item => item.Id == project.Id);
        Assert.Equal(2, updated.DailyTargetHours);
        Assert.Equal(10, updated.WeeklyTargetHours);
        Assert.Equal(40, updated.MonthlyTargetHours);
        Assert.Equal(125.50m, updated.HourlyRate);
        Assert.Equal("EUR", updated.Currency);
        Assert.True(updated.CarryOverTargetDebtEnabled);
    }

    [Fact]
    public async Task StandaloneTargetsCanBeGlobalOrProjectScopedAndAreEditable()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var global = await _store.AddCustomTargetAsync(
            "Studio monthly goal",
            projectId: null,
            CustomTargetCadence.Monthly,
            160);
        var projectTarget = await _store.AddCustomTargetAsync(
            "Phoenix delivery",
            project.Id,
            CustomTargetCadence.OneTime,
            8);

        var saved = await _store.GetCustomTargetsAsync();
        Assert.Equal(2, saved.Count);
        Assert.Equal(global.Id, Assert.Single(saved, target => target.Id == global.Id).Id);
        var savedProjectTarget = Assert.Single(saved, target => target.Id == projectTarget.Id);
        Assert.Equal(project.Id, savedProjectTarget.ProjectId);
        Assert.Null(savedProjectTarget.CompletedUtc);

        var completedUtc = new DateTimeOffset(2026, 7, 23, 14, 0, 0, TimeSpan.Zero);
        await _store.SetCustomTargetCompletionAsync(projectTarget.Id, completedUtc);
        var completedTarget = Assert.Single(
            await _store.GetCustomTargetsAsync(),
            target => target.Id == projectTarget.Id);
        Assert.Equal(completedUtc, completedTarget.CompletedUtc);

        await _store.UpdateCustomTargetAsync(
            projectTarget.Id,
            "Phoenix delivery renamed",
            project.Id,
            CustomTargetCadence.OneTime,
            8);
        var renamedCompletedTarget = Assert.Single(
            await _store.GetCustomTargetsAsync(),
            target => target.Id == projectTarget.Id);
        Assert.Equal(completedUtc, renamedCompletedTarget.CompletedUtc);

        await _store.UpdateCustomTargetAsync(
            projectTarget.Id,
            "Phoenix delivery extended",
            project.Id,
            CustomTargetCadence.OneTime,
            10);
        var extendedTarget = Assert.Single(
            await _store.GetCustomTargetsAsync(),
            target => target.Id == projectTarget.Id);
        Assert.Null(extendedTarget.CompletedUtc);

        await _store.UpdateCustomTargetAsync(
            global.Id,
            "Studio weekly goal",
            project.Id,
            CustomTargetCadence.Weekly,
            40);

        var updated = Assert.Single(await _store.GetCustomTargetsAsync(), target => target.Id == global.Id);
        Assert.Equal("Studio weekly goal", updated.Name);
        Assert.Equal(project.Id, updated.ProjectId);
        Assert.Equal(CustomTargetCadence.Weekly, updated.Cadence);
        Assert.Equal(40, updated.TargetHours);

        await _store.DeleteCustomTargetAsync(projectTarget.Id);
        Assert.Single(await _store.GetCustomTargetsAsync());
    }

    [Fact]
    public async Task VersionNineteenOneTimeTargetsMigrateWithoutAConfiguredDate()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var targetId = Guid.NewGuid();
        var createdUtc = new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);
        await using (var connection = new SqliteConnection($"Data Source={_store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DROP TABLE CustomTargets;
                CREATE TABLE CustomTargets (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL COLLATE NOCASE,
                    ProjectId TEXT NULL REFERENCES Projects(Id),
                    Cadence INTEGER NOT NULL CHECK (Cadence IN (0, 1, 2, 3)),
                    TargetHours REAL NOT NULL CHECK (TargetHours > 0),
                    OneTimeDate TEXT NULL,
                    CreatedUtc TEXT NOT NULL,
                    ModifiedUtc TEXT NOT NULL,
                    CHECK ((Cadence = 3 AND OneTimeDate IS NOT NULL) OR (Cadence <> 3 AND OneTimeDate IS NULL))
                );
                CREATE INDEX IX_CustomTargets_ProjectId ON CustomTargets (ProjectId);
                INSERT INTO CustomTargets
                    (Id, Name, ProjectId, Cadence, TargetHours, OneTimeDate, CreatedUtc, ModifiedUtc)
                VALUES
                    ($id, 'Legacy dated delivery', $project, 3, 8, '2026-07-24', $created, $created);
                PRAGMA user_version = 19;
                """;
            command.Parameters.AddWithValue("$id", targetId.ToString("D"));
            command.Parameters.AddWithValue("$project", project.Id.ToString("D"));
            command.Parameters.AddWithValue(
                "$created",
                createdUtc.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }

        var databasePath = _store.DatabasePath;
        await _store.DisposeAsync();
        _store = new SqliteTrackerStore(databasePath);
        await _store.InitializeAsync();

        var migrated = Assert.Single(
            await _store.GetCustomTargetsAsync(),
            target => target.Id == targetId);
        Assert.Equal(CustomTargetCadence.OneTime, migrated.Cadence);
        Assert.Equal(createdUtc, migrated.CreatedUtc);
        Assert.Null(migrated.CompletedUtc);

        await using var verification = new SqliteConnection($"Data Source={databasePath}");
        await verification.OpenAsync();
        await using var columns = verification.CreateCommand();
        columns.CommandText = "SELECT name FROM pragma_table_info('CustomTargets');";
        var columnNames = new List<string>();
        await using var reader = await columns.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columnNames.Add(reader.GetString(0));
        }

        Assert.DoesNotContain("OneTimeDate", columnNames);
        Assert.Contains("CompletedUtc", columnNames);
    }

    [Fact]
    public async Task RemovingProjectsPermanentlyDeletesTheirTargetsAndDebtMetadata()
    {
        var firstClient = await _store.AddClientAsync("Removed target client", "#112233");
        var firstProject = await _store.AddProjectAsync(firstClient.Id, "Removed target project", "#445566");
        var secondClient = await _store.AddClientAsync("Removed target client two", "#223344");
        var secondProject = await _store.AddProjectAsync(secondClient.Id, "Removed with client", "#556677");
        var globalTarget = await _store.AddCustomTargetAsync(
            "Global retained",
            projectId: null,
            CustomTargetCadence.Monthly,
            160);
        _ = await _store.AddCustomTargetAsync(
            "Project removed",
            firstProject.Id,
            CustomTargetCadence.Monthly,
            40);
        _ = await _store.AddCustomTargetAsync(
            "Client project removed",
            secondProject.Id,
            CustomTargetCadence.Daily,
            8);
        _ = await _store.CancelProjectTargetDebtAsync(
            firstProject.Id,
            3600,
            DateTimeOffset.UtcNow);
        _ = await _store.CancelProjectTargetDebtAsync(
            secondProject.Id,
            1800,
            DateTimeOffset.UtcNow);
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        await _store.AddManualEntryAsync(
            firstProject.Id,
            taskId: null,
            "Deleted project history",
            start,
            start.AddMinutes(5));
        await _store.AddManualEntryAsync(
            secondProject.Id,
            taskId: null,
            "Deleted client history",
            start.AddMinutes(10),
            start.AddMinutes(15));

        await _store.ArchiveProjectAsync(firstProject.Id);
        await _store.ArchiveClientAsync(secondClient.Id);

        var remainingTargets = await _store.GetCustomTargetsAsync();
        Assert.Equal(globalTarget.Id, Assert.Single(remainingTargets).Id);
        Assert.Empty(await _store.GetProjectTargetDebtCancellationsAsync(
            firstProject.Id,
            includeRestored: true));
        Assert.Empty(await _store.GetProjectTargetDebtCancellationsAsync(
            secondProject.Id,
            includeRestored: true));

        var allProjects = await _store.GetProjectsAsync(includeArchived: true);
        Assert.DoesNotContain(allProjects, project =>
            project.Id == firstProject.Id || project.Id == secondProject.Id);
        Assert.DoesNotContain(
            await _store.GetClientsAsync(includeArchived: true),
            client => client.Id == secondClient.Id);
        var history = await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(1));
        Assert.DoesNotContain(history, entry =>
            entry.ProjectId == firstProject.Id || entry.ProjectId == secondProject.Id);
    }

    [Fact]
    public async Task RemovingTheLastMonthlyTargetDeletesItsDebtMetadata()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var first = await _store.AddCustomTargetAsync(
            "Monthly target one",
            project.Id,
            CustomTargetCadence.Monthly,
            40);
        _ = await _store.CancelProjectTargetDebtAsync(
            project.Id,
            3600,
            DateTimeOffset.UtcNow);

        await _store.UpdateCustomTargetAsync(
            first.Id,
            "Weekly target",
            project.Id,
            CustomTargetCadence.Weekly,
            10);
        Assert.Empty(await _store.GetProjectTargetDebtCancellationsAsync(
            project.Id,
            includeRestored: true));

        await _store.UpdateCustomTargetAsync(
            first.Id,
            "Monthly target one",
            project.Id,
            CustomTargetCadence.Monthly,
            40);
        var second = await _store.AddCustomTargetAsync(
            "Monthly target two",
            project.Id,
            CustomTargetCadence.Monthly,
            20);
        _ = await _store.CancelProjectTargetDebtAsync(
            project.Id,
            1800,
            DateTimeOffset.UtcNow);

        await _store.DeleteCustomTargetAsync(first.Id);
        Assert.NotEmpty(await _store.GetProjectTargetDebtCancellationsAsync(
            project.Id,
            includeRestored: true));

        await _store.DeleteCustomTargetAsync(second.Id);
        Assert.Empty(await _store.GetProjectTargetDebtCancellationsAsync(
            project.Id,
            includeRestored: true));
    }

    [Fact]
    public async Task VersionNineteenUpgradePurgesDataPreviouslyLeftOnArchivedProjects()
    {
        var client = await _store.AddClientAsync("Legacy archived target client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Legacy archived target project", "#445566");
        _ = await _store.AddCustomTargetAsync(
            "Legacy archived target",
            project.Id,
            CustomTargetCadence.Monthly,
            40);
        _ = await _store.CancelProjectTargetDebtAsync(
            project.Id,
            3600,
            DateTimeOffset.UtcNow);
        var task = await _store.AddTaskAsync(project.Id, "Legacy archived task");
        var entryStart = DateTimeOffset.UtcNow.AddHours(-1);
        await _store.AddManualEntryAsync(
            project.Id,
            task.Id,
            "Legacy archived history",
            entryStart,
            entryStart.AddMinutes(5));

        await using (var connection = new SqliteConnection($"Data Source={_store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE Projects
                SET IsArchived = 1,
                    CarryOverTargetDebtEnabled = 1
                WHERE Id = $project;
                PRAGMA user_version = 18;
                """;
            command.Parameters.AddWithValue("$project", project.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        var databasePath = _store.DatabasePath;
        await _store.DisposeAsync();
        _store = new SqliteTrackerStore(databasePath);
        await _store.InitializeAsync();

        Assert.Empty(await _store.GetCustomTargetsAsync());
        Assert.Empty(await _store.GetProjectTargetDebtCancellationsAsync(
            project.Id,
            includeRestored: true));
        Assert.DoesNotContain(
            await _store.GetProjectsAsync(includeArchived: true),
            item => item.Id == project.Id);
        Assert.DoesNotContain(
            await _store.GetTasksAsync(includeArchived: true),
            item => item.Id == task.Id);
        Assert.Empty(await _store.GetEntriesAsync(
            entryStart.AddMinutes(-1),
            entryStart.AddMinutes(10)));

        await using var verification = new SqliteConnection($"Data Source={databasePath}");
        await verification.OpenAsync();
        await using var countCommand = verification.CreateCommand();
        countCommand.CommandText =
            "SELECT COUNT(*) FROM CustomTargets WHERE ProjectId = $project;";
        countCommand.Parameters.AddWithValue("$project", project.Id.ToString("D"));
        Assert.Equal(0L, (long)(await countCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ProjectTargetsRemainIndividualWhileProjectCadenceValuesAreSummaries()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var firstDaily = await _store.AddCustomTargetAsync(
            "Production",
            project.Id,
            CustomTargetCadence.Daily,
            2);
        var secondDaily = await _store.AddCustomTargetAsync(
            "Review",
            project.Id,
            CustomTargetCadence.Daily,
            3);

        var projectWithTwoTargets = Assert.Single(
            await _store.GetProjectsAsync(),
            item => item.Id == project.Id);
        Assert.Equal(5, projectWithTwoTargets.DailyTargetHours);
        Assert.Equal(2, (await _store.GetCustomTargetsAsync())
            .Count(target => target.ProjectId == project.Id &&
                target.Cadence == CustomTargetCadence.Daily));

        await _store.UpdateCustomTargetAsync(
            firstDaily.Id,
            "Monthly production",
            project.Id,
            CustomTargetCadence.Monthly,
            40);

        var changedCadence = Assert.Single(
            await _store.GetProjectsAsync(),
            item => item.Id == project.Id);
        Assert.Equal(3, changedCadence.DailyTargetHours);
        Assert.Equal(40, changedCadence.MonthlyTargetHours);

        await _store.ReplaceProjectTargetsAsync(
            project.Id,
            [
                new ProjectTargetInput(
                    secondDaily.Id,
                    "Review",
                    CustomTargetCadence.Weekly,
                    10),
                new ProjectTargetInput(
                    firstDaily.Id,
                    "Monthly production",
                    CustomTargetCadence.Monthly,
                    40),
                new ProjectTargetInput(
                    null,
                    "Monthly stretch",
                    CustomTargetCadence.Monthly,
                    8),
            ]);

        var replacedTargets = (await _store.GetCustomTargetsAsync())
            .Where(target => target.ProjectId == project.Id)
            .ToArray();
        Assert.Equal(3, replacedTargets.Length);
        Assert.Contains(replacedTargets, target => target.Id == firstDaily.Id);
        Assert.Contains(replacedTargets, target => target.Id == secondDaily.Id &&
            target.Cadence == CustomTargetCadence.Weekly);
        var summarized = Assert.Single(
            await _store.GetProjectsAsync(),
            item => item.Id == project.Id);
        Assert.Null(summarized.DailyTargetHours);
        Assert.Equal(10, summarized.WeeklyTargetHours);
        Assert.Equal(48, summarized.MonthlyTargetHours);
    }

    [Fact]
    public async Task VersionSeventeenTargetsMigrateToIndividualRecordsWithoutLosingExistingTargets()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var existingId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using (var connection = new SqliteConnection($"Data Source={_store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM CustomTargets WHERE ProjectId = $project;
                UPDATE Projects
                SET DailyTargetHours = 2,
                    WeeklyTargetHours = 10,
                    MonthlyTargetHours = 40
                WHERE Id = $project;
                INSERT INTO CustomTargets
                    (Id, Name, ProjectId, Cadence, TargetHours, CreatedUtc, ModifiedUtc, CompletedUtc)
                VALUES
                    ($id, 'Existing monthly stretch', $project, 2, 5, $now, $now, NULL);
                PRAGMA user_version = 17;
                """;
            command.Parameters.AddWithValue("$project", project.Id.ToString("D"));
            command.Parameters.AddWithValue("$id", existingId.ToString("D"));
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync();
        }

        await _store.InitializeAsync();

        var migratedTargets = (await _store.GetCustomTargetsAsync())
            .Where(target => target.ProjectId == project.Id)
            .ToArray();
        Assert.Equal(4, migratedTargets.Length);
        Assert.Contains(migratedTargets, target => target.Id == existingId);
        Assert.Single(migratedTargets, target => target.Cadence == CustomTargetCadence.Daily);
        Assert.Single(migratedTargets, target => target.Cadence == CustomTargetCadence.Weekly);
        Assert.Equal(2, migratedTargets.Count(
            target => target.Cadence == CustomTargetCadence.Monthly));
        var summarized = Assert.Single(
            await _store.GetProjectsAsync(),
            item => item.Id == project.Id);
        Assert.Equal(2, summarized.DailyTargetHours);
        Assert.Equal(10, summarized.WeeklyTargetHours);
        Assert.Equal(45, summarized.MonthlyTargetHours);
        Assert.NotEmpty(Directory.GetFiles(_directory, "test.db.backup-v17-*"));
    }

    [Fact]
    public async Task TargetDebtCarriesMonthlyShortfallAndRepaysOnlyDailySurplus()
    {
        var client = await _store.AddClientAsync("Target client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Target project", "#445566");
        await _store.UpdateProjectSettingsAsync(project.Id, 8, null, 160, null, "PLN", true);

        var julyStart = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "July work", julyStart, julyStart.AddHours(154));
        var augustStart = new DateTimeOffset(2026, 8, 2, 8, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "August surplus", augustStart, augustStart.AddHours(10));

        var debts = await _store.GetProjectTargetDebtsAsync(
            new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        var debt = Assert.Single(debts, item => item.ProjectId == project.Id);
        Assert.Equal(4 * 3600, debt.OutstandingSeconds);
        Assert.Equal(TargetDebtRepaymentBasis.Daily, debt.RepaymentBasis);
    }

    [Fact]
    public async Task TargetDebtCanBeLoweredThenCanceledAndAllAdjustmentsCanBeRestored()
    {
        var client = await _store.AddClientAsync("Debt cancellation client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Debt cancellation project", "#445566");
        await _store.UpdateProjectSettingsAsync(project.Id, null, null, 160, null, "PLN", true);
        var julyStart = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "July work", julyStart, julyStart.AddHours(154));
        var canceledAt = new DateTimeOffset(2026, 8, 15, 12, 34, 0, TimeSpan.Zero);
        var before = Assert.Single(await _store.GetProjectTargetDebtsAsync(canceledAt, TimeZoneInfo.Utc));
        Assert.Equal(6 * 3600, before.OutstandingSeconds);

        var reduction = await _store.CancelProjectTargetDebtAsync(
            project.Id,
            2 * 3600,
            canceledAt);
        var lowered = Assert.Single(await _store.GetProjectTargetDebtsAsync(
            canceledAt.AddSeconds(1),
            TimeZoneInfo.Utc));

        Assert.Equal(4 * 3600, lowered.OutstandingSeconds);
        Assert.True(lowered.HasCanceledDebt);
        Assert.Equal(2 * 3600, lowered.CanceledSeconds);
        Assert.Equal(canceledAt, lowered.LastCanceledUtc);
        Assert.Equal(reduction.Id, Assert.Single(lowered.ActiveCancellations).Id);

        var finalCancellationAt = canceledAt.AddMinutes(1);
        var cancellation = await _store.CancelProjectTargetDebtAsync(
            project.Id,
            lowered.OutstandingSeconds,
            finalCancellationAt);
        var canceled = Assert.Single(await _store.GetProjectTargetDebtsAsync(
            finalCancellationAt.AddSeconds(1),
            TimeZoneInfo.Utc));

        Assert.Equal(0, canceled.OutstandingSeconds);
        Assert.True(canceled.HasCanceledDebt);
        Assert.Equal(6 * 3600, canceled.CanceledSeconds);
        Assert.Equal(finalCancellationAt, canceled.LastCanceledUtc);
        Assert.Contains(canceled.ActiveCancellations, item => item.Id == reduction.Id);
        Assert.Contains(canceled.ActiveCancellations, item => item.Id == cancellation.Id);

        await _store.RestoreProjectTargetDebtAsync(project.Id, canceledAt.AddHours(1));
        var restored = Assert.Single(await _store.GetProjectTargetDebtsAsync(
            canceledAt.AddHours(1).AddMinutes(1),
            TimeZoneInfo.Utc));
        Assert.Equal(6 * 3600, restored.OutstandingSeconds);
        Assert.False(restored.HasCanceledDebt);
        Assert.Empty(await _store.GetProjectTargetDebtCancellationsAsync(project.Id));
        var auditRecords = await _store.GetProjectTargetDebtCancellationsAsync(
            project.Id,
            includeRestored: true);
        Assert.Equal(2, auditRecords.Count);
        Assert.Contains(auditRecords, item => item.CanceledUtc == canceledAt &&
            item.RestoredUtc == canceledAt.AddHours(1));
        Assert.Contains(auditRecords, item => item.CanceledUtc == finalCancellationAt &&
            item.RestoredUtc == canceledAt.AddHours(1));
    }

    [Fact]
    public async Task VersionSixteenUpgradeAddsDebtCancellationLedgerWithBackup()
    {
        await using (var connection = new SqliteConnection($"Data Source={_store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE ProjectTargetDebtCancellations; PRAGMA user_version = 16;";
            await command.ExecuteNonQueryAsync();
        }

        await _store.InitializeAsync();
        var client = await _store.AddClientAsync("Migrated debt client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Migrated debt project", "#445566");
        var canceledAt = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var cancellation = await _store.CancelProjectTargetDebtAsync(project.Id, 3600, canceledAt);

        Assert.Equal(cancellation.Id, Assert.Single(
            await _store.GetProjectTargetDebtCancellationsAsync(project.Id)).Id);
        Assert.NotEmpty(Directory.GetFiles(_directory, "test.db.backup-v16-*"));
    }

    [Fact]
    public async Task ProjectSettingsCanMoveProjectToAnotherClientWithoutChangingHistory()
    {
        var sourceClient = await _store.AddClientAsync("Source client", "#112233");
        var destinationClient = await _store.AddClientAsync("Destination client", "#334455");
        var project = await _store.AddProjectAsync(sourceClient.Id, "Phoenix", "#556677");
        var task = await _store.AddTaskAsync(project.Id, "Animation");
        var start = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, task.Id, "Existing work", start, start.AddHours(1));

        await _store.UpdateProjectSettingsAsync(project.Id, destinationClient.Id, 2, 10, 40, 125m, "EUR");

        var updatedProject = Assert.Single(await _store.GetProjectsAsync(), item => item.Id == project.Id);
        Assert.Equal(destinationClient.Id, updatedProject.ClientId);
        Assert.Equal(project.Id, Assert.Single(await _store.GetTasksAsync(project.Id)).ProjectId);
        var historicalEntry = Assert.Single(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(2)));
        Assert.Equal(project.Id, historicalEntry.ProjectId);
        Assert.Equal("Destination client", historicalEntry.ClientName);
    }

    [Fact]
    public async Task MovingProjectRejectsDuplicateNameWithinDestinationClient()
    {
        var sourceClient = await _store.AddClientAsync("Source client", "#112233");
        var destinationClient = await _store.AddClientAsync("Destination client", "#334455");
        var project = await _store.AddProjectAsync(sourceClient.Id, "Phoenix", "#556677");
        await _store.AddProjectAsync(destinationClient.Id, "PHOENIX", "#778899");

        await Assert.ThrowsAsync<SqliteException>(() =>
            _store.UpdateProjectSettingsAsync(project.Id, destinationClient.Id, null, null, null, null, "PLN"));

        var unchanged = Assert.Single(await _store.GetProjectsAsync(), item => item.Id == project.Id);
        Assert.Equal(sourceClient.Id, unchanged.ClientId);
    }

    [Fact]
    public async Task ReportFiltersTagsAndSeparatesPaidFromUnpaidTime()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        await _store.UpdateProjectSettingsAsync(project.Id, 2, 10, 40, 100m, "PLN");
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "First #review", start, start.AddHours(1), isPaid: true);
        await _store.AddManualEntryAsync(project.Id, null, "Second #review", start.AddHours(2), start.AddHours(2.5), isPaid: false);
        await _store.AddManualEntryAsync(project.Id, null, "Different #animation", start.AddHours(3), start.AddHours(4), isPaid: false);

        var report = Assert.Single(await _store.GetReportAsync(start.AddMinutes(-1), start.AddHours(5), "REVIEW"));

        Assert.Equal(5_400, report.DurationSeconds);
        Assert.Equal(project.Id, report.ProjectId);
        Assert.Null(report.TaskId);
        Assert.Equal(3_600, report.PaidDurationSeconds);
        Assert.Equal(1_800, report.UnpaidDurationSeconds);
        Assert.Equal(100m, report.HourlyRate);
        Assert.Equal("PLN", report.Currency);
    }

    [Fact]
    public async Task ReportFiltersByClientProjectTaskTagAndPaidStatus()
    {
        var firstClient = await _store.AddClientAsync("Acme", "#112233");
        var firstProject = await _store.AddProjectAsync(firstClient.Id, "Phoenix", "#445566");
        var firstTask = await _store.AddTaskAsync(firstProject.Id, "Animation");
        var secondClient = await _store.AddClientAsync("Globex", "#778899");
        var secondProject = await _store.AddProjectAsync(secondClient.Id, "Orion", "#99AABB");
        var secondTask = await _store.AddTaskAsync(secondProject.Id, "Review");
        var start = new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero);

        await _store.AddManualEntryAsync(firstProject.Id, firstTask.Id, "Paid #review", start, start.AddHours(1), isPaid: true);
        await _store.AddManualEntryAsync(firstProject.Id, null, "Unassigned #review", start.AddHours(2), start.AddHours(2.5));
        await _store.AddManualEntryAsync(secondProject.Id, secondTask.Id, "Other #animation", start.AddHours(3), start.AddHours(5));

        var paidTask = Assert.Single(await _store.GetReportAsync(
            start.AddMinutes(-1),
            start.AddHours(6),
            new ReportFilter(
                firstClient.Id,
                firstProject.Id,
                firstTask.Id,
                Tag: "REVIEW",
                PaidStatus: PaidStatusFilter.Paid)));
        Assert.Equal(3_600, paidTask.DurationSeconds);
        Assert.Equal(firstProject.Id, paidTask.ProjectId);
        Assert.Equal(firstTask.Id, paidTask.TaskId);
        Assert.Equal(3_600, paidTask.PaidDurationSeconds);

        var unassigned = Assert.Single(await _store.GetReportAsync(
            start.AddMinutes(-1),
            start.AddHours(6),
            new ReportFilter(
                ProjectId: firstProject.Id,
                UnassignedTaskOnly: true,
                PaidStatus: PaidStatusFilter.Unpaid)));
        Assert.Equal(1_800, unassigned.DurationSeconds);

        var secondClientRows = await _store.GetReportAsync(
            start.AddMinutes(-1),
            start.AddHours(6),
            new ReportFilter(ClientId: secondClient.Id));
        Assert.Equal(7_200, Assert.Single(secondClientRows).DurationSeconds);
    }

    [Fact]
    public async Task MultipleEntriesCanBeMarkedPaidTogether()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "One", start, start.AddHours(1));
        await _store.AddManualEntryAsync(project.Id, null, "Two", start.AddHours(2), start.AddHours(3));
        var entries = await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(4));

        await _store.SetEntriesPaidAsync(entries.Select(entry => entry.Id).ToArray(), true);

        var updated = await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(4));
        Assert.All(updated, entry => Assert.True(entry.IsPaid));
    }

    [Fact]
    public async Task UnpaidReportFilterClearsAfterMatchingLogsAreMarkedPaid()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var task = await _store.AddTaskAsync(project.Id, "Animation");
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, task.Id, "Already paid", start, start.AddHours(1), isPaid: true);
        await _store.AddManualEntryAsync(project.Id, task.Id, "Needs payment", start.AddHours(2), start.AddHours(3));
        var rangeEnd = start.AddHours(4);

        var unpaid = Assert.Single(await _store.GetReportAsync(
            start.AddMinutes(-1),
            rangeEnd,
            new ReportFilter(ProjectId: project.Id, TaskId: task.Id, PaidStatus: PaidStatusFilter.Unpaid)));
        Assert.Equal(3_600, unpaid.DurationSeconds);

        var entry = Assert.Single(
            await _store.GetEntriesAsync(start.AddMinutes(-1), rangeEnd),
            item => !item.IsPaid);
        await _store.SetEntriesPaidAsync([entry.Id], isPaid: true);

        Assert.Empty(await _store.GetReportAsync(
            start.AddMinutes(-1),
            rangeEnd,
            new ReportFilter(ProjectId: project.Id, TaskId: task.Id, PaidStatus: PaidStatusFilter.Unpaid)));
    }

    [Fact]
    public async Task NewTagsReceivePersistentNonDuplicatedColors()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "Work #anim #rig #review", start, start.AddHours(1));

        var tags = await _store.GetTagsAsync();

        Assert.Equal(["anim", "review", "rig"], tags.Select(tag => tag.Name));
        Assert.Equal(tags.Count, tags.Select(tag => tag.Color).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(tags, tag => Assert.Matches("^#[0-9A-F]{6}$", tag.Color));
    }

    [Fact]
    public async Task DescriptionTagsAreProjectRelatedAndGlobalTagsRemainAvailableEverywhere()
    {
        var (firstProject, secondProject) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(
            firstProject.Id,
            null,
            "First project #animation",
            start,
            start.AddHours(1));
        await _store.AddTagAsync("shared", "#123456", projectId: null);

        var firstTags = await _store.GetTagsAsync(firstProject.Id);
        var secondTags = await _store.GetTagsAsync(secondProject.Id);
        var projectTag = Assert.Single(firstTags, tag => tag.Name == "animation");

        Assert.False(projectTag.IsGlobal);
        Assert.Equal([firstProject.Id], projectTag.AssignedProjectIds);
        Assert.Contains(firstTags, tag => tag.Name == "shared" && tag.IsGlobal);
        Assert.DoesNotContain(secondTags, tag => tag.Name == "animation");
        Assert.Contains(secondTags, tag => tag.Name == "shared" && tag.IsGlobal);
    }

    [Fact]
    public async Task ReusingProjectTagNameAssociatesItWithEachExplicitProject()
    {
        var (firstProject, secondProject) = await CreateTwoProjectsAsync();
        var first = await _store.GetOrAddTagAsync("rig", firstProject.Id);
        var reused = await _store.GetOrAddTagAsync("RIG", secondProject.Id);

        Assert.Equal(first.Id, reused.Id);
        var stored = Assert.Single(await _store.GetTagsAsync(), tag => tag.Id == first.Id);
        Assert.False(stored.IsGlobal);
        Assert.Equal(
            new[] { firstProject.Id, secondProject.Id }.OrderBy(id => id),
            stored.AssignedProjectIds.OrderBy(id => id));
        Assert.Contains(await _store.GetTagsAsync(firstProject.Id), tag => tag.Id == first.Id);
        Assert.Contains(await _store.GetTagsAsync(secondProject.Id), tag => tag.Id == first.Id);
    }

    [Fact]
    public async Task TagSettingsCanMoveTagBetweenProjectAndGlobalScope()
    {
        var (firstProject, secondProject) = await CreateTwoProjectsAsync();
        var tag = await _store.AddTagAsync("review", "#123456", firstProject.Id);

        await _store.UpdateTagAsync(tag.Id, "reviewed", "#ABCDEF", projectId: null);

        var global = Assert.Single(await _store.GetTagsAsync(), item => item.Id == tag.Id);
        Assert.True(global.IsGlobal);
        Assert.Empty(global.AssignedProjectIds);
        Assert.Equal("reviewed", global.Name);
        Assert.Equal("#ABCDEF", global.Color);
        Assert.Contains(await _store.GetTagsAsync(secondProject.Id), item => item.Id == tag.Id);

        await _store.UpdateTagAsync(tag.Id, global.Name, global.Color, secondProject.Id);
        var projectTag = Assert.Single(await _store.GetTagsAsync(), item => item.Id == tag.Id);
        Assert.False(projectTag.IsGlobal);
        Assert.Equal([secondProject.Id], projectTag.AssignedProjectIds);
        Assert.DoesNotContain(await _store.GetTagsAsync(firstProject.Id), item => item.Id == tag.Id);
    }

    [Fact]
    public async Task GetOrAddTagNormalizesNamesReusesExistingTagsAndAllocatesUniqueColors()
    {
        var first = await _store.GetOrAddTagAsync("#Rigging");
        var reused = await _store.GetOrAddTagAsync("rigging");
        var second = await _store.GetOrAddTagAsync("animation");

        Assert.Equal("rigging", first.Name);
        Assert.Equal(first, reused);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Color, second.Color, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, (await _store.GetTagsAsync()).Count);
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetOrAddTagAsync("invalid tag"));
    }

    [Fact]
    public async Task EditingTagInOneDescriptionDoesNotChangeOtherLogs()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "First #anim", start, start.AddHours(1));
        await _store.AddManualEntryAsync(project.Id, null, "Second #anim", start.AddHours(2), start.AddHours(3));
        var entries = await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(4));
        var first = Assert.Single(entries, entry => entry.Description == "First #anim");

        await _store.UpdateTimeEntryAsync(
            first.Id,
            project.Id,
            null,
            "First #rig",
            first.StartUtc,
            first.EndUtc!.Value);

        var updated = await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(4));
        Assert.Contains(updated, entry => entry.Description == "First #rig");
        Assert.Contains(updated, entry => entry.Description == "Second #anim");
        Assert.Equal(["anim", "rig"], (await _store.GetTagsAsync()).Select(tag => tag.Name));
    }

    [Fact]
    public async Task RenamingTagFromManagementUpdatesEveryExactMatchAndKeepsColor()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "First #rig", start, start.AddHours(1));
        await _store.AddManualEntryAsync(project.Id, null, "Second #RIG and #rigging", start.AddHours(2), start.AddHours(3));
        var original = Assert.Single(await _store.GetTagsAsync(), tag => tag.Name == "rig");

        await _store.RenameTagAsync(original.Id, "rigging-main");

        var entries = await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(4));
        Assert.Contains(entries, entry => entry.Description == "First #rigging-main");
        Assert.Contains(entries, entry => entry.Description == "Second #rigging-main and #rigging");
        var renamed = Assert.Single(await _store.GetTagsAsync(), tag => tag.Id == original.Id);
        Assert.Equal("rigging-main", renamed.Name);
        Assert.Equal(original.Color, renamed.Color);
    }

    [Fact]
    public async Task TagColorCanBeChangedManually()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "Work #anim", start, start.AddHours(1));
        var tag = Assert.Single(await _store.GetTagsAsync());

        await _store.UpdateTagColorAsync(tag.Id, "#123abc");

        Assert.Equal("#123ABC", Assert.Single(await _store.GetTagsAsync()).Color);
    }

    [Fact]
    public async Task RemovingTagConvertsExactDescriptionTagsToTextAndSynchronizesMonthlyLog()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(
            project.Id,
            null,
            "First #RIG and #rigging plus #keep",
            start,
            start.AddHours(1));
        await _store.AddManualEntryAsync(
            project.Id,
            null,
            "Second #rig",
            start.AddHours(2),
            start.AddHours(3));
        var tag = Assert.Single(await _store.GetTagsAsync(), item => item.Name == "rig");

        await _store.DeleteTagAsync(tag.Id);
        await _store.InitializeAsync();

        var entries = await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(4));
        Assert.Contains(entries, entry => entry.Description == "First RIG and #rigging plus #keep");
        Assert.Contains(entries, entry => entry.Description == "Second rig");
        Assert.Equal(
            ["keep", "rigging"],
            (await _store.GetTagsAsync()).Select(item => item.Name));

        var monthlyPath = Assert.Single(Directory.GetFiles(
            _store.MonthlyLogDirectory,
            "TimeTracker-Logs-2026-07.csv"));
        var monthlyText = await File.ReadAllTextAsync(monthlyPath);
        Assert.Contains("First RIG and #rigging plus #keep", monthlyText);
        Assert.Contains("Second rig", monthlyText);
        Assert.DoesNotContain("Second #rig", monthlyText);
    }

    [Fact]
    public async Task BulkUpdatesApplyOnlySelectedFieldsAcrossManagementObjects()
    {
        var firstClient = await _store.AddClientAsync("First client", "#112233");
        var secondClient = await _store.AddClientAsync("Second client", "#445566");
        var firstProject = await _store.AddProjectAsync(firstClient.Id, "Alpha", "#111111");
        var secondProject = await _store.AddProjectAsync(firstClient.Id, "Beta", "#222222");
        await _store.UpdateProjectSettingsAsync(firstProject.Id, 1, 5, 15, 100, "PLN");
        await _store.UpdateProjectSettingsAsync(secondProject.Id, 2, 8, 20, 200, "EUR");

        await _store.BulkUpdateProjectsAsync(
            [firstProject.Id, secondProject.Id],
            new ProjectBulkEdit(
                UpdateClient: true,
                ClientId: secondClient.Id,
                UpdateColor: true,
                Color: "#ABCDEF",
                UpdateDailyTarget: true,
                DailyTargetHours: 3,
                UpdateWeeklyTarget: true,
                WeeklyTargetHours: 12,
                UpdateCarryOverTargetDebt: true,
                CarryOverTargetDebtEnabled: true));

        var projects = (await _store.GetProjectsAsync())
            .Where(project => project.Id == firstProject.Id || project.Id == secondProject.Id)
            .OrderBy(project => project.Name)
            .ToArray();
        Assert.All(projects, project =>
        {
            Assert.Equal(secondClient.Id, project.ClientId);
            Assert.Equal("#ABCDEF", project.Color);
            Assert.Equal(3, project.DailyTargetHours);
            Assert.Equal(12, project.WeeklyTargetHours);
            Assert.True(project.CarryOverTargetDebtEnabled);
        });
        Assert.Equal([15d, 20d], projects.Select(project => project.MonthlyTargetHours!.Value));
        Assert.Equal([100m, 200m], projects.Select(project => project.HourlyRate!.Value));
        Assert.Equal(["PLN", "EUR"], projects.Select(project => project.Currency));

        var firstTask = await _store.AddTaskAsync(firstProject.Id, "First task");
        var secondTask = await _store.AddTaskAsync(secondProject.Id, "Second task");
        await _store.BulkUpdateTasksAsync(
            [firstTask.Id, secondTask.Id],
            new TaskBulkEdit(UpdateProject: true, ProjectId: firstProject.Id));
        Assert.All(
            (await _store.GetTasksAsync()).Where(task => task.Id == firstTask.Id || task.Id == secondTask.Id),
            task => Assert.Equal(firstProject.Id, task.ProjectId));

        var start = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(firstProject.Id, firstTask.Id, "Work #one #two", start, start.AddHours(1));
        var tags = await _store.GetTagsAsync();
        await _store.BulkUpdateTagsAsync(
            tags.Select(tag => tag.Id).ToArray(),
            new TagBulkEdit(UpdateColor: true, Color: "#123456"));
        Assert.All(await _store.GetTagsAsync(), tag => Assert.Equal("#123456", tag.Color));

        var firstRule = await _store.AddRuleAsync(firstProject.Id, "Alpha title", "first.exe");
        var secondRule = await _store.AddRuleAsync(secondProject.Id, "Beta title", "second.exe");
        await _store.BulkUpdateRulesAsync(
            [firstRule.Id, secondRule.Id],
            new RecognitionRuleBulkEdit(
                UpdateProject: true,
                ProjectId: firstProject.Id,
                UpdateProcessName: true,
                ProcessName: "shared.exe"));
        var rules = (await _store.GetRulesAsync())
            .Where(rule => rule.Id == firstRule.Id || rule.Id == secondRule.Id)
            .OrderBy(rule => rule.TitlePattern)
            .ToArray();
        Assert.All(rules, rule =>
        {
            Assert.Equal(firstProject.Id, rule.ProjectId);
            Assert.Equal("shared", rule.ProcessName);
        });
        Assert.Equal(["Alpha title", "Beta title"], rules.Select(rule => rule.TitlePattern));
    }

    [Fact]
    public async Task UpgradeDiscoversTagsAlreadyPresentInDescriptions()
    {
        var (project, _) = await CreateTwoProjectsAsync();
        var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "Legacy #existing-tag", start, start.AddHours(1));
        await using (var connection = new SqliteConnection($"Data Source={_store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Tags; PRAGMA user_version = 2;";
            await command.ExecuteNonQueryAsync();
        }

        await _store.InitializeAsync();

        Assert.Equal("existing-tag", Assert.Single(await _store.GetTagsAsync()).Name);
        Assert.NotEmpty(Directory.GetFiles(_directory, "test.db.backup-v2-*"));
    }

    [Fact]
    public async Task VersionOneDatabaseMigratesWithoutLosingCompatibility()
    {
        var legacyPath = Path.Combine(_directory, "legacy.db");
        await using (var connection = new SqliteConnection($"Data Source={legacyPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA user_version = 1;
                CREATE TABLE Clients (Id TEXT PRIMARY KEY, Name TEXT NOT NULL COLLATE NOCASE UNIQUE, Color TEXT NOT NULL, IsArchived INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE Projects (Id TEXT PRIMARY KEY, ClientId TEXT NOT NULL REFERENCES Clients(Id), Name TEXT NOT NULL COLLATE NOCASE, Color TEXT NOT NULL, IsArchived INTEGER NOT NULL DEFAULT 0, UNIQUE (ClientId, Name));
                CREATE TABLE TimeEntries (Id TEXT PRIMARY KEY, ProjectId TEXT NOT NULL REFERENCES Projects(Id), TaskId TEXT NULL, Description TEXT NULL, StartUtc TEXT NOT NULL, EndUtc TEXT NULL, LastCheckpointUtc TEXT NOT NULL, DetailsPending INTEGER NOT NULL DEFAULT 1, Source INTEGER NOT NULL, CreatedUtc TEXT NOT NULL, ModifiedUtc TEXT NOT NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var migratedStore = new SqliteTrackerStore(legacyPath);
        await migratedStore.InitializeAsync();

        await using var verification = new SqliteConnection($"Data Source={legacyPath}");
        await verification.OpenAsync();
        var projectColumns = await ReadColumnNamesAsync(verification, "Projects");
        var entryColumns = await ReadColumnNamesAsync(verification, "TimeEntries");
        Assert.Contains("DailyTargetHours", projectColumns);
        Assert.Contains("WeeklyTargetHours", projectColumns);
        Assert.Contains("MonthlyTargetHours", projectColumns);
        Assert.Contains("HourlyRate", projectColumns);
        Assert.Contains("Currency", projectColumns);
        Assert.Contains("IsPaid", entryColumns);
        Assert.Equal(["Id", "Name", "Color", "IsGlobal"], await ReadColumnNamesAsync(verification, "Tags"));
        Assert.Equal(
            ["Id", "ProcessName", "Label", "IsExcluded", "IsHidden", "IsGlobal"],
            await ReadColumnNamesAsync(verification, "Software"));
        Assert.Equal(["SoftwareId", "TagId"], await ReadColumnNamesAsync(verification, "SoftwareTags"));
        Assert.Equal(["ProjectId", "SoftwareId", "IsExcluded"], await ReadColumnNamesAsync(verification, "ProjectSoftwareSettings"));
        Assert.Equal(["ProjectId", "SoftwareId", "TagId"], await ReadColumnNamesAsync(verification, "ProjectSoftwareTags"));
        Assert.Equal(["TagId", "ProjectId"], await ReadColumnNamesAsync(verification, "ProjectTags"));
        Assert.Equal(["TimeEntryId", "SoftwareId"], await ReadColumnNamesAsync(verification, "TimeEntrySoftware"));
        Assert.NotEmpty(Directory.GetFiles(_directory, "legacy.db.backup-v1-*"));
    }

    [Fact]
    public async Task VersionTwelveTagMigrationPreservesExistingTagsAsGlobal()
    {
        var legacyPath = Path.Combine(_directory, "version-twelve.db");
        var tagId = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={legacyPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA user_version = 12;
                CREATE TABLE Tags (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    Color TEXT NOT NULL
                );
                INSERT INTO Tags (Id, Name, Color) VALUES ($id, 'legacy-tag', '#123456');
                """;
            command.Parameters.AddWithValue("$id", tagId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await using var migratedStore = new SqliteTrackerStore(legacyPath);
        await migratedStore.InitializeAsync();

        var migrated = Assert.Single(await migratedStore.GetTagsAsync());
        Assert.Equal(tagId, migrated.Id);
        Assert.True(migrated.IsGlobal);
        Assert.Empty(migrated.AssignedProjectIds);
        Assert.NotEmpty(Directory.GetFiles(_directory, "version-twelve.db.backup-v12-*"));
    }

    [Fact]
    public async Task VersionThirteenMigrationClearsHiddenSoftwareFromHistoryAndMonthlyLogs()
    {
        var client = await _store.AddClientAsync("Acme", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Phoenix", "#445566");
        var start = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        var software = await _store.AddSoftwareAsync(
            "blender.exe",
            "Blender",
            project.Id,
            isExcluded: false,
            tagIds: []);
        var entry = await _store.StartTimerAsync(project.Id, TrackingSource.Manual, start);
        Assert.True(await _store.RecordSoftwareUsageAsync(entry.Id, "blender.exe"));
        await _store.StopRunningTimerAsync(start.AddHours(1));

        await using (var connection = new SqliteConnection($"Data Source={_store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Software SET IsHidden = 1 WHERE Id = $software; PRAGMA user_version = 13;";
            command.Parameters.AddWithValue("$software", software.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await _store.InitializeAsync();

        var migratedEntry = Assert.Single(
            await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(2)));
        Assert.Equal(string.Empty, migratedEntry.SoftwareLabels);
        var monthlyPath = Path.Combine(_store.MonthlyLogDirectory, "TimeTracker-Logs-2026-07.csv");
        Assert.DoesNotContain("Blender", await File.ReadAllTextAsync(monthlyPath), StringComparison.Ordinal);
        await using (var verification = new SqliteConnection($"Data Source={_store.DatabasePath}"))
        {
            await verification.OpenAsync();
            await using var command = verification.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM TimeEntrySoftware WHERE SoftwareId = $software;";
            command.Parameters.AddWithValue("$software", software.Id.ToString("D"));
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }

        Assert.NotEmpty(Directory.GetFiles(_directory, "test.db.backup-v13-*"));
    }

    [Fact]
    public async Task VersionThreeDatabaseAddsDailyTargetWithBackup()
    {
        var legacyPath = Path.Combine(_directory, "version-three.db");
        await using (var connection = new SqliteConnection($"Data Source={legacyPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA user_version = 3;
                CREATE TABLE Clients (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    Color TEXT NOT NULL,
                    IsArchived INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE Projects (
                    Id TEXT PRIMARY KEY,
                    ClientId TEXT NOT NULL REFERENCES Clients(Id),
                    Name TEXT NOT NULL COLLATE NOCASE,
                    Color TEXT NOT NULL,
                    IsArchived INTEGER NOT NULL DEFAULT 0,
                    WeeklyTargetHours REAL NULL,
                    MonthlyTargetHours REAL NULL,
                    HourlyRate REAL NULL,
                    Currency TEXT NOT NULL DEFAULT 'PLN',
                    UNIQUE (ClientId, Name)
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var migratedStore = new SqliteTrackerStore(legacyPath);
        await migratedStore.InitializeAsync();

        await using var verification = new SqliteConnection($"Data Source={legacyPath}");
        await verification.OpenAsync();
        Assert.Contains("DailyTargetHours", await ReadColumnNamesAsync(verification, "Projects"));
        Assert.NotEmpty(Directory.GetFiles(_directory, "version-three.db.backup-v3-*"));
    }

    [Fact]
    public async Task VersionSevenDatabaseAddsExcludedSoftwareFlagWithBackup()
    {
        var legacyPath = Path.Combine(_directory, "version-seven.db");
        await using (var connection = new SqliteConnection($"Data Source={legacyPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA user_version = 7;
                CREATE TABLE Software (
                    Id TEXT PRIMARY KEY,
                    ProcessName TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    Label TEXT NOT NULL
                );
                INSERT INTO Software (Id, ProcessName, Label)
                VALUES ('11111111-1111-1111-1111-111111111111', 'discord', 'Discord');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var migratedStore = new SqliteTrackerStore(legacyPath);
        await migratedStore.InitializeAsync();

        await using var verification = new SqliteConnection($"Data Source={legacyPath}");
        await verification.OpenAsync();
        Assert.Equal(
            ["Id", "ProcessName", "Label", "IsExcluded", "IsHidden", "IsGlobal"],
            await ReadColumnNamesAsync(verification, "Software"));
        var software = Assert.Single(await migratedStore.GetSoftwareAsync());
        Assert.Equal("Discord", software.Label);
        Assert.NotEmpty(Directory.GetFiles(_directory, "version-seven.db.backup-v7-*"));
    }

    [Fact]
    public async Task VersionEightUpgradeAddsHiddenUnassignedEntitiesWithBackup()
    {
        await using (var connection = new SqliteConnection($"Data Source={_store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM Projects WHERE Id = $project;
                DELETE FROM Clients WHERE Id = $client;
                PRAGMA user_version = 8;
                """;
            command.Parameters.AddWithValue(
                "$project",
                SystemEntityIds.UnassignedProjectId.ToString("D"));
            command.Parameters.AddWithValue(
                "$client",
                SystemEntityIds.UnassignedClientId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await _store.InitializeAsync();

        await using var verification = new SqliteConnection($"Data Source={_store.DatabasePath}");
        await verification.OpenAsync();
        await using var verifyCommand = verification.CreateCommand();
        verifyCommand.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM Clients WHERE Id = $client),
                (SELECT COUNT(*) FROM Projects WHERE Id = $project);
            """;
        verifyCommand.Parameters.AddWithValue(
            "$client",
            SystemEntityIds.UnassignedClientId.ToString("D"));
        verifyCommand.Parameters.AddWithValue(
            "$project",
            SystemEntityIds.UnassignedProjectId.ToString("D"));
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.DoesNotContain(
            await _store.GetProjectOptionsAsync(),
            option => option.ProjectId == SystemEntityIds.UnassignedProjectId);
        Assert.NotEmpty(Directory.GetFiles(_directory, "test.db.backup-v8-*"));
    }

    [Fact]
    public async Task VersionNineGlobalSoftwareSettingsMigrateToEveryProject()
    {
        var client = await _store.AddClientAsync("Legacy software client", "#112233");
        var firstProject = await _store.AddProjectAsync(client.Id, "First", "#445566");
        var secondProject = await _store.AddProjectAsync(client.Id, "Second", "#667788");
        var start = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        _ = await _store.AddSoftwareAsync(
            "blender",
            "blender",
            firstProject.Id,
            isExcluded: false,
            tagIds: []);
        var entry = await _store.StartTimerAsync(firstProject.Id, TrackingSource.Manual, start);
        Assert.True(await _store.RecordSoftwareUsageAsync(entry.Id, "blender"));
        await _store.StopRunningTimerAsync(start.AddHours(1));
        await _store.UpdateEntryDetailsAsync(entry.Id, null, "Legacy #animation", start.AddMinutes(30));
        var software = Assert.Single(await _store.GetSoftwareAsync());
        var tag = Assert.Single(await _store.GetTagsAsync());

        await using (var connection = new SqliteConnection($"Data Source={_store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Software SET IsExcluded = 1 WHERE Id = $software;
                INSERT INTO SoftwareTags (SoftwareId, TagId) VALUES ($software, $tag);
                DROP TABLE ProjectSoftwareTags;
                DROP TABLE ProjectSoftwareSettings;
                PRAGMA user_version = 9;
                """;
            command.Parameters.AddWithValue("$software", software.Id.ToString("D"));
            command.Parameters.AddWithValue("$tag", tag.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await _store.InitializeAsync();

        var settings = (await _store.GetProjectSoftwareAsync())
            .Where(setting =>
                setting.Software.Id == software.Id &&
                (setting.ProjectId == firstProject.Id || setting.ProjectId == secondProject.Id))
            .ToArray();
        Assert.Equal(2, settings.Length);
        Assert.All(settings, setting =>
        {
            Assert.True(setting.IsExcluded);
            Assert.Equal("animation", Assert.Single(setting.Tags).Name);
        });
        Assert.NotEmpty(Directory.GetFiles(_directory, "test.db.backup-v9-*"));
    }

    [Fact]
    public async Task VersionTenUpgradeAddsHiddenSoftwareFlagWithBackup()
    {
        await using (var connection = new SqliteConnection($"Data Source={_store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE Software DROP COLUMN IsHidden;
                PRAGMA user_version = 10;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await _store.InitializeAsync();

        await using var verification = new SqliteConnection($"Data Source={_store.DatabasePath}");
        await verification.OpenAsync();
        Assert.Contains("IsHidden", await ReadColumnNamesAsync(verification, "Software"));
        Assert.NotEmpty(Directory.GetFiles(_directory, "test.db.backup-v10-*"));
    }

    [Fact]
    public async Task VersionElevenUpgradeAddsGlobalSoftwareScopeWithBackup()
    {
        await using (var connection = new SqliteConnection($"Data Source={_store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE Software DROP COLUMN IsGlobal;
                PRAGMA user_version = 11;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await _store.InitializeAsync();

        await using var verification = new SqliteConnection($"Data Source={_store.DatabasePath}");
        await verification.OpenAsync();
        Assert.Contains("IsGlobal", await ReadColumnNamesAsync(verification, "Software"));
        Assert.NotEmpty(Directory.GetFiles(_directory, "test.db.backup-v11-*"));
    }

    [Fact]
    public async Task VersionFourUpgradeBacksUpAndRemovesExistingSubMinuteEntries()
    {
        var client = await _store.AddClientAsync("Cleanup client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Cleanup project", "#445566");
        var start = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        await using (var connection = new SqliteConnection($"Data Source={_store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO TimeEntries
                    (Id, ProjectId, TaskId, Description, StartUtc, EndUtc, LastCheckpointUtc,
                     DetailsPending, Source, CreatedUtc, ModifiedUtc, IsPaid)
                VALUES
                    ($id, $project, NULL, 'Legacy short entry', $start, $end, $end,
                     0, 0, $start, $end, 0);
                PRAGMA user_version = 4;
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$project", project.Id.ToString("D"));
            command.Parameters.AddWithValue("$start", start.ToString("O"));
            command.Parameters.AddWithValue("$end", start.AddSeconds(30).ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await _store.InitializeAsync();

        Assert.Empty(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddMinutes(2)));
        Assert.NotEmpty(Directory.GetFiles(_directory, "test.db.backup-v4-*"));
    }

    [Fact]
    public async Task SoftwareUsageIsDistinctPerEntryAndRenamedLabelsUpdateHistoryAndMonthlyLogs()
    {
        var client = await _store.AddClientAsync("Acme", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Phoenix", "#445566");
        var otherProject = await _store.AddProjectAsync(client.Id, "Apollo", "#556677");
        var start = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        var entry = await _store.StartTimerAsync(project.Id, TrackingSource.Manual, start);

        Assert.False(await _store.RecordSoftwareUsageAsync(entry.Id, "unknown.exe"));
        Assert.Empty(await _store.GetSoftwareAsync());
        _ = await _store.AddSoftwareAsync(
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
        Assert.True(await _store.RecordSoftwareUsageAsync(entry.Id, "blender.exe"));
        Assert.False(await _store.RecordSoftwareUsageAsync(entry.Id, "BLENDER"));
        Assert.True(await _store.RecordSoftwareUsageAsync(entry.Id, "maya"));
        await _store.StopRunningTimerAsync(start.AddHours(1));

        var stored = Assert.Single(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(2)));
        Assert.Equal("blender · maya", stored.SoftwareLabels);
        var software = await _store.GetSoftwareAsync();
        Assert.Equal(["blender", "maya"], software.Select(item => item.ProcessName));
        Assert.All(software, item => Assert.Equal(1, item.EntryCount));

        var blender = Assert.Single(software, item => item.ProcessName == "blender");
        await _store.UpdateEntryDetailsAsync(
            entry.Id,
            taskId: null,
            "Character work #animation #rigging",
            start.AddMinutes(30));
        var tags = await _store.GetTagsAsync();
        await _store.UpdateSoftwareAsync(
            blender.Id,
            project.Id,
            "Blender 4.5",
            isExcluded: false,
            tagIds: tags.Select(tag => tag.Id).ToArray());

        stored = Assert.Single(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(2)));
        Assert.Equal("Blender 4.5 · maya", stored.SoftwareLabels);
        var updatedBlender = Assert.Single(await _store.GetSoftwareAsync(), item => item.Id == blender.Id);
        Assert.Equal("Blender 4.5", updatedBlender.Label);
        Assert.Equal(
            ["animation", "rigging"],
            (await _store.GetSoftwareTagsByProcessAsync(project.Id, "BLENDER.exe")).Select(tag => tag.Name));
        Assert.Empty(await _store.GetSoftwareTagsByProcessAsync(otherProject.Id, "blender"));
        var monthlyText = await File.ReadAllTextAsync(Path.Combine(
            _store.MonthlyLogDirectory,
            "TimeTracker-Logs-2026-07.csv"));
        Assert.Contains("Software", monthlyText, StringComparison.Ordinal);
        Assert.Contains("Blender 4.5 · maya", monthlyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovedSoftwareIsClearedFromHistoryAndStartsCleanWhenReAdded()
    {
        var client = await _store.AddClientAsync("Acme", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Phoenix", "#445566");
        var start = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        _ = await _store.AddSoftwareAsync(
            "blender.exe",
            "blender",
            project.Id,
            isExcluded: false,
            tagIds: []);
        var entry = await _store.StartTimerAsync(project.Id, TrackingSource.Manual, start);
        Assert.True(await _store.RecordSoftwareUsageAsync(entry.Id, "blender.exe"));
        await _store.StopRunningTimerAsync(start.AddHours(1));
        await _store.UpdateEntryDetailsAsync(entry.Id, null, "Animation #rigging", start.AddMinutes(30));
        var tag = Assert.Single(await _store.GetTagsAsync());
        var blender = Assert.Single(await _store.GetSoftwareAsync());
        await _store.UpdateSoftwareAsync(
            blender.Id,
            project.Id,
            "Blender 4.5",
            isExcluded: true,
            tagIds: [tag.Id]);

        await _store.RemoveSoftwareFromListAsync(blender.Id);

        Assert.DoesNotContain(await _store.GetSoftwareAsync(), item => item.Id == blender.Id);
        Assert.DoesNotContain(await _store.GetProjectSoftwareAsync(), item => item.Software.Id == blender.Id);
        Assert.Empty(await _store.GetSoftwareTagsByProcessAsync(project.Id, "blender"));
        var clearedEntry = Assert.Single(
            await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(2)));
        Assert.Equal(string.Empty, clearedEntry.SoftwareLabels);
        var monthlyPath = Path.Combine(_store.MonthlyLogDirectory, "TimeTracker-Logs-2026-07.csv");
        Assert.DoesNotContain("Blender 4.5", await File.ReadAllTextAsync(monthlyPath), StringComparison.Ordinal);

        await using (var connection = new SqliteConnection($"Data Source={_store.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT IsHidden FROM Software WHERE Id = $software),
                    (SELECT COUNT(*) FROM TimeEntrySoftware WHERE SoftwareId = $software),
                    (SELECT COUNT(*) FROM ProjectSoftwareSettings WHERE SoftwareId = $software);
                """;
            command.Parameters.AddWithValue("$software", blender.Id.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(0, reader.GetInt32(1));
            Assert.Equal(0, reader.GetInt32(2));
        }

        var restored = await _store.AddSoftwareAsync(
            "BLENDER.EXE",
            "Blender restored",
            project.Id,
            isExcluded: false,
            tagIds: [tag.Id]);
        Assert.Equal(blender.Id, restored.Id);
        Assert.Equal("Blender restored", Assert.Single(
            await _store.GetSoftwareAsync(), item => item.Id == blender.Id).Label);
        var restoredSetting = Assert.Single(
            await _store.GetProjectSoftwareAsync(project.Id),
            item => item.Software.Id == blender.Id);
        Assert.False(restoredSetting.IsExcluded);
        Assert.Equal("rigging", Assert.Single(restoredSetting.Tags).Name);
        Assert.Equal(0, Assert.Single(
            await _store.GetSoftwareAsync(), item => item.Id == blender.Id).EntryCount);
        Assert.Equal(string.Empty, Assert.Single(
            await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(2))).SoftwareLabels);
    }

    [Fact]
    public async Task ExcludedSoftwareCanBeAddedBeforeUseAndIsNotAssociatedWithTrackedEntries()
    {
        var client = await _store.AddClientAsync("Acme", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Phoenix", "#445566");
        var otherProject = await _store.AddProjectAsync(client.Id, "Apollo", "#556677");
        var discord = await _store.AddSoftwareAsync(
            "discord.exe",
            "Discord",
            project.Id,
            isExcluded: true,
            tagIds: []);
        _ = await _store.AddSoftwareAsync(
            "blender",
            "blender",
            project.Id,
            isExcluded: false,
            tagIds: []);

        var beforeUse = Assert.Single(
            await _store.GetProjectSoftwareAsync(project.Id),
            item => item.Software.Id == discord.Id);
        Assert.Equal("discord", beforeUse.Software.ProcessName);
        Assert.True(beforeUse.IsExcluded);
        Assert.Equal(0, beforeUse.Software.EntryCount);

        var start = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        var firstEntry = await _store.StartTimerAsync(project.Id, TrackingSource.Manual, start);
        Assert.False(await _store.RecordSoftwareUsageAsync(firstEntry.Id, "DISCORD.EXE"));
        Assert.True(await _store.RecordSoftwareUsageAsync(firstEntry.Id, "blender"));
        await _store.StopRunningTimerAsync(start.AddHours(1));

        var stored = Assert.Single(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(2)));
        Assert.Equal("blender", stored.SoftwareLabels);

        var otherStart = start.AddHours(2);
        var otherEntry = await _store.StartTimerAsync(otherProject.Id, TrackingSource.Manual, otherStart);
        Assert.False(await _store.RecordSoftwareUsageAsync(otherEntry.Id, "discord"));
        await _store.StopRunningTimerAsync(otherStart.AddHours(1));

        await _store.UpdateSoftwareAsync(
            discord.Id,
            project.Id,
            "Discord",
            isExcluded: false,
            tagIds: []);
        var secondStart = start.AddHours(4);
        var secondEntry = await _store.StartTimerAsync(project.Id, TrackingSource.Manual, secondStart);
        Assert.True(await _store.RecordSoftwareUsageAsync(secondEntry.Id, "discord"));
        await _store.StopRunningTimerAsync(secondStart.AddHours(1));

        var updatedDiscord = Assert.Single(
            await _store.GetProjectSoftwareAsync(project.Id),
            item => item.Software.Id == discord.Id);
        Assert.False(updatedDiscord.IsExcluded);
        Assert.Equal(1, updatedDiscord.Software.EntryCount);
    }

    [Fact]
    public async Task GlobalSoftwareTracksEveryProjectAndSharesTagsAndExclusion()
    {
        var client = await _store.AddClientAsync("Acme", "#112233");
        var firstProject = await _store.AddProjectAsync(client.Id, "Phoenix", "#445566");
        var secondProject = await _store.AddProjectAsync(client.Id, "Apollo", "#556677");
        var tag = await _store.GetOrAddTagAsync("recording");
        var software = await _store.AddSoftwareAsync(
            "obs64.exe",
            "OBS",
            SystemEntityIds.GlobalSoftwareScopeId,
            isExcluded: false,
            tagIds: [tag.Id]);

        var globalSetting = Assert.Single(
            await _store.GetProjectSoftwareAsync(),
            setting => setting.Software.Id == software.Id);
        Assert.True(globalSetting.IsGlobal);
        Assert.Equal(SystemEntityIds.GlobalSoftwareScopeId, globalSetting.ProjectId);
        Assert.Equal("All projects", globalSetting.ProjectName);
        Assert.Equal("recording", Assert.Single(globalSetting.Tags).Name);
        Assert.Equal(
            "recording",
            Assert.Single(await _store.GetSoftwareTagsByProcessAsync(firstProject.Id, "OBS64.EXE")).Name);
        Assert.Equal(
            "recording",
            Assert.Single(await _store.GetSoftwareTagsByProcessAsync(secondProject.Id, "obs64")).Name);

        var start = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        var firstEntry = await _store.StartTimerAsync(firstProject.Id, TrackingSource.Manual, start);
        Assert.True(await _store.RecordSoftwareUsageAsync(firstEntry.Id, "obs64"));
        await _store.StopRunningTimerAsync(start.AddHours(1));

        var secondStart = start.AddHours(2);
        var secondEntry = await _store.StartTimerAsync(secondProject.Id, TrackingSource.Manual, secondStart);
        Assert.True(await _store.RecordSoftwareUsageAsync(secondEntry.Id, "OBS64.EXE"));
        await _store.StopRunningTimerAsync(secondStart.AddHours(1));

        await _store.UpdateSoftwareAsync(
            software.Id,
            SystemEntityIds.GlobalSoftwareScopeId,
            "OBS Studio",
            isExcluded: true,
            tagIds: [tag.Id]);
        var excludedStart = start.AddHours(4);
        var excludedEntry = await _store.StartTimerAsync(firstProject.Id, TrackingSource.Manual, excludedStart);
        Assert.False(await _store.RecordSoftwareUsageAsync(excludedEntry.Id, "obs64"));
        await _store.StopRunningTimerAsync(excludedStart.AddHours(1));

        var entries = await _store.GetEntriesAsync(start.AddMinutes(-1), excludedStart.AddHours(2));
        Assert.Equal("OBS Studio", entries.Single(entry => entry.Id == firstEntry.Id).SoftwareLabels);
        Assert.Equal("OBS Studio", entries.Single(entry => entry.Id == secondEntry.Id).SoftwareLabels);
        Assert.Equal(string.Empty, entries.Single(entry => entry.Id == excludedEntry.Id).SoftwareLabels);
    }

    [Fact]
    public async Task MonthlyLogFilesAreSeparatedByLocalStartMonthAndStaySynchronized()
    {
        var client = await _store.AddClientAsync("Acme", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Phoenix", "#445566");
        var task = await _store.AddTaskAsync(project.Id, "Animation");
        var january = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var february = new DateTimeOffset(2026, 2, 16, 12, 0, 0, TimeSpan.Zero);

        await _store.AddManualEntryAsync(project.Id, task.Id, "January work #anim", january, january.AddHours(2));
        await _store.AddManualEntryAsync(project.Id, task.Id, "February work", february, february.AddHours(1), isPaid: true);

        var januaryPath = Path.Combine(_store.MonthlyLogDirectory, "TimeTracker-Logs-2026-01.csv");
        var februaryPath = Path.Combine(_store.MonthlyLogDirectory, "TimeTracker-Logs-2026-02.csv");
        Assert.True(File.Exists(januaryPath));
        Assert.True(File.Exists(februaryPath));
        Assert.Contains("January work #anim", await File.ReadAllTextAsync(januaryPath));
        Assert.DoesNotContain("February work", await File.ReadAllTextAsync(januaryPath));
        Assert.Contains("February work", await File.ReadAllTextAsync(februaryPath));

        await _store.RenameClientAsync(client.Id, "Renamed client");
        await _store.RenameTaskAsync(task.Id, "Rigging");
        var januaryText = await File.ReadAllTextAsync(januaryPath);
        Assert.Contains("Renamed client", januaryText);
        Assert.Contains("Rigging", januaryText);

        var januaryEntry = Assert.Single(await _store.GetEntriesAsync(january.AddDays(-1), january.AddDays(1)));
        var march = new DateTimeOffset(2026, 3, 17, 12, 0, 0, TimeSpan.Zero);
        await _store.UpdateTimeEntryAsync(
            januaryEntry.Id,
            project.Id,
            task.Id,
            januaryEntry.Description,
            march,
            march.AddHours(2));

        Assert.False(File.Exists(januaryPath));
        Assert.True(File.Exists(Path.Combine(_store.MonthlyLogDirectory, "TimeTracker-Logs-2026-03.csv")));
    }

    [Fact]
    public async Task DailyLogsKeepPastRevisionsAndDatabaseSafetySnapshots()
    {
        var client = await _store.AddClientAsync("Daily safety client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Daily safety project", "#445566");
        var task = await _store.AddTaskAsync(project.Id, "Original task");
        var start = new DateTimeOffset(2026, 1, 12, 9, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, task.Id, "Original description", start, start.AddHours(1));

        var dailyPath = Path.Combine(_store.DailyLogDirectory, "TimeTracker-Logs-2026-01-12.csv");
        Assert.True(File.Exists(dailyPath));
        Assert.Contains("Original task", await File.ReadAllTextAsync(dailyPath));

        await _store.RenameTaskAsync(task.Id, "Renamed task");
        Assert.Contains("Renamed task", await File.ReadAllTextAsync(dailyPath));
        var revisions = Directory.GetFiles(
            Path.Combine(_store.DailyLogDirectory, "Revisions", "2026-01-12"),
            "*.csv");
        Assert.NotEmpty(revisions);
        Assert.Contains(revisions, path => File.ReadAllText(path).Contains("Original task", StringComparison.Ordinal));

        var today = DateOnly.FromDateTime(DateTime.Today);
        var latestBackup = Path.Combine(
            _store.DailyBackupDirectory,
            $"TimeTracker-Backup-{today:yyyy-MM-dd}.db");
        var firstBackup = Path.Combine(
            _store.DailyBackupDirectory,
            $"TimeTracker-Backup-{today:yyyy-MM-dd}-first.db");
        Assert.True(File.Exists(latestBackup));
        Assert.True(File.Exists(firstBackup));

        var backupConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = latestBackup,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        await using var backup = new SqliteConnection(backupConnectionString);
        await backup.OpenAsync();
        await using var taskCommand = backup.CreateCommand();
        taskCommand.CommandText = "SELECT COUNT(*) FROM SavedTasks WHERE Name = 'Renamed task';";
        Assert.Equal(1L, (long)(await taskCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task GoogleSheetsExportModeStopsCreatingNewLocalCsvButKeepsDatabaseSnapshots()
    {
        var client = await _store.AddClientAsync("Cloud client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Cloud project", "#445566");
        await _store.SetSettingAsync(
            LogExportDestinationSettings.DestinationKey,
            LogExportDestinationSettings.GoogleSheets);
        var start = new DateTimeOffset(2025, 11, 3, 9, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "Cloud only export", start, start.AddHours(1));

        Assert.False(File.Exists(Path.Combine(
            _store.DailyLogDirectory,
            "TimeTracker-Logs-2025-11-03.csv")));
        Assert.False(File.Exists(Path.Combine(
            _store.MonthlyLogDirectory,
            "TimeTracker-Logs-2025-11.csv")));
        Assert.NotEmpty(Directory.GetFiles(_store.DailyBackupDirectory, "TimeTracker-Backup-*.db"));
    }

    [Fact]
    public async Task GoogleSheetsSyncCreatesDailyWorksheetsAndPreservesRemoteOnlyRows()
    {
        var client = await _store.AddClientAsync("Sheets client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Sheets project", "#445566");
        var start = new DateTimeOffset(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "Synced work", start, start.AddHours(1));
        var credentialStore = new FakeCredentialStore();
        var api = new FakeGoogleSheetsApiClient();
        await using var service = new GoogleSheetsSyncService(
            _store,
            api,
            new FakeGoogleAuthorizationBroker(),
            credentialStore,
            Guid.NewGuid(),
            "Work",
            new FixedClock(new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero)),
            TimeZoneInfo.Utc);

        var connection = await service.ConnectAsync("desktop-client", "desktop-secret");
        Assert.Equal("person@example.com", connection.Email);
        Assert.NotNull(credentialStore.GoogleCredentials);

        var result = await service.SyncNowAsync();
        Assert.Equal(1, result.WorksheetCount);
        Assert.Equal(1, api.CreateCalls);
        Assert.Equal(1, api.AddBatchCalls);
        Assert.Equal(1, api.ReadBatchCalls);
        Assert.Equal(1, api.WriteBatchCalls);
        Assert.Contains("2026-05-04", api.Worksheets);
        var rows = api.Written["2026-05-04"];
        Assert.Contains(rows, row => row.Count > 0 && Equals(row[0], FakeGoogleSheetsApiClient.RemoteOnlyEntryId));
        Assert.Contains(rows, row => row.Count > 8 && Equals(row[8], "Synced work"));
        Assert.Equal(
            LogExportDestinationSettings.GoogleSheets,
            await _store.GetSettingAsync(LogExportDestinationSettings.DestinationKey));
    }

    [Fact]
    public async Task GoogleSheetsSyncRetainsCreatedSpreadsheetAcrossPartialFailure()
    {
        var client = await _store.AddClientAsync("Retry client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Retry project", "#445566");
        var start = new DateTimeOffset(2026, 5, 5, 9, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "Retry work", start, start.AddHours(1));
        var api = new FakeGoogleSheetsApiClient { FailNextWrite = true };
        await using var service = new GoogleSheetsSyncService(
            _store,
            api,
            new FakeGoogleAuthorizationBroker(),
            new FakeCredentialStore(),
            Guid.NewGuid(),
            "Work",
            new FixedClock(new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero)),
            TimeZoneInfo.Utc);

        await service.ConnectAsync("desktop-client", "desktop-secret");
        await Assert.ThrowsAsync<HttpRequestException>(() => service.SyncNowAsync());
        Assert.Equal("sheet-1", (await service.GetConnectionAsync())?.SpreadsheetId);

        await service.SyncNowAsync();

        Assert.Equal(1, api.CreateCalls);
        Assert.Equal(2, api.WriteBatchCalls);
    }

    [Fact]
    public async Task GoogleSheetsSyncRemovesExplicitlyDeletedEntryButKeepsRecoveryRows()
    {
        var client = await _store.AddClientAsync("Mirror client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Mirror project", "#445566");
        var start = new DateTimeOffset(2026, 5, 6, 9, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "Delete from mirror", start, start.AddHours(1));
        var entry = Assert.Single(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(2)));
        var api = new FakeGoogleSheetsApiClient();
        await using var service = new GoogleSheetsSyncService(
            _store,
            api,
            new FakeGoogleAuthorizationBroker(),
            new FakeCredentialStore(),
            Guid.NewGuid(),
            "Work",
            new FixedClock(new DateTimeOffset(2026, 5, 6, 12, 0, 0, TimeSpan.Zero)),
            TimeZoneInfo.Utc);

        await service.ConnectAsync("desktop-client", "desktop-secret");
        await service.SyncNowAsync();
        Assert.Contains(api.Written["2026-05-06"], row => Equals(row[0], entry.Id.ToString("D")));

        await _store.DeleteTimeEntryAsync(entry.Id);
        Assert.Contains(entry.Id, await _store.GetGoogleSheetsEntryDeletionIdsAsync());
        await service.SyncNowAsync();

        var rows = api.Written["2026-05-06"];
        Assert.DoesNotContain(rows, row => Equals(row[0], entry.Id.ToString("D")));
        Assert.Contains(rows, row => Equals(row[0], FakeGoogleSheetsApiClient.RemoteOnlyEntryId));
        Assert.Empty(await _store.GetGoogleSheetsEntryDeletionIdsAsync());
    }

    [Fact]
    public async Task LegacyDatabaseCopyIsIntegrityCheckedAndProducesMonthlyFiles()
    {
        var sourceDirectory = Path.Combine(_directory, "source");
        var targetDirectory = Path.Combine(_directory, "target");
        var sourcePath = Path.Combine(sourceDirectory, "tracker.db");
        var targetPath = Path.Combine(targetDirectory, "TimeTracker.db");
        Directory.CreateDirectory(sourceDirectory);

        await using (var sourceStore = new SqliteTrackerStore(sourcePath, sourceDirectory, TimeZoneInfo.Utc))
        {
            await sourceStore.InitializeAsync();
            var client = await sourceStore.AddClientAsync("Legacy client", "#112233");
            var project = await sourceStore.AddProjectAsync(client.Id, "Legacy project", "#445566");
            var start = new DateTimeOffset(2026, 4, 10, 9, 0, 0, TimeSpan.Zero);
            await sourceStore.AddManualEntryAsync(project.Id, null, "Migrated entry", start, start.AddHours(1));
        }

        await SqliteDatabaseMigrator.CopyIfTargetMissingAsync(sourcePath, targetPath);

        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(targetPath));
        await using var targetStore = new SqliteTrackerStore(targetPath, targetDirectory, TimeZoneInfo.Utc);
        await targetStore.InitializeAsync();
        Assert.Equal("Legacy client", Assert.Single(await targetStore.GetClientsAsync()).Name);
        Assert.Equal("Migrated entry", Assert.Single(await targetStore.GetEntriesAsync(
            new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero))).Description);
        Assert.True(File.Exists(Path.Combine(targetDirectory, "TimeTracker-Logs-2026-04.csv")));
    }

    [Fact]
    public async Task LatestEntryStartReturnsMostRecentHistoryTimestamp()
    {
        var client = await _store.AddClientAsync("Latest history client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Latest history project", "#445566");
        var first = new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero);
        var latest = new DateTimeOffset(2026, 7, 31, 14, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(project.Id, null, "Older", first, first.AddHours(1));
        await _store.AddManualEntryAsync(project.Id, null, "Latest", latest, latest.AddHours(1));

        Assert.Equal(latest, await _store.GetLatestEntryStartUtcAsync());
    }

    [Fact]
    public async Task TrelloReconciliationAllowsDuplicateRemoteNamesAndPreservesTimedCards()
    {
        var client = await _store.AddClientAsync("Trello client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Trello project", "#445566");
        var mapping = new TrelloBoardMapping(
            Guid.NewGuid(),
            project.Id,
            "board-1",
            "Production",
            [new TrelloListMapping("list-active", "Active")]);
        await _store.UpsertTrelloBoardMappingAsync(mapping);
        var firstCard = CreateTrelloCard("card-1", "Shared name");
        var secondCard = CreateTrelloCard("card-2", "Shared name");

        var imported = await _store.ReconcileTrelloBoardAsync(
            mapping.Id,
            [firstCard, secondCard],
            DateTimeOffset.UtcNow);

        Assert.Equal(2, imported.ImportedCount);
        var linkedTasks = await _store.GetTasksAsync(project.Id);
        Assert.Equal(2, linkedTasks.Count);
        Assert.All(linkedTasks, task => Assert.Equal(SavedTaskOrigin.Trello, task.Origin));
        var local = await _store.AddTaskAsync(project.Id, "Shared name");
        await Assert.ThrowsAsync<SqliteException>(() => _store.AddTaskAsync(project.Id, "SHARED NAME"));

        var firstLink = Assert.Single(
            await _store.GetExternalTaskLinksAsync(),
            link => link.ExternalId == firstCard.Id);
        var start = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        await _store.AddManualEntryAsync(
            project.Id,
            firstLink.TaskId,
            "Timed Trello card",
            start,
            start.AddHours(1));

        var removed = await _store.ReconcileTrelloBoardAsync(
            mapping.Id,
            [],
            start.AddHours(2));

        Assert.Equal(1, removed.DetachedCount);
        Assert.Equal(1, removed.DeletedCount);
        var afterRemoval = await _store.GetTasksAsync(project.Id);
        Assert.Contains(afterRemoval, task => task.Id == local.Id && task.Origin == SavedTaskOrigin.Local);
        var detached = Assert.Single(afterRemoval, task => task.Id == firstLink.TaskId);
        Assert.Equal(SavedTaskOrigin.TrelloDetached, detached.Origin);

        var renamedCard = firstCard with { Name = "Renamed remotely" };
        await _store.ReconcileTrelloBoardAsync(mapping.Id, [renamedCard], start.AddHours(3));

        var relinked = Assert.Single(await _store.GetTasksAsync(project.Id), task => task.Id == firstLink.TaskId);
        Assert.Equal("Renamed remotely", relinked.Name);
        Assert.Equal(SavedTaskOrigin.Trello, relinked.Origin);
        Assert.Single(await _store.GetEntriesAsync(start.AddMinutes(-1), start.AddHours(2)));
    }

    [Fact]
    public async Task RemovingLinkedTaskSuppressesItAcrossLaterSynchronization()
    {
        var client = await _store.AddClientAsync("Suppress client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Suppress project", "#445566");
        var mapping = new TrelloBoardMapping(
            Guid.NewGuid(),
            project.Id,
            "board-suppress",
            "Suppression",
            [new TrelloListMapping("list-active", "Active")]);
        await _store.UpsertTrelloBoardMappingAsync(mapping);
        var card = CreateTrelloCard("card-suppress", "Do not recreate");
        await _store.ReconcileTrelloBoardAsync(mapping.Id, [card], DateTimeOffset.UtcNow);
        var task = Assert.Single(await _store.GetTasksAsync(project.Id));

        await _store.ArchiveTaskAsync(task.Id);
        await _store.ReconcileTrelloBoardAsync(mapping.Id, [card], DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Empty(await _store.GetTasksAsync(project.Id));
        var archived = Assert.Single(await _store.GetTasksAsync(project.Id, includeArchived: true));
        Assert.True(archived.IsArchived);
        Assert.Equal(SavedTaskOrigin.TrelloDetached, archived.Origin);
        Assert.Equal(
            ExternalTaskLinkState.Suppressed,
            Assert.Single(await _store.GetExternalTaskLinksAsync()).State);
    }

    [Fact]
    public async Task TrelloSyncImportsOnlyConnectedMembersCardsFromSelectedLists()
    {
        var client = await _store.AddClientAsync("Filter client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Filter project", "#445566");
        await _store.SaveTrelloConnectionAsync(new TrelloConnection("member-me", "me", "Me"));
        var mapping = new TrelloBoardMapping(
            Guid.NewGuid(),
            project.Id,
            "board-filter",
            "Filtering",
            [new TrelloListMapping("selected", "Selected")]);
        await _store.UpsertTrelloBoardMappingAsync(mapping);
        var api = new FakeTrelloApiClient(
        [
            CreateTrelloCard("included", "Included", "selected", ["member-me"]),
            CreateTrelloCard("other-member", "Other member", "selected", ["member-other"]),
            CreateTrelloCard("other-list", "Other list", "ignored", ["member-me"]),
        ]);
        var credentials = new FakeCredentialStore(new TrelloCredentials("key", "token"));
        await using var service = new TrelloSyncService(
            _store,
            api,
            credentials,
            Guid.NewGuid(),
            new FixedClock(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero)));

        var result = await service.SyncNowAsync();

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal("Included", Assert.Single(await _store.GetTasksAsync(project.Id)).Name);
    }

    [Fact]
    public async Task FailedBoardFetchDoesNotRemovePreviouslySynchronizedTasks()
    {
        var client = await _store.AddClientAsync("Offline client", "#112233");
        var project = await _store.AddProjectAsync(client.Id, "Offline project", "#445566");
        await _store.SaveTrelloConnectionAsync(new TrelloConnection("member-me", "me", "Me"));
        var mapping = new TrelloBoardMapping(
            Guid.NewGuid(),
            project.Id,
            "board-1",
            "Offline board",
            [new TrelloListMapping("list-active", "Active")]);
        await _store.UpsertTrelloBoardMappingAsync(mapping);
        await _store.ReconcileTrelloBoardAsync(
            mapping.Id,
            [CreateTrelloCard("offline-card", "Available offline")],
            DateTimeOffset.UtcNow);
        await using var service = new TrelloSyncService(
            _store,
            new FakeTrelloApiClient([], new HttpRequestException("Network unavailable")),
            new FakeCredentialStore(new TrelloCredentials("key", "token")),
            Guid.NewGuid(),
            new FixedClock(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SyncNowAsync());

        var task = Assert.Single(await _store.GetTasksAsync(project.Id));
        Assert.Equal("Available offline", task.Name);
        Assert.True(task.IsTrelloLinked);
    }

    [Fact]
    public async Task VersionTwentyMigrationPreservesTasksAndEntriesAndAddsTaskOrigins()
    {
        var migrationDirectory = Path.Combine(_directory, "trello-migration");
        var databasePath = Path.Combine(migrationDirectory, "TimeTracker.db");
        Directory.CreateDirectory(migrationDirectory);
        Guid taskId;
        var start = new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
        await using (var original = new SqliteTrackerStore(databasePath, migrationDirectory))
        {
            await original.InitializeAsync();
            var client = await original.AddClientAsync("Migration client", "#112233");
            var project = await original.AddProjectAsync(client.Id, "Migration project", "#445566");
            var task = await original.AddTaskAsync(project.Id, "Preserved task");
            taskId = task.Id;
            await original.AddManualEntryAsync(project.Id, task.Id, "Preserved entry", start, start.AddHours(1));
        }

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA foreign_keys = OFF;
                DROP INDEX UX_SavedTasks_LocalName;
                CREATE TABLE SavedTasksV20 (
                    Id TEXT PRIMARY KEY,
                    ProjectId TEXT NOT NULL REFERENCES Projects(Id),
                    Name TEXT NOT NULL COLLATE NOCASE,
                    IsArchived INTEGER NOT NULL DEFAULT 0 CHECK (IsArchived IN (0, 1)),
                    UNIQUE (ProjectId, Name)
                );
                INSERT INTO SavedTasksV20 (Id, ProjectId, Name, IsArchived)
                SELECT Id, ProjectId, Name, IsArchived FROM SavedTasks;
                DROP TABLE SavedTasks;
                ALTER TABLE SavedTasksV20 RENAME TO SavedTasks;
                PRAGMA user_version = 20;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var migrated = new SqliteTrackerStore(databasePath, migrationDirectory);
        await migrated.InitializeAsync();

        var taskAfterMigration = Assert.Single(await migrated.GetTasksAsync());
        Assert.Equal(taskId, taskAfterMigration.Id);
        Assert.Equal(SavedTaskOrigin.Local, taskAfterMigration.Origin);
        Assert.Equal("Preserved entry", Assert.Single(await migrated.GetEntriesAsync(
            start.AddMinutes(-1),
            start.AddHours(2))).Description);
        Assert.Single(Directory.GetFiles(migrationDirectory, "TimeTracker.db.backup-v20-*"));
        await using var versionConnection = new SqliteConnection($"Data Source={databasePath}");
        await versionConnection.OpenAsync();
        await using var versionCommand = versionConnection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(23L, (long)(await versionCommand.ExecuteScalarAsync())!);
    }

    private static TrelloCard CreateTrelloCard(
        string id,
        string name,
        string listId = "list-active",
        IReadOnlyList<string>? memberIds = null) =>
        new(
            id,
            id.StartsWith("card-suppress", StringComparison.Ordinal) ? "board-suppress" :
            id is "included" or "other-member" or "other-list" ? "board-filter" : "board-1",
            listId,
            name,
            $"https://trello.com/c/{id}",
            memberIds ?? ["member-me"],
            DateTimeOffset.UtcNow);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
        public double MonotonicSeconds => 0;
    }

    private sealed class FakeCredentialStore(TrelloCredentials? credentials = null) : ICredentialStore
    {
        private TrelloCredentials? _credentials = credentials;
        public GoogleSheetsCredentials? GoogleCredentials { get; private set; }
        public Task<TrelloCredentials?> GetTrelloCredentialsAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_credentials);
        public Task SetTrelloCredentialsAsync(Guid profileId, TrelloCredentials value, CancellationToken cancellationToken = default)
        {
            _credentials = value;
            return Task.CompletedTask;
        }
        public Task DeleteTrelloCredentialsAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            _credentials = null;
            return Task.CompletedTask;
        }
        public Task<GoogleSheetsCredentials?> GetGoogleSheetsCredentialsAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GoogleCredentials);
        public Task SetGoogleSheetsCredentialsAsync(Guid profileId, GoogleSheetsCredentials value, CancellationToken cancellationToken = default)
        {
            GoogleCredentials = value;
            return Task.CompletedTask;
        }
        public Task DeleteGoogleSheetsCredentialsAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            GoogleCredentials = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGoogleAuthorizationBroker : IGoogleAuthorizationBroker
    {
        public Task<GoogleOAuthAuthorizationCode> AuthorizeAsync(string clientId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GoogleOAuthAuthorizationCode("code", "http://127.0.0.1/callback", "verifier"));
    }

    private sealed class FakeGoogleSheetsApiClient : IGoogleSheetsApiClient
    {
        public const string RemoteOnlyEntryId = "11111111-1111-1111-1111-111111111111";
        public HashSet<string> Worksheets { get; } = ["About"];
        public Dictionary<string, IReadOnlyList<IReadOnlyList<object?>>> Written { get; } = [];
        public int CreateCalls { get; private set; }
        public int AddBatchCalls { get; private set; }
        public int ReadBatchCalls { get; private set; }
        public int WriteBatchCalls { get; private set; }
        public bool FailNextWrite { get; set; }

        public Task<GoogleOAuthTokens> ExchangeAuthorizationCodeAsync(string clientId, string clientSecret, GoogleOAuthAuthorizationCode authorization, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GoogleOAuthTokens("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));
        public Task<GoogleOAuthTokens> RefreshAccessTokenAsync(GoogleSheetsCredentials credentials, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GoogleOAuthTokens("access", null, DateTimeOffset.UtcNow.AddHours(1)));
        public Task<GoogleUser> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GoogleUser("person@example.com", "Person"));
        public Task<GoogleSpreadsheet> CreateSpreadsheetAsync(string accessToken, string title, CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return Task.FromResult(new GoogleSpreadsheet("sheet-1", "https://docs.google.com/spreadsheets/d/sheet-1/edit"));
        }
        public Task<IReadOnlyList<string>> GetWorksheetNamesAsync(string accessToken, string spreadsheetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Worksheets.ToArray());
        public Task<IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>>> ReadWorksheetsAsync(
            string accessToken,
            string spreadsheetId,
            IReadOnlyList<string> worksheetNames,
            CancellationToken cancellationToken = default)
        {
            ReadBatchCalls++;
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>>>(
                worksheetNames.ToDictionary(
                    worksheetName => worksheetName,
                    worksheetName => Written.TryGetValue(worksheetName, out var written)
                        ? (IReadOnlyList<IReadOnlyList<string>>)written
                            .Select(row => (IReadOnlyList<string>)row
                                .Select(value => value?.ToString() ?? string.Empty)
                                .ToArray())
                            .ToArray()
                        :
                        [
                            ["EntryId", "Date", "Start"],
                            [RemoteOnlyEntryId, worksheetName, worksheetName + " 08:00:00 +00:00"],
                        ],
                    StringComparer.Ordinal));
        }
        public Task AddWorksheetsAsync(
            string accessToken,
            string spreadsheetId,
            IReadOnlyList<string> worksheetNames,
            CancellationToken cancellationToken = default)
        {
            AddBatchCalls++;
            Worksheets.UnionWith(worksheetNames);
            return Task.CompletedTask;
        }
        public Task WriteWorksheetsAsync(
            string accessToken,
            string spreadsheetId,
            IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>> worksheets,
            CancellationToken cancellationToken = default)
        {
            WriteBatchCalls++;
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new HttpRequestException("Simulated write failure.");
            }

            foreach (var worksheet in worksheets)
            {
                Written[worksheet.Key] = worksheet.Value;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeTrelloApiClient(
        IReadOnlyList<TrelloCard> cards,
        Exception? cardsError = null) : ITrelloApiClient
    {
        public Uri CreateAuthorizationUri(string apiKey) => new("https://trello.com/1/authorize");
        public Task<TrelloMember> GetCurrentMemberAsync(TrelloCredentials credentials, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TrelloMember("member-me", "me", "Me"));
        public Task<IReadOnlyList<TrelloBoard>> GetBoardsAsync(TrelloCredentials credentials, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrelloBoard>>([new TrelloBoard("board-filter", "Filtering", string.Empty)]);
        public Task<IReadOnlyList<TrelloList>> GetListsAsync(TrelloCredentials credentials, string boardId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrelloList>>([new TrelloList("selected", boardId, "Selected")]);
        public Task<IReadOnlyList<TrelloCard>> GetCardsAsync(TrelloCredentials credentials, string boardId, CancellationToken cancellationToken = default) =>
            cardsError is null
                ? Task.FromResult(cards)
                : Task.FromException<IReadOnlyList<TrelloCard>>(cardsError);
    }

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private async Task<(Project First, Project Second)> CreateTwoProjectsAsync()
    {
        var client = await _store.AddClientAsync("Acme", "#112233");
        var first = await _store.AddProjectAsync(client.Id, "Phoenix", "#445566");
        var second = await _store.AddProjectAsync(client.Id, "Orion", "#778899");
        return (first, second);
    }
}
