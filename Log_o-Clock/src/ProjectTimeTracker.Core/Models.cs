namespace ProjectTimeTracker.Core;

public static class SystemEntityIds
{
    public static readonly Guid UnassignedClientId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static readonly Guid UnassignedProjectId =
        Guid.Parse("00000000-0000-0000-0000-000000000002");

    public static readonly Guid GlobalSoftwareScopeId =
        Guid.Parse("00000000-0000-0000-0000-000000000003");

    public static readonly Guid GlobalTagScopeId =
        Guid.Parse("00000000-0000-0000-0000-000000000004");
}

public sealed record Client(
    Guid Id,
    string Name,
    string Color,
    bool IsArchived = false);

public sealed record Project(
    Guid Id,
    Guid ClientId,
    string Name,
    string Color,
    bool IsArchived = false,
    double? DailyTargetHours = null,
    double? WeeklyTargetHours = null,
    double? MonthlyTargetHours = null,
    decimal? HourlyRate = null,
    string Currency = "PLN",
    bool CarryOverTargetDebtEnabled = false,
    bool IsFrozen = false);

public sealed record ProjectWorkSummary(
    Guid ProjectId,
    long TotalSeconds,
    DateTimeOffset? FirstStartUtc,
    DateTimeOffset? LastEndUtc);

/// <summary>
/// An individual work target. It can belong to one project, cover every project, or
/// represent a single completion goal. Project cadence fields are derived target summaries.
/// </summary>
public enum CustomTargetCadence
{
    Daily,
    Weekly,
    Monthly,
    OneTime,
}

public enum TargetDurationMetric
{
    ActiveTime,
    IncludingShortIdle,
}

public sealed record CustomTarget(
    Guid Id,
    string Name,
    Guid? ProjectId,
    CustomTargetCadence Cadence,
    double TargetHours,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    DateTimeOffset? CompletedUtc = null,
    TargetDurationMetric DurationMetric = TargetDurationMetric.ActiveTime);

public sealed record ProjectTargetInput(
    Guid? Id,
    string Name,
    CustomTargetCadence Cadence,
    double TargetHours,
    TargetDurationMetric DurationMetric = TargetDurationMetric.ActiveTime);

public sealed record TaskWorkSummary(
    Guid TaskId,
    long TotalSeconds);

public enum SavedTaskOrigin
{
    Local,
    Trello,
    TrelloDetached,
    Notification,
}

public sealed record SavedTask(
    Guid Id,
    Guid ProjectId,
    string Name,
    bool IsArchived = false,
    SavedTaskOrigin Origin = SavedTaskOrigin.Local,
    string? ExternalUrl = null)
{
    public bool IsTrelloLinked => Origin == SavedTaskOrigin.Trello;
}

public enum ExternalTaskLinkState
{
    Linked,
    Detached,
    Suppressed,
}

public sealed record ExternalTaskLink(
    Guid? TaskId,
    string Provider,
    string ExternalId,
    string BoardId,
    string ListId,
    string WebUrl,
    ExternalTaskLinkState State,
    DateTimeOffset? RemoteModifiedUtc = null);

public sealed record TrelloConnection(
    string MemberId,
    string Username,
    string DisplayName,
    DateTimeOffset? LastSuccessfulSyncUtc = null,
    string? LastError = null,
    bool RequiresReconnect = false);

public sealed record TrelloListMapping(string ListId, string ListName);

public sealed record TrelloBoardMapping(
    Guid Id,
    Guid ProjectId,
    string BoardId,
    string BoardName,
    IReadOnlyList<TrelloListMapping> Lists);

public sealed record TrelloCredentials(string ApiKey, string Token);

public sealed record TrelloMember(string Id, string Username, string DisplayName);

public sealed record TrelloBoard(string Id, string Name, string Url);

public sealed record TrelloList(string Id, string BoardId, string Name);

