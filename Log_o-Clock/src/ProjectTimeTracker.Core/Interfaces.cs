namespace ProjectTimeTracker.Core;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    double MonotonicSeconds { get; }
}

public interface IForegroundActivityMonitor : IDisposable
{
    event EventHandler<WindowActivity>? ActivityChanged;
    WindowActivity? GetCurrentActivity();
    void Start();
}

public interface IUserIdleMonitor : IDisposable
{
    event EventHandler<DateTimeOffset>? IdleStarted;
    event EventHandler<DateTimeOffset>? ActivityResumed;
    bool IsIdle { get; }
    void Start();
}

public interface IIdleProtectionMonitor : IDisposable
{
    event EventHandler<IdleProtectionState>? StateChanged;
    IdleProtectionState CurrentState { get; }
    void Configure(bool callsEnabled, bool videoEnabled);
    void Start();
}

public interface ISystemSessionMonitor : IDisposable
{
    event EventHandler<SystemSessionEvent>? SessionChanged;
    void Start();
}

public interface INotificationService : IDisposable
{
    Task<ReminderResponse> ShowProjectReminderAsync(
        RecognitionCandidate candidate,
        IReadOnlyList<SavedTask> projectTasks,
        IReadOnlyList<TagDefinition> correlatedTags,
        IReadOnlyList<TagDefinition> availableTags,
        bool isProjectSwitch = false,
        Guid? suggestedTaskId = null,
        string? suggestedTaskName = null,
        nint targetWindowHandle = default,
        CancellationToken cancellationToken = default);

    Task<RecognitionCandidate?> ShowAmbiguousReminderAsync(
        IReadOnlyList<RecognitionCandidate> candidates,
        CancellationToken cancellationToken = default);

    Task ShowTargetReviewAsync(
        IReadOnlyList<TargetReviewItem> items,
        CancellationToken cancellationToken = default);

    Task ShowBreakReminderAsync(
        BreakReminderPlacement placement,
        string message,
        CancellationToken cancellationToken = default);

    void DismissActive();
}

public interface IAutostartService
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public interface ICredentialStore
{
    Task<TrelloCredentials?> GetTrelloCredentialsAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task SetTrelloCredentialsAsync(Guid profileId, TrelloCredentials credentials, CancellationToken cancellationToken = default);
    Task DeleteTrelloCredentialsAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<GoogleSheetsCredentials?> GetGoogleSheetsCredentialsAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task SetGoogleSheetsCredentialsAsync(Guid profileId, GoogleSheetsCredentials credentials, CancellationToken cancellationToken = default);
    Task DeleteGoogleSheetsCredentialsAsync(Guid profileId, CancellationToken cancellationToken = default);
}

public interface IGoogleAuthorizationBroker
{
    Task<GoogleOAuthAuthorizationCode> AuthorizeAsync(
        string clientId,
        CancellationToken cancellationToken = default);
}