public sealed record TrelloCard(
    string Id,
    string BoardId,
    string ListId,
    string Name,
    string Url,
    IReadOnlyList<string> MemberIds,
    DateTimeOffset? LastActivityUtc = null);

public sealed record TrelloSyncResult(
    int MappingCount,
    int ImportedCount,
    int UpdatedCount,
    int DetachedCount,
    int DeletedCount,
    DateTimeOffset CompletedUtc);

public sealed record GoogleSheetsCredentials(
    string ClientId,
    string ClientSecret,
    string RefreshToken);

public sealed record GoogleOAuthAuthorizationCode(
    string Code,
    string RedirectUri,
    string CodeVerifier);

public sealed record GoogleOAuthTokens(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresUtc);

public sealed record GoogleUser(string Email, string DisplayName);

public sealed record GoogleSpreadsheet(string Id, string Url);

public sealed record GoogleSheetsConnection(
    string Email,
    string DisplayName,
    string? SpreadsheetId = null,
    string? SpreadsheetUrl = null,
    bool StoreExportsInGoogleSheets = true,
    DateTimeOffset? LastSuccessfulSyncUtc = null,
    string? LastError = null,
    bool RequiresReconnect = false,
    Guid? SyncProfileId = null,
    Guid? DeviceId = null,
    string? DeviceName = null,
    string? PinnedTimeZoneId = null,
    int SyncProtocolVersion = 0);

public sealed record GoogleSheetsSyncResult(
    int WorksheetCount,
    int EntryCount,
    DateTimeOffset CompletedUtc,
    int ImportedCount = 0,
    int UploadedCount = 0,
    int ConflictCount = 0,
    bool DataChanged = false,
    IReadOnlyList<RemoteTimerStatus>? RemoteTimers = null,
    string? SharedProfileName = null);

public enum ProfileSyncOperation
{
    Upsert = 0,
    Delete = 1,
}

public sealed record ProfileSyncChange(
    Guid RevisionId,
    string EntityType,
    string EntityId,
    IReadOnlyList<Guid> ParentRevisionIds,
    ProfileSyncOperation Operation,
    Guid DeviceId,
    string DeviceName,
    DateTimeOffset ChangedUtc,
    string ContentHash,
    string? PayloadJson);

public enum ProfileSyncConflictKind
{
    ConcurrentEdit = 0,
    DeleteVersusEdit = 1,
    IdentityCollision = 2,
    LegacyEntry = 3,
    InvalidRemoteRecord = 4,
}

public sealed record ProfileSyncConflict(
    Guid Id,
    string EntityType,
    string EntityId,
    ProfileSyncConflictKind Kind,
    IReadOnlyList<ProfileSyncChange> Heads,
    DateTimeOffset DetectedUtc,
    string? Summary = null,
    string? RelatedEntityIdsJson = null);

public enum ProfileSyncResolution
{
    KeepLocal = 0,
    KeepCloud = 1,
    KeepBoth = 2,
    Delete = 3,
    Restore = 4,
    ImportLegacy = 5,
    IgnoreLegacy = 6,
}

public sealed record ProfileSyncReconcileResult(
    int ImportedCount,
    int ConflictCount,
    bool DataChanged);

public sealed record GoogleSyncProfileMetadata(
    Guid ProfileId,
    string ProfileName,
    string PinnedTimeZoneId,
    int ProtocolVersion);

public sealed record RemoteTimerStatus(
    Guid DeviceId,
    string DeviceName,
    DateTimeOffset LastSeenUtc,
    Guid? EntryId,
    string? ClientName,
    string? ProjectName,
    string? TaskName,
    DateTimeOffset? StartedUtc)
{
    public bool IsRunning => EntryId is not null && StartedUtc is not null;
}

public sealed record LegacyProfileSyncCandidate(
    string CandidateId,
    string SourceWorksheet,
    IReadOnlyList<string> RawRow,
    Guid? EntryId,
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    string? ClientName,
    string? ProjectName,
    string? TaskName,
    string? Description,
    bool IsPaid,
    bool IsCall,
    TrackingSource Source,
    string? ValidationError = null);

public static class LogExportDestinationSettings
{
    public const string DestinationKey = "storage.logExportDestination";
    public const string Local = "local";
    public const string GoogleSheets = "google-sheets";
}

public static class GoogleSheetsSettings
{
    public const string ConnectionKey = "googleSheets.connection.v1";
}

public sealed class TrelloAuthenticationException(string message) : InvalidOperationException(message);

public sealed class TrelloRateLimitException(string message) : InvalidOperationException(message);

public sealed record TagDefinition(
    Guid Id,
    string Name,
    string Color,
    bool IsGlobal = true,
    IReadOnlyList<Guid>? ProjectIds = null)
{
    public IReadOnlyList<Guid> AssignedProjectIds => ProjectIds ?? [];

    public bool IsAvailableFor(Guid projectId) =>
        IsGlobal || AssignedProjectIds.Contains(projectId);
}

public sealed record TagSummary(
    TagDefinition Tag,
    int EntryCount);

public sealed record SoftwareDefinition(
    Guid Id,
    string ProcessName,
    string Label,
    int EntryCount = 0);

public sealed record ProjectSoftwareDefinition(
    Guid ProjectId,
    string ProjectName,
    string ClientName,
    SoftwareDefinition Software,
    IReadOnlyList<TagDefinition> Tags,
    bool IsExcluded,
    bool IsGlobal = false);

public sealed record RecognitionRule(
    Guid Id,
    Guid ProjectId,
    string TitlePattern,
    string? ProcessName,
    bool IsEnabled = true);

public sealed record ProjectBulkEdit(
    bool UpdateClient = false,
    Guid? ClientId = null,
    bool UpdateColor = false,
    string? Color = null,
    bool UpdateDailyTarget = false,
    double? DailyTargetHours = null,
    bool UpdateWeeklyTarget = false,
    double? WeeklyTargetHours = null,
    bool UpdateMonthlyTarget = false,
    double? MonthlyTargetHours = null,
    bool UpdateHourlyRate = false,
    decimal? HourlyRate = null,
    bool UpdateCurrency = false,
    string? Currency = null,
    bool UpdateCarryOverTargetDebt = false,
    bool? CarryOverTargetDebtEnabled = null);

public enum TargetDebtRepaymentBasis
{
    None,
    Daily,
    Weekly,
    Monthly,
}

public sealed record ProjectTargetDebt(
    Guid ProjectId,
    long OutstandingSeconds,
    TargetDebtRepaymentBasis RepaymentBasis,
    IReadOnlyList<ProjectTargetDebtCancellation>? Cancellations = null)
{
    public IReadOnlyList<ProjectTargetDebtCancellation> ActiveCancellations => Cancellations ?? [];
    public long CanceledSeconds => ActiveCancellations.Sum(item => item.CanceledSeconds);
    public DateTimeOffset? LastCanceledUtc => ActiveCancellations
        .OrderByDescending(item => item.CanceledUtc)
        .FirstOrDefault()
        ?.CanceledUtc;
    public bool HasCanceledDebt => CanceledSeconds > 0;

    public static ProjectTargetDebt None(Guid projectId) =>
        new(projectId, 0, TargetDebtRepaymentBasis.None);
}

public sealed record ProjectTargetDebtCancellation(
    Guid Id,
    Guid ProjectId,
    long CanceledSeconds,
    DateTimeOffset CanceledUtc,
    DateTimeOffset? RestoredUtc = null);

public sealed record TargetDebtAdjustment(
    DateTimeOffset OccurredUtc,
    long DebtAddedSeconds,
    long RepaymentCapacitySeconds,
    long DebtCanceledSeconds = 0);

public sealed record TaskBulkEdit(
    bool UpdateProject = false,
    Guid? ProjectId = null);

public sealed record TagBulkEdit(
    bool UpdateColor = false,
    string? Color = null);