public interface IGoogleSheetsApiClient
{
    Task<GoogleOAuthTokens> ExchangeAuthorizationCodeAsync(
        string clientId,
        string clientSecret,
        GoogleOAuthAuthorizationCode authorization,
        CancellationToken cancellationToken = default);
    Task<GoogleOAuthTokens> RefreshAccessTokenAsync(
        GoogleSheetsCredentials credentials,
        CancellationToken cancellationToken = default);
    Task<GoogleUser> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken = default);
    Task<GoogleSpreadsheet> CreateSpreadsheetAsync(string accessToken, string title, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetWorksheetNamesAsync(string accessToken, string spreadsheetId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>>> ReadWorksheetsAsync(
        string accessToken,
        string spreadsheetId,
        IReadOnlyList<string> worksheetNames,
        CancellationToken cancellationToken = default);
    Task AddWorksheetsAsync(
        string accessToken,
        string spreadsheetId,
        IReadOnlyList<string> worksheetNames,
        CancellationToken cancellationToken = default);
    Task WriteWorksheetsAsync(
        string accessToken,
        string spreadsheetId,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>> worksheets,
        CancellationToken cancellationToken = default);
    Task AddHiddenWorksheetsAsync(
        string accessToken,
        string spreadsheetId,
        IReadOnlyList<string> worksheetNames,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IReadOnlyList<string>>> ReadRangeAsync(
        string accessToken,
        string spreadsheetId,
        string range,
        CancellationToken cancellationToken = default);
    Task AppendRowsAsync(
        string accessToken,
        string spreadsheetId,
        string range,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        CancellationToken cancellationToken = default);
    Task WriteRangeAsync(
        string accessToken,
        string spreadsheetId,
        string range,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        CancellationToken cancellationToken = default);
}

public interface IGoogleSheetsSyncService : IAsyncDisposable
{
    event EventHandler<GoogleSheetsSyncResult>? SyncCompleted;
    Task<GoogleSheetsConnection?> GetConnectionAsync(CancellationToken cancellationToken = default);
    Task<GoogleSheetsConnection> ConnectAsync(string clientId, string clientSecret, CancellationToken cancellationToken = default);
    Task<GoogleSheetsConnection> ConnectExistingAsync(string clientId, string clientSecret, string spreadsheetUrlOrId, CancellationToken cancellationToken = default);
    Task SetCloudExportEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task SetDeviceNameAsync(string deviceName, CancellationToken cancellationToken = default);
    Task SetProfileNameAsync(string profileName, CancellationToken cancellationToken = default);
    Task SetPinnedTimeZoneAsync(string timeZoneId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProfileSyncConflict>> GetConflictsAsync(CancellationToken cancellationToken = default);
    Task ResolveConflictAsync(Guid conflictId, ProfileSyncResolution resolution, Guid? cloudRevisionId = null, CancellationToken cancellationToken = default);
    IReadOnlyList<RemoteTimerStatus> RemoteTimers { get; }
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<GoogleSheetsSyncResult> SyncNowAsync(CancellationToken cancellationToken = default);
    void QueueSync();
    void Start();
}

public interface ITrelloApiClient
{
    Uri CreateAuthorizationUri(string apiKey);
    Task<TrelloMember> GetCurrentMemberAsync(TrelloCredentials credentials, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrelloBoard>> GetBoardsAsync(TrelloCredentials credentials, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrelloList>> GetListsAsync(TrelloCredentials credentials, string boardId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrelloCard>> GetCardsAsync(TrelloCredentials credentials, string boardId, CancellationToken cancellationToken = default);
}

public interface IGitHubReleaseClient
{
    Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default);
}

public interface ITrelloSyncService : IAsyncDisposable
{
    event EventHandler<TrelloSyncResult>? SyncCompleted;
    Uri CreateAuthorizationUri(string apiKey);
    Task<TrelloConnection?> GetConnectionAsync(CancellationToken cancellationToken = default);
    Task<TrelloMember> ConnectAsync(string apiKey, string token, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrelloBoard>> GetBoardsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrelloList>> GetListsAsync(string boardId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrelloBoardMapping>> GetMappingsAsync(CancellationToken cancellationToken = default);
    Task SaveMappingAsync(TrelloBoardMapping mapping, CancellationToken cancellationToken = default);
    Task RemoveMappingAsync(Guid mappingId, CancellationToken cancellationToken = default);
    Task<TrelloSyncResult> SyncNowAsync(CancellationToken cancellationToken = default);
    void Start();
}

public interface IUpdateSettingsStore
{
    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default);
    Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default);
}

public interface ITrackerStore : IAsyncDisposable, IUpdateSettingsStore
{
    string DatabasePath { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RecoverInterruptedTimerAsync(DateTimeOffset recoveredAtUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Client>> GetClientsAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Project>> GetProjectsAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectTargetDebt>> GetProjectTargetDebtsAsync(DateTimeOffset nowUtc, TimeZoneInfo timeZone, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectTargetDebtCancellation>> GetProjectTargetDebtCancellationsAsync(Guid? projectId = null, bool includeRestored = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectWorkSummary>> GetProjectWorkSummariesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SavedTask>> GetTasksAsync(Guid? projectId = null, bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskWorkSummary>> GetTaskWorkSummariesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TagDefinition>> GetTagsAsync(Guid? projectId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TagSummary>> GetTagSummariesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SoftwareDefinition>> GetSoftwareAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectSoftwareDefinition>> GetProjectSoftwareAsync(Guid? projectId = null, bool includeFrozen = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecognitionRule>> GetRulesAsync(Guid? projectId = null, bool includeFrozen = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecognitionCandidate>> GetRecognitionCandidatesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectOption>> GetProjectOptionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CustomTarget>> GetCustomTargetsAsync(CancellationToken cancellationToken = default);
    Task<TrelloConnection?> GetTrelloConnectionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrelloBoardMapping>> GetTrelloBoardMappingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExternalTaskLink>> GetExternalTaskLinksAsync(CancellationToken cancellationToken = default);

    Task<Client> AddClientAsync(string name, string color, CancellationToken cancellationToken = default);
    Task<Project> AddProjectAsync(Guid clientId, string name, string color, CancellationToken cancellationToken = default);
    Task<SavedTask> AddTaskAsync(Guid projectId, string name, CancellationToken cancellationToken = default);
    Task<SavedTask> GetOrAddTaskAsync(
        Guid projectId,
        string name,
        SavedTaskOrigin origin = SavedTaskOrigin.Local,
        CancellationToken cancellationToken = default);
    Task<TagDefinition> GetOrAddTagAsync(string name, Guid? projectId = null, CancellationToken cancellationToken = default);
    Task<TagDefinition> AddTagAsync(string name, string color, Guid? projectId = null, CancellationToken cancellationToken = default);
    Task<SoftwareDefinition> AddSoftwareAsync(string processName, string label, Guid projectId, bool isExcluded, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken = default);
    Task<RecognitionRule> AddRuleAsync(Guid projectId, string titlePattern, string? processName, CancellationToken cancellationToken = default);
    Task<CustomTarget> AddCustomTargetAsync(
        string name,
        Guid? projectId,
        CustomTargetCadence cadence,
        double targetHours,
        TargetDurationMetric durationMetric = TargetDurationMetric.ActiveTime,
        CancellationToken cancellationToken = default);
    Task SetCustomTargetCompletionAsync(
        Guid targetId,
        DateTimeOffset? completedUtc,
        CancellationToken cancellationToken = default);
    Task<ProjectTargetDebtCancellation> CancelProjectTargetDebtAsync(Guid projectId, long canceledSeconds, DateTimeOffset canceledUtc, CancellationToken cancellationToken = default);
    Task RestoreProjectTargetDebtAsync(Guid projectId, DateTimeOffset restoredUtc, CancellationToken cancellationToken = default);
    Task RenameClientAsync(Guid clientId, string name, CancellationToken cancellationToken = default);
    Task RenameProjectAsync(Guid projectId, string name, CancellationToken cancellationToken = default);
    Task SetProjectFrozenAsync(Guid projectId, bool isFrozen, CancellationToken cancellationToken = default);
    Task UpdateProjectColorAsync(Guid projectId, string color, CancellationToken cancellationToken = default);
    Task UpdateProjectSettingsAsync(Guid projectId, double? dailyTargetHours, double? weeklyTargetHours, double? monthlyTargetHours, decimal? hourlyRate, string currency, bool? carryOverTargetDebtEnabled = null, CancellationToken cancellationToken = default);
    Task UpdateProjectSettingsAsync(Guid projectId, Guid clientId, double? dailyTargetHours, double? weeklyTargetHours, double? monthlyTargetHours, decimal? hourlyRate, string currency, bool? carryOverTargetDebtEnabled = null, CancellationToken cancellationToken = default);
    Task RenameTaskAsync(Guid taskId, string name, CancellationToken cancellationToken = default);
    Task RenameTagAsync(Guid tagId, string name, CancellationToken cancellationToken = default);
    Task UpdateTagColorAsync(Guid tagId, string color, CancellationToken cancellationToken = default);
    Task UpdateTagAsync(Guid tagId, string name, string color, Guid? projectId, CancellationToken cancellationToken = default);
    Task DeleteTagAsync(Guid tagId, CancellationToken cancellationToken = default);
    Task RenameSoftwareAsync(Guid softwareId, string label, CancellationToken cancellationToken = default);
    Task UpdateSoftwareAsync(Guid softwareId, Guid projectId, string label, bool isExcluded, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken = default);
    Task RemoveSoftwareFromListAsync(Guid softwareId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TagDefinition>> GetSoftwareTagsByProcessAsync(Guid projectId, string processName, CancellationToken cancellationToken = default);
    Task BulkUpdateProjectsAsync(IReadOnlyCollection<Guid> projectIds, ProjectBulkEdit edit, CancellationToken cancellationToken = default);
    Task BulkUpdateTasksAsync(IReadOnlyCollection<Guid> taskIds, TaskBulkEdit edit, CancellationToken cancellationToken = default);
    Task BulkUpdateTagsAsync(IReadOnlyCollection<Guid> tagIds, TagBulkEdit edit, CancellationToken cancellationToken = default);
    Task BulkUpdateRulesAsync(IReadOnlyCollection<Guid> ruleIds, RecognitionRuleBulkEdit edit, CancellationToken cancellationToken = default);
    Task UpdateRuleAsync(Guid ruleId, Guid projectId, string titlePattern, string? processName, CancellationToken cancellationToken = default);
    Task UpdateCustomTargetAsync(
        Guid targetId,
        string name,
        Guid? projectId,
        CustomTargetCadence cadence,
        double targetHours,
        TargetDurationMetric durationMetric = TargetDurationMetric.ActiveTime,
        CancellationToken cancellationToken = default);
    Task ReplaceProjectTargetsAsync(
        Guid projectId,
        IReadOnlyList<ProjectTargetInput> targets,
        CancellationToken cancellationToken = default);
    Task UpdateProjectDetailsAsync(
        Guid projectId,
        Guid clientId,
        decimal? hourlyRate,
        string currency,
        bool carryOverTargetDebtEnabled,
        CancellationToken cancellationToken = default);
    Task ArchiveClientAsync(Guid clientId, CancellationToken cancellationToken = default);
    Task ArchiveProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task ArchiveTaskAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default);
    Task DeleteCustomTargetAsync(Guid targetId, CancellationToken cancellationToken = default);
    Task SaveTrelloConnectionAsync(TrelloConnection connection, CancellationToken cancellationToken = default);
    Task UpdateTrelloSyncStatusAsync(DateTimeOffset? successfulUtc, string? error, bool requiresReconnect, CancellationToken cancellationToken = default);
    Task ClearTrelloConnectionAsync(CancellationToken cancellationToken = default);
    Task UpsertTrelloBoardMappingAsync(TrelloBoardMapping mapping, CancellationToken cancellationToken = default);
    Task RemoveTrelloBoardMappingAsync(Guid mappingId, CancellationToken cancellationToken = default);
    Task<TrelloSyncResult> ReconcileTrelloBoardAsync(Guid mappingId, IReadOnlyList<TrelloCard> cards, DateTimeOffset completedUtc, CancellationToken cancellationToken = default);
    Task SuppressExternalTaskAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<TimeEntry?> GetRunningEntryAsync(CancellationToken cancellationToken = default);
    Task<TimeEntry?> GetTimeEntryAsync(Guid entryId, CancellationToken cancellationToken = default);
    Task<long> GetEntryExcludedSecondsAsync(Guid entryId, CancellationToken cancellationToken = default);
    Task<TimeEntry> StartTimerAsync(Guid projectId, TrackingSource source, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<TimerStartResult> StartOrResumeTimerAsync(
        Guid projectId,
        Guid? taskId,
        string? description,
        TrackingSource source,
        DateTimeOffset nowUtc,
        TimeSpan maximumGap,
        CancellationToken cancellationToken = default);
    Task<TimeEntry> SplitRunningTimerAsync(Guid entryId, Guid? taskId, string? description, DateTimeOffset nowUtc, CancellationToken cancellationToken = default, bool? isCall = null);
    Task<TimeEntry> SwitchRunningTimerAsync(Guid entryId, Guid projectId, Guid? taskId, string? description, TrackingSource source, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<TimeEntry?> StopRunningTimerAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<bool> CancelRunningTimerAsync(Guid entryId, CancellationToken cancellationToken = default);
    Task CheckpointRunningTimerAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<TimeEntry> UpdateRunningEntryStartAsync(Guid entryId, DateTimeOffset startUtc, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task UpdateEntryDetailsAsync(Guid entryId, Guid? taskId, string? description, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task UpdateEntryAssignmentAsync(Guid entryId, Guid projectId, Guid? taskId, string? description, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<bool> RecordSoftwareUsageAsync(Guid entryId, string processName, CancellationToken cancellationToken = default);
    Task AddManualEntryAsync(Guid projectId, Guid? taskId, string? description, DateTimeOffset startUtc, DateTimeOffset endUtc, bool isPaid = false, bool isCall = false, CancellationToken cancellationToken = default);
    Task UpdateTimeEntryAsync(Guid entryId, Guid projectId, Guid? taskId, string? description, DateTimeOffset startUtc, DateTimeOffset endUtc, bool isPaid = false, long excludedSeconds = 0, CancellationToken cancellationToken = default, bool? isCall = null);
    Task SetEntriesPaidAsync(IReadOnlyCollection<Guid> entryIds, bool isPaid, CancellationToken cancellationToken = default);
    Task DeleteTimeEntryAsync(Guid entryId, CancellationToken cancellationToken = default);
    Task AddExclusionAsync(Guid entryId, DateTimeOffset startUtc, DateTimeOffset endUtc, string reason, CancellationToken cancellationToken = default);
    Task AddExclusionsAsync(Guid entryId, IReadOnlyCollection<TimeExclusionPeriod> exclusions, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TimeEntryView>> GetEntriesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default);
    Task<DateTimeOffset?> GetLatestEntryStartUtcAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TimeEntryView>> GetPendingEntriesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportRow>> GetReportAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, string? tag = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportRow>> GetReportAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, ReportFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetGoogleSheetsEntryDeletionIdsAsync(CancellationToken cancellationToken = default);
    Task CompleteGoogleSheetsEntryDeletionsAsync(IReadOnlyCollection<Guid> entryIds, CancellationToken cancellationToken = default);
    Task<bool> HasUserProfileDataAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProfileSyncChange>> CaptureProfileSyncChangesAsync(
        Guid deviceId,
        string deviceName,
        bool seedAll,
        CancellationToken cancellationToken = default);
    Task AcknowledgeProfileSyncChangesAsync(
        IReadOnlyCollection<Guid> revisionIds,
        CancellationToken cancellationToken = default);
    Task<long> GetProfileSyncCloudCursorAsync(CancellationToken cancellationToken = default);
    Task SetProfileSyncCloudCursorAsync(long cursor, CancellationToken cancellationToken = default);
    Task<ProfileSyncReconcileResult> ReconcileProfileSyncChangesAsync(
        IReadOnlyList<ProfileSyncChange> cloudChanges,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProfileSyncConflict>> GetProfileSyncConflictsAsync(CancellationToken cancellationToken = default);
    Task ResolveProfileSyncConflictAsync(
        Guid conflictId,
        ProfileSyncResolution resolution,
        Guid? cloudRevisionId,
        Guid deviceId,
        string deviceName,
        DateTimeOffset resolvedUtc,
        CancellationToken cancellationToken = default);
    Task RegisterLegacyProfileSyncCandidatesAsync(
        IReadOnlyList<LegacyProfileSyncCandidate> candidates,
        CancellationToken cancellationToken = default);

}