public sealed record RecognitionRuleBulkEdit(
    bool UpdateProject = false,
    Guid? ProjectId = null,
    bool UpdateTitlePattern = false,
    string? TitlePattern = null,
    bool UpdateProcessName = false,
    string? ProcessName = null);

public enum TrackingSource
{
    Manual = 0,
    WindowReminder = 1,
}

public sealed record TimeEntry(
    Guid Id,
    Guid ProjectId,
    Guid? TaskId,
    string? Description,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    DateTimeOffset LastCheckpointUtc,
    bool DetailsPending,
    TrackingSource Source,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    bool IsPaid = false,
    bool IsCall = false)
{
    public bool IsRunning => EndUtc is null;
}

public sealed record TimerStartResult(
    TimeEntry Entry,
    bool ResumedPreviousEntry);

public sealed record TimerStartRequest(
    Guid ProjectId,
    Guid? TaskId,
    string? Description,
    TrackingSource Source,
    DateTimeOffset StartUtc);

public sealed record TimerTransitionResult(
    TimeEntry? StoppedEntry,
    TimeEntry? RunningEntry);

public sealed record TimeExclusion(
    Guid Id,
    Guid TimeEntryId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Reason);

public sealed record TimeExclusionPeriod(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Reason);

public sealed record WindowActivity(
    nint Handle,
    string Title,
    string ProcessName,
    DateTimeOffset ObservedUtc);

[Flags]
public enum IdleProtectionReason
{
    None = 0,
    CommunicationAudio = 1,
    ForegroundAudio = 2,
    VideoPlayback = 4,
}

public sealed record IdleProtectionState(
    IdleProtectionReason ActiveReasons,
    bool CallsAvailable,
    bool VideoAvailable,
    bool IsInitialized,
    DateTimeOffset ObservedUtc)
{
    public bool IsProtected => ActiveReasons != IdleProtectionReason.None;

    public static IdleProtectionState NotStarted { get; } = new(
        IdleProtectionReason.None,
        CallsAvailable: false,
        VideoAvailable: false,
        IsInitialized: false,
        DateTimeOffset.MinValue);
}

public sealed record RecognitionCandidate(
    Project Project,
    Client Client,
    RecognitionRule Rule);

public sealed record RecognitionMatch(
    IReadOnlyList<RecognitionCandidate> Candidates,
    int Score)
{
    public bool IsMatch => Candidates.Count > 0;
    public bool IsAmbiguous => Candidates.Select(candidate => candidate.Project.Id).Distinct().Skip(1).Any();
    public RecognitionCandidate? Single => IsMatch && !IsAmbiguous ? Candidates[0] : null;
}

public sealed record ProjectOption(
    Guid ProjectId,
    Guid ClientId,
    string ClientName,
    string ProjectName,
    string Color)
{
    public string DisplayName => $"{ClientName} / {ProjectName}";
}

public sealed record TimeEntryView(
    Guid Id,
    Guid ProjectId,
    Guid? TaskId,
    string ClientName,
    string ProjectName,
    string? TaskName,
    string? Description,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    long ExcludedSeconds,
    bool DetailsPending,
    TrackingSource Source,
    bool IsPaid = false,
    decimal? HourlyRate = null,
    string Currency = "PLN",
    string SoftwareLabels = "",
    bool IsCall = false,
    DateTimeOffset? CreatedUtc = null,
    DateTimeOffset? ModifiedUtc = null)
{
    public long NetDurationSeconds(DateTimeOffset nowUtc)
    {
        var end = EndUtc ?? nowUtc;
        return Math.Max(0, (long)(end - StartUtc).TotalSeconds - ExcludedSeconds);
    }
}

public sealed record ReportRow(
    Guid ProjectId,
    Guid? TaskId,
    string ClientName,
    string ProjectName,
    string TaskName,
    long DurationSeconds,
    int EntryCount,
    decimal? HourlyRate = null,
    string Currency = "PLN",
    long PaidDurationSeconds = 0,
    long UnpaidDurationSeconds = 0,
    DateTimeOffset? LatestActivityUtc = null,
    long DurationWithShortIdleSeconds = 0,
    long CallDurationSeconds = 0);

public static class ShortIdleReportingSettings
{
    public const string MaximumMinutesKey = "reports.short-idle.maximum-minutes";
    public const int DefaultMaximumMinutes = 60;
    public const int MinimumAllowedMinutes = 1;
    public const int MaximumAllowedMinutes = 60;

    public static int ParseMaximumMinutes(string? value) =>
        int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var minutes) &&
        IsValidMaximumMinutes(minutes)
            ? minutes
            : DefaultMaximumMinutes;

    public static bool IsValidMaximumMinutes(int minutes) =>
        minutes is >= MinimumAllowedMinutes and <= MaximumAllowedMinutes;
}

public enum PaidStatusFilter
{
    All,
    Paid,
    Unpaid,
}

public sealed record ReportFilter(
    Guid? ClientId = null,
    Guid? ProjectId = null,
    Guid? TaskId = null,
    bool UnassignedTaskOnly = false,
    string? Tag = null,
    PaidStatusFilter PaidStatus = PaidStatusFilter.All);

public readonly record struct TrackingPeriod(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

public sealed record AppSetting(string Key, string Value);

public enum SessionTrackingBehavior
{
    StopTimer,
    KeepRunningAndExclude,
}

public static class SessionTrackingSettings
{
    public const string BehaviorKey = "session.tracking.behavior";
    public const string ResumeMarkerKey = "session.tracking.resume-marker";
    public const string ReviewEntryKey = "session.tracking.review-entry";

    public static string FormatResumeMarker(Guid entryId, DateTimeOffset unavailableSinceUtc) =>
        $"{entryId:D}|{unavailableSinceUtc.ToUniversalTime():O}";

    public static bool TryParseResumeMarker(
        string? value,
        out Guid entryId,
        out DateTimeOffset unavailableSinceUtc)
    {
        entryId = Guid.Empty;
        unavailableSinceUtc = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.IndexOf('|');
        return separator > 0 &&
               Guid.TryParse(value[..separator], out entryId) &&
               DateTimeOffset.TryParseExact(
                   value[(separator + 1)..],
                   "O",
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.RoundtripKind,
                   out unavailableSinceUtc);
    }
}

public static class ExcludedSoftwareReviewSettings
{
    public const string MinimumMinutesKey = "excluded-software.review.minimum-minutes";
    public const int DefaultMinimumMinutes = 5;
    public const int MinimumAllowedMinutes = 1;
    public const int MaximumAllowedMinutes = 1_440;

    public static int ParseMinimumMinutes(string? value) =>
        int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var minutes) &&
        IsValidMinimumMinutes(minutes)
            ? minutes
            : DefaultMinimumMinutes;

    public static bool IsValidMinimumMinutes(int minutes) =>
        minutes is >= MinimumAllowedMinutes and <= MaximumAllowedMinutes;
}

public static class AccumulatedAwayReviewSettings
{
    public const string MinimumMinutesKey = "accumulated-away.review.minimum-minutes";
    public const string DailyStateKey = "accumulated-away.review.daily-state";
    public const int DefaultMinimumMinutes = 5;
    public const int MinimumAllowedMinutes = 1;
    public const int MaximumAllowedMinutes = 1_440;

    public static int ParseMinimumMinutes(string? value) =>
        int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var minutes) &&
        IsValidMinimumMinutes(minutes)
            ? minutes
            : DefaultMinimumMinutes;

    public static bool IsValidMinimumMinutes(int minutes) =>
        minutes is >= MinimumAllowedMinutes and <= MaximumAllowedMinutes;
}

public static class RecentEntryResumeSettings
{
    public const string MaximumGapMinutesKey = "timer.resume-recent.maximum-gap-minutes";
    public const int DefaultMaximumGapMinutes = 2;
    public const int MinimumAllowedMinutes = 0;
    public const int MaximumAllowedMinutes = 1_440;

    public static int ParseMaximumGapMinutes(string? value) =>
        int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var minutes) &&
        IsValidMaximumGapMinutes(minutes)
            ? minutes
            : DefaultMaximumGapMinutes;

    public static bool IsValidMaximumGapMinutes(int minutes) =>
        minutes is >= MinimumAllowedMinutes and <= MaximumAllowedMinutes;
}

public static class AutomaticRecognitionSettings
{
    public const string EnabledKey = "recognition.automatic.enabled";
    public const string GraceMinutesKey = "recognition.automatic.grace-minutes";
    public const int DefaultGraceMinutes = 10;
    public const int MinimumAllowedMinutes = 1;
    public const int MaximumAllowedMinutes = 1_440;

    public static bool ParseEnabled(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public static int ParseGraceMinutes(string? value) =>
        int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var minutes) &&
        IsValidGraceMinutes(minutes)
            ? minutes
            : DefaultGraceMinutes;

    public static bool IsValidGraceMinutes(int minutes) =>
        minutes is >= MinimumAllowedMinutes and <= MaximumAllowedMinutes;
}

public enum BreakReminderPlacement
{
    BottomRight,
    ScreenCenter,
}

public sealed record BreakReminderMessage(
    string Id,
    string Text,
    int? AvailableFromHour = null,
    int? AvailableUntilHour = null)
{
    public bool IsAvailableAt(DateTimeOffset utcNow)
    {
        var hour = utcNow.ToLocalTime().Hour;
        return (!AvailableFromHour.HasValue || hour >= AvailableFromHour.Value) &&
               (!AvailableUntilHour.HasValue || hour < AvailableUntilHour.Value);
    }
}

public sealed record BreakReminderDailyUsage(DateOnly LocalDate, Dictionary<string, int> Counts);

public static class BreakReminderSettings
{
    public const string IntervalMinutesKey = "break-reminder.interval-minutes";
    public const string PlacementKey = "break-reminder.placement";
    public const string EnabledMessageIdsKey = "break-reminder.enabled-message-ids";
    public const string DailyUsageKey = "break-reminder.daily-usage";
    public const int DefaultIntervalMinutes = 120;
    public const int MinimumAllowedMinutes = 1;
    public const int MaximumAllowedMinutes = 1_440;
    public const BreakReminderPlacement DefaultPlacement = BreakReminderPlacement.BottomRight;

    public static IReadOnlyList<BreakReminderMessage> Messages { get; } =
    [
        new("bathroom", "you really need a bathroom"),
        new("break", "breeeak !"),
        new("coffee", "how about coffee?"),
        new("tea", "Make a tea."),
        new("snack", "snack time!"),
        new("stand-up", "Just stand up and do something"),
        new("laundry", "Do this fkn loundry!"),
        new("dinner", "...dinner?", AvailableFromHour: 12, AvailableUntilHour: 18),
        new("episode", "One episode...but short one!", AvailableFromHour: 10, AvailableUntilHour: 22),
    ];

    public static int ParseIntervalMinutes(string? value) =>
        int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var minutes) &&
        IsValidIntervalMinutes(minutes)
            ? minutes
            : DefaultIntervalMinutes;

    public static BreakReminderPlacement ParsePlacement(string? value) =>
        Enum.TryParse<BreakReminderPlacement>(value, ignoreCase: true, out var placement) &&
        Enum.IsDefined(placement)
            ? placement
            : DefaultPlacement;

    public static bool IsValidIntervalMinutes(int minutes) =>
        minutes is >= MinimumAllowedMinutes and <= MaximumAllowedMinutes;

    public static IReadOnlySet<string> ParseEnabledMessageIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        }

        try
        {
            var storedIds = System.Text.Json.JsonSerializer.Deserialize<string[]>(value);
            return storedIds is null
                ? Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal)
                : NormalizeEnabledMessageIds(storedIds);
        }
        catch (System.Text.Json.JsonException)
        {
            return Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        }
    }

    public static IReadOnlySet<string> NormalizeEnabledMessageIds(IEnumerable<string> messageIds)
    {
        var knownIds = Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        return messageIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && knownIds.Contains(id))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static string SerializeEnabledMessageIds(IEnumerable<string> messageIds) =>
        System.Text.Json.JsonSerializer.Serialize(
            NormalizeEnabledMessageIds(messageIds).OrderBy(id => id, StringComparer.Ordinal));

    public static BreakReminderDailyUsage ParseDailyUsage(string? value, DateOnly localDate)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new BreakReminderDailyUsage(localDate, []);
        }

        try
        {
            var stored = System.Text.Json.JsonSerializer.Deserialize<BreakReminderDailyUsage>(value);
            if (stored is null || stored.LocalDate != localDate || stored.Counts is null)
            {
                return new BreakReminderDailyUsage(localDate, []);
            }

            var knownIds = Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
            return new BreakReminderDailyUsage(
                localDate,
                stored.Counts
                    .Where(pair => knownIds.Contains(pair.Key) && pair.Value > 0)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }
        catch (System.Text.Json.JsonException)
        {
            return new BreakReminderDailyUsage(localDate, []);
        }
    }

    public static BreakReminderMessage? SelectMessage(
        IReadOnlySet<string> enabledMessageIds,
        BreakReminderDailyUsage dailyUsage,
        DateTimeOffset utcNow,
        Random? random = null)
    {
        var available = Messages
            .Where(message => enabledMessageIds.Contains(message.Id) && message.IsAvailableAt(utcNow))
            .ToArray();
        if (available.Length == 0)
        {
            return null;
        }

        var fewestUses = available.Min(message =>
            dailyUsage.Counts.GetValueOrDefault(message.Id));
        var leastUsed = available
            .Where(message => dailyUsage.Counts.GetValueOrDefault(message.Id) == fewestUses)
            .ToArray();
        return leastUsed[(random ?? Random.Shared).Next(leastUsed.Length)];
    }

    public static string SerializeDailyUsage(BreakReminderDailyUsage usage) =>
        System.Text.Json.JsonSerializer.Serialize(usage);
}

public static class SidebarTargetsPanelSettings
{
    public const string HeightKey = "sidebar.targets-panel.height";
    public const int DefaultHeight = 312;
    public const int MinimumHeight = 96;
    public const int MaximumHeight = 2_000;

    public static int ParseHeight(string? value) =>
        int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var height) &&
        IsValidHeight(height)
            ? height
            : DefaultHeight;

    public static bool IsValidHeight(int height) =>
        height is >= MinimumHeight and <= MaximumHeight;
}

public static class IdleProtectionSettings
{
    public const string CallsEnabledKey = "idle-protection.calls.enabled";
    public const string VideoEnabledKey = "idle-protection.video.enabled";

    public static bool ParseEnabled(string? value) =>
        !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
}

public enum TargetReviewMonthWeek
{
    First,
    Second,
    Penultimate,
    Last,
}

public sealed record TargetReviewSchedule(
    bool Enabled,
    DayOfWeek DayOfWeek,
    TargetReviewMonthWeek MonthWeek)
{
    public static TargetReviewSchedule Default { get; } =
        new(false, DayOfWeek.Monday, TargetReviewMonthWeek.Last);

    public bool IsDueOn(DateTime localDate)
    {
        var date = localDate.Date;
        if (!Enabled || date.DayOfWeek != DayOfWeek)
        {
            return false;
        }

        return MonthWeek switch
        {
            TargetReviewMonthWeek.First => date.Day <= 7,
            TargetReviewMonthWeek.Second => date.Day is >= 8 and <= 14,
            TargetReviewMonthWeek.Penultimate => date.AddDays(14).Month != date.Month,
            TargetReviewMonthWeek.Last => date.AddDays(7).Month != date.Month,
            _ => false,
        };
    }
}

public static class TargetReviewSettings
{
    public const string EnabledKey = "target-review.enabled";
    public const string DayOfWeekKey = "target-review.day-of-week";
    public const string MonthWeekKey = "target-review.month-week";
    public const string LastShownDateKey = "target-review.last-shown-date";

    public static TargetReviewSchedule Parse(
        string? enabled,
        string? dayOfWeek,
        string? monthWeek)
    {
        var fallback = TargetReviewSchedule.Default;
        var day = Enum.TryParse<DayOfWeek>(dayOfWeek, ignoreCase: true, out var parsedDay) &&
                  Enum.IsDefined(parsedDay)
            ? parsedDay
            : fallback.DayOfWeek;
        var week = Enum.TryParse<TargetReviewMonthWeek>(monthWeek, ignoreCase: true, out var parsedWeek) &&
                   Enum.IsDefined(parsedWeek)
            ? parsedWeek
            : fallback.MonthWeek;
        return new TargetReviewSchedule(
            string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase),
            day,
            week);
    }

    public static string FormatDate(DateTime localDate) =>
        localDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record GitHubRelease(string TagName, Uri ReleasePageUri);

public enum UpdateCheckStatus
{
    NotChecked,
    UpToDate,
    UpdateAvailable,
    NoRelease,
    Failed,
}

public sealed record UpdateCheckState(
    bool AutomaticChecksEnabled,
    UpdateCheckStatus Status,
    Version InstalledVersion,
    Version? LatestVersion,
    Uri? ReleasePageUri,
    DateTimeOffset? LastSuccessfulCheckUtc,
    string? ErrorMessage)
{
    public bool IsUpdateAvailable => LatestVersion is not null &&
                                     LatestVersion.CompareTo(InstalledVersion) > 0 &&
                                     ReleasePageUri is not null;
}

public static class UpdateCheckSettings
{
    public const string AutomaticChecksEnabledKey = "updates.automatic-checks.enabled";
    public const string LastSuccessfulCheckUtcKey = "updates.last-successful-check-utc";
    public const string LatestVersionKey = "updates.latest-version";
    public const string ReleasePageUrlKey = "updates.release-page-url";
    public const string LastResultKey = "updates.last-result";
    public const string NoReleaseResult = "no-release";
    public const string ReleaseResult = "release";
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);

    public static bool ParseAutomaticChecksEnabled(string? value) =>
        !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    public static bool TryParseReleaseVersion(string? value, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var parts = normalized.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || parts.Any(part =>
                !int.TryParse(
                    part,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _)))
        {
            return false;
        }

        if (Version.TryParse(normalized, out var parsedVersion) && parsedVersion is not null)
        {
            version = parsedVersion;
            return true;
        }

        return false;
    }

    public static bool TryParseUtc(string? value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out timestamp);

    public static bool TryParseGitHubReleasePageUri(string? value, out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            uri = candidate;
            return true;
        }

        uri = null;
        return false;
    }
}

public sealed record TargetReviewItem(
    Guid ProjectId,
    string ClientName,
    string ProjectName,
    string ProjectColor,
    long WeeklySeconds,
    double? WeeklyTargetHours,
    long MonthlySeconds,
    double? MonthlyTargetHours,
    ProjectTargetDebt? TargetDebt);

public enum SystemSessionEvent
{
    Locked,
    Unlocked,
    Suspending,
    Resumed,
    SigningOut,
    Ending,
}

public enum ReminderResult
{
    Dismissed,
    Started,
    Snoozed,
}

public sealed record ReminderResponse(
    ReminderResult Result,
    IReadOnlyList<string> SelectedTags,
    Guid? TaskId = null,
    string? TaskName = null,
    string? Description = null);
