using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using ProjectTimeTracker.Core;
using ProjectTimeTracker.Windows.Views;
using MessageBox = ProjectTimeTracker.Windows.Views.ThemedMessageBox;

namespace ProjectTimeTracker.Windows;

public sealed class AppController : IAsyncDisposable
{
    internal const string SessionReturnPromptTitle =
        "You're back! What you were working on?";
    internal const string RepeatedIdlePromptTitle = "Double trouble!";

    private static readonly TimeSpan RecognitionStabilityDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RecognitionSnoozeDuration = TimeSpan.FromMinutes(5);
    private static readonly string CurrentProcessName =
        Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "ProjectTimeTracker";

    private readonly ITrackerStore _store;
    private readonly IClock _clock;
    private readonly IForegroundActivityMonitor _foregroundMonitor;
    private readonly IUserIdleMonitor _idleMonitor;
    private readonly IIdleProtectionMonitor _idleProtectionMonitor;
    private readonly ISystemSessionMonitor _sessionMonitor;
    private readonly INotificationService _notificationService;
    private readonly Dispatcher _dispatcher;
    private readonly RecognitionEngine _recognitionEngine = new();
    private readonly TaskTitleMatcher _taskTitleMatcher = new();
    private readonly RecognitionPromptPolicy _promptPolicy = new(TimeSpan.Zero);
    private readonly AutomaticRecognitionPolicy _automaticRecognitionPolicy = new(
        TimeSpan.FromMinutes(AutomaticRecognitionSettings.DefaultGraceMinutes));
    private readonly DispatcherTimer _secondTimer;
    private readonly DispatcherTimer _checkpointTimer;
    private readonly DispatcherTimer _targetReviewTimer;
    private readonly SemaphoreSlim _excludedSoftwareReviewGate = new(1, 1);
    private readonly SemaphoreSlim _accumulatedAwayReviewGate = new(1, 1);
    private readonly SemaphoreSlim _idleReviewGate = new(1, 1);
    private readonly SemaphoreSlim _targetReviewGate = new(1, 1);
    private readonly SemaphoreSlim _breakReminderGate = new(1, 1);
    private readonly SemaphoreSlim _automaticRecognitionGate = new(1, 1);
    private IReadOnlyList<RecognitionCandidate> _recognitionCandidates = [];
    private IReadOnlyDictionary<(Guid ProjectId, string ProcessName), ProjectSoftwareDefinition> _projectSoftware =
        new Dictionary<(Guid ProjectId, string ProcessName), ProjectSoftwareDefinition>();
    private CancellationTokenSource? _recognitionDebounce;
    private CancellationTokenSource? _recognitionSnooze;
    private WindowActivity? _lastActivity;
    private WindowActivity? _lastExternalActivity;
    private WindowActivity? _lastTrackableActivity;
    private AutomaticForegroundKey? _lastAutomaticForegroundKey;
    private bool _automaticForegroundSnapshotInitialized;
    private IdleCandidate? _idleCandidate;
    private readonly Dictionary<string, ExcludedSoftwareReview> _excludedSoftwareReviews =
        new(StringComparer.OrdinalIgnoreCase);
    private AccumulatedAwayReview? _accumulatedAwayReview;
    private string? _activeExcludedSoftwareKey;
    private int _excludedSoftwarePromptCountForPreview;
    private int _accumulatedAwayPromptCountForPreview;
    private TimeEntry? _sessionStoppedEntryPendingReview;
    private IdleProtectionState _idleProtectionState = IdleProtectionState.NotStarted;
    private Func<string, string, MessageBoxResult> _idleReviewPrompt = ShowIdleReviewPrompt;
    private bool _idleReviewVisible;
    private bool _systemAvailable = true;
    private bool _signOutPrepared;
    private bool _disposed;
    private long _breakReminderCompletedSeconds;
    private long _breakReminderEntryBaselineSeconds;
    private long _breakReminderLastShownInterval;
    private Guid? _breakReminderEntryId;
    private IReadOnlySet<string> _breakReminderEnabledMessageIds =
        BreakReminderSettings.Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
    private BreakReminderDailyUsage _breakReminderDailyUsage = new(DateOnly.MinValue, []);

    public AppController(
        ITrackerStore store,
        IClock clock,
        IForegroundActivityMonitor foregroundMonitor,
        IUserIdleMonitor idleMonitor,
        IIdleProtectionMonitor idleProtectionMonitor,
        ISystemSessionMonitor sessionMonitor,
        INotificationService notificationService,
        Dispatcher dispatcher)
    {
        _store = store;
        _clock = clock;
        _foregroundMonitor = foregroundMonitor;
        _idleMonitor = idleMonitor;
        _idleProtectionMonitor = idleProtectionMonitor;
        _sessionMonitor = sessionMonitor;
        _notificationService = notificationService;
        _dispatcher = dispatcher;

        _secondTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _secondTimer.Tick += OnSecondTick;

        _checkpointTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _checkpointTimer.Tick += OnCheckpointTick;

        _targetReviewTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _targetReviewTimer.Tick += OnTargetReviewTick;
    }

    public event EventHandler? DataChanged;
    public event EventHandler? TimerTick;
    public event EventHandler<TimeEntry?>? RunningEntryChanged;
    public event EventHandler<EntryDetailsRequest>? DetailsRequested;
    public event EventHandler<IdleProtectionState>? IdleProtectionChanged;
    public event EventHandler? AutomaticRecognitionSettingsChanged;

    public TimeEntry? RunningEntry { get; private set; }
    public long RunningExcludedSeconds { get; private set; }
    public bool RecognitionEnabled { get; private set; } = true;
    public bool AutomaticRecognitionEnabled { get; private set; }
    public int AutomaticRecognitionGraceMinutes { get; private set; } =
        AutomaticRecognitionSettings.DefaultGraceMinutes;
    public bool CallsIdleProtectionEnabled { get; private set; } = true;
    public bool VideoIdleProtectionEnabled { get; private set; } = true;
    public IdleProtectionState IdleProtectionState => _idleProtectionState;
    public SessionTrackingBehavior SessionTrackingBehavior { get; private set; } =
        SessionTrackingBehavior.StopTimer;
    public int ExcludedSoftwareReviewMinimumMinutes { get; private set; } =
        ExcludedSoftwareReviewSettings.DefaultMinimumMinutes;
    public int AccumulatedAwayReviewMinimumMinutes { get; private set; } =
        AccumulatedAwayReviewSettings.DefaultMinimumMinutes;
    public int RecentEntryResumeMaximumGapMinutes { get; private set; } =
        RecentEntryResumeSettings.DefaultMaximumGapMinutes;
    public int ShortIdleReportingMaximumMinutes { get; private set; } =
        ShortIdleReportingSettings.DefaultMaximumMinutes;
    public TargetReviewSchedule TargetReviewSchedule { get; private set; } =
        TargetReviewSchedule.Default;
    public int BreakReminderIntervalMinutes { get; private set; } =
        BreakReminderSettings.DefaultIntervalMinutes;
    public BreakReminderPlacement BreakReminderPlacement { get; private set; } =
        BreakReminderSettings.DefaultPlacement;
    public IReadOnlySet<string> BreakReminderEnabledMessageIds => _breakReminderEnabledMessageIds;
    public DateTimeOffset UtcNow => _clock.UtcNow;
    public TimeSpan RunningElapsed
    {
        get
        {
            var elapsed = RunningEntry is null
                ? TimeSpan.Zero
                : _clock.UtcNow - RunningEntry.StartUtc - TimeSpan.FromSeconds(RunningExcludedSeconds);
            return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        }
    }
    public WindowActivity? CurrentActivity => _lastActivity ?? _foregroundMonitor.GetCurrentActivity();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        RunningEntry = await _store.GetRunningEntryAsync(cancellationToken);
        RunningExcludedSeconds = RunningEntry is null
            ? 0
            : await _store.GetEntryExcludedSecondsAsync(RunningEntry.Id, cancellationToken);
        _recognitionCandidates = await _store.GetRecognitionCandidatesAsync(cancellationToken);
        await ReloadSoftwareSettingsCoreAsync(cancellationToken);
        RecognitionEnabled = !string.Equals(
            await _store.GetSettingAsync("recognition.enabled", cancellationToken),
            "false",
            StringComparison.OrdinalIgnoreCase);
        AutomaticRecognitionEnabled = RecognitionEnabled && AutomaticRecognitionSettings.ParseEnabled(
            await _store.GetSettingAsync(AutomaticRecognitionSettings.EnabledKey, cancellationToken));
        AutomaticRecognitionGraceMinutes = AutomaticRecognitionSettings.ParseGraceMinutes(
            await _store.GetSettingAsync(AutomaticRecognitionSettings.GraceMinutesKey, cancellationToken));
        _automaticRecognitionPolicy.SetGracePeriod(
            TimeSpan.FromMinutes(AutomaticRecognitionGraceMinutes));
        CallsIdleProtectionEnabled = IdleProtectionSettings.ParseEnabled(
            await _store.GetSettingAsync(
                IdleProtectionSettings.CallsEnabledKey,
                cancellationToken));
        VideoIdleProtectionEnabled = IdleProtectionSettings.ParseEnabled(
            await _store.GetSettingAsync(
                IdleProtectionSettings.VideoEnabledKey,
                cancellationToken));
        ExcludedSoftwareReviewMinimumMinutes =
            ExcludedSoftwareReviewSettings.ParseMinimumMinutes(
                await _store.GetSettingAsync(
                    ExcludedSoftwareReviewSettings.MinimumMinutesKey,
                    cancellationToken));
        AccumulatedAwayReviewMinimumMinutes =
            AccumulatedAwayReviewSettings.ParseMinimumMinutes(
                await _store.GetSettingAsync(
                    AccumulatedAwayReviewSettings.MinimumMinutesKey,
                    cancellationToken));
        RecentEntryResumeMaximumGapMinutes =
            RecentEntryResumeSettings.ParseMaximumGapMinutes(
                await _store.GetSettingAsync(
                    RecentEntryResumeSettings.MaximumGapMinutesKey,
                    cancellationToken));
        ShortIdleReportingMaximumMinutes =
            ShortIdleReportingSettings.ParseMaximumMinutes(
                await _store.GetSettingAsync(
                    ShortIdleReportingSettings.MaximumMinutesKey,
                    cancellationToken));
        await RestoreAccumulatedAwayReviewAsync(cancellationToken);
        TargetReviewSchedule = TargetReviewSettings.Parse(
            await _store.GetSettingAsync(TargetReviewSettings.EnabledKey, cancellationToken),
            await _store.GetSettingAsync(TargetReviewSettings.DayOfWeekKey, cancellationToken),
            await _store.GetSettingAsync(TargetReviewSettings.MonthWeekKey, cancellationToken));
        BreakReminderIntervalMinutes = BreakReminderSettings.ParseIntervalMinutes(
            await _store.GetSettingAsync(BreakReminderSettings.IntervalMinutesKey, cancellationToken));
        BreakReminderPlacement = BreakReminderSettings.ParsePlacement(
            await _store.GetSettingAsync(BreakReminderSettings.PlacementKey, cancellationToken));
        await ReloadBreakReminderMessagesAsync(cancellationToken);
        ResetBreakReminderStreak();
        var sessionBehaviorValue = await _store.GetSettingAsync(
            SessionTrackingSettings.BehaviorKey,
            cancellationToken);
        SessionTrackingBehavior = Enum.TryParse<SessionTrackingBehavior>(
            sessionBehaviorValue,
            ignoreCase: true,
            out var sessionBehavior)
                ? sessionBehavior
                : SessionTrackingBehavior.StopTimer;
        var resumeMarkerValue = await _store.GetSettingAsync(
            SessionTrackingSettings.ResumeMarkerKey,
            cancellationToken);
        if (RunningEntry is not null &&
            SessionTrackingBehavior == SessionTrackingBehavior.KeepRunningAndExclude &&
            SessionTrackingSettings.TryParseResumeMarker(
                resumeMarkerValue,
                out var resumedEntryId,
                out var unavailableSinceUtc) &&
            resumedEntryId == RunningEntry.Id)
        {
            var startedUtc = unavailableSinceUtc.ToUniversalTime() < RunningEntry.StartUtc
                ? RunningEntry.StartUtc
                : unavailableSinceUtc.ToUniversalTime();
            _idleCandidate = new IdleCandidate(
                RunningEntry.Id,
                startedUtc,
                IdleCandidateKind.SessionUnavailable,
                "Windows signed out",
                RunningEntry.LastCheckpointUtc);
        }

        var reviewEntryValue = await _store.GetSettingAsync(
            SessionTrackingSettings.ReviewEntryKey,
            cancellationToken);
        if (Guid.TryParse(reviewEntryValue, out var reviewEntryId))
        {
            _sessionStoppedEntryPendingReview = await _store.GetTimeEntryAsync(
                reviewEntryId,
                cancellationToken);
            if (_sessionStoppedEntryPendingReview?.EndUtc is null)
            {
                _sessionStoppedEntryPendingReview = null;
                await _store.SetSettingAsync(
                    SessionTrackingSettings.ReviewEntryKey,
                    string.Empty,
                    cancellationToken);
            }
        }

        _foregroundMonitor.ActivityChanged += OnForegroundActivityChanged;
        _idleMonitor.IdleStarted += OnIdleStarted;
        _idleMonitor.ActivityResumed += OnActivityResumed;
        _idleProtectionMonitor.StateChanged += OnIdleProtectionStateChanged;
        _sessionMonitor.SessionChanged += OnSessionChanged;
        _foregroundMonitor.Start();
        _idleProtectionMonitor.Configure(
            CallsIdleProtectionEnabled,
            VideoIdleProtectionEnabled);
        _idleProtectionMonitor.Start();
        _idleMonitor.Start();
        _sessionMonitor.Start();
        ResetAutomaticRecognitionPolicy();
        if (AutomaticRecognitionEnabled)
        {
            PollAutomaticForeground(force: true);
        }
        _secondTimer.Start();
        _checkpointTimer.Start();
        _targetReviewTimer.Start();
        _ = TryShowScheduledTargetReviewAsync();
    }

    public async Task ReloadSoftwareSettingsAsync(CancellationToken cancellationToken = default)
    {
        await ReloadSoftwareSettingsCoreAsync(cancellationToken);
        DataChanged?.Invoke(this, EventArgs.Empty);
        if (AutomaticRecognitionEnabled)
        {
            ResetAutomaticRecognitionPolicy();
            PollAutomaticForeground(force: true);
        }
        else if (_foregroundMonitor.GetCurrentActivity() is { } activity)
        {
            QueueActivity(activity);
        }
    }

    public async Task ReloadSynchronizedProfileSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        _recognitionCandidates = await _store.GetRecognitionCandidatesAsync(cancellationToken);
        await ReloadSoftwareSettingsCoreAsync(cancellationToken);
        RecognitionEnabled = !string.Equals(
            await _store.GetSettingAsync("recognition.enabled", cancellationToken),
            "false",
            StringComparison.OrdinalIgnoreCase);
        AutomaticRecognitionEnabled = RecognitionEnabled && AutomaticRecognitionSettings.ParseEnabled(
            await _store.GetSettingAsync(AutomaticRecognitionSettings.EnabledKey, cancellationToken));
        AutomaticRecognitionGraceMinutes = AutomaticRecognitionSettings.ParseGraceMinutes(
            await _store.GetSettingAsync(AutomaticRecognitionSettings.GraceMinutesKey, cancellationToken));
        _automaticRecognitionPolicy.SetGracePeriod(
            TimeSpan.FromMinutes(AutomaticRecognitionGraceMinutes));
        CallsIdleProtectionEnabled = IdleProtectionSettings.ParseEnabled(
            await _store.GetSettingAsync(IdleProtectionSettings.CallsEnabledKey, cancellationToken));
        VideoIdleProtectionEnabled = IdleProtectionSettings.ParseEnabled(
            await _store.GetSettingAsync(IdleProtectionSettings.VideoEnabledKey, cancellationToken));
        ExcludedSoftwareReviewMinimumMinutes = ExcludedSoftwareReviewSettings.ParseMinimumMinutes(
            await _store.GetSettingAsync(ExcludedSoftwareReviewSettings.MinimumMinutesKey, cancellationToken));
        AccumulatedAwayReviewMinimumMinutes = AccumulatedAwayReviewSettings.ParseMinimumMinutes(
            await _store.GetSettingAsync(AccumulatedAwayReviewSettings.MinimumMinutesKey, cancellationToken));
        RecentEntryResumeMaximumGapMinutes = RecentEntryResumeSettings.ParseMaximumGapMinutes(
            await _store.GetSettingAsync(RecentEntryResumeSettings.MaximumGapMinutesKey, cancellationToken));
        ShortIdleReportingMaximumMinutes = ShortIdleReportingSettings.ParseMaximumMinutes(
            await _store.GetSettingAsync(ShortIdleReportingSettings.MaximumMinutesKey, cancellationToken));
        TargetReviewSchedule = TargetReviewSettings.Parse(
            await _store.GetSettingAsync(TargetReviewSettings.EnabledKey, cancellationToken),
            await _store.GetSettingAsync(TargetReviewSettings.DayOfWeekKey, cancellationToken),
            await _store.GetSettingAsync(TargetReviewSettings.MonthWeekKey, cancellationToken));
        BreakReminderIntervalMinutes = BreakReminderSettings.ParseIntervalMinutes(
            await _store.GetSettingAsync(BreakReminderSettings.IntervalMinutesKey, cancellationToken));
        BreakReminderPlacement = BreakReminderSettings.ParsePlacement(
            await _store.GetSettingAsync(BreakReminderSettings.PlacementKey, cancellationToken));
        await ReloadBreakReminderMessagesAsync(cancellationToken);
        var sessionBehaviorValue = await _store.GetSettingAsync(
            SessionTrackingSettings.BehaviorKey,
            cancellationToken);
        SessionTrackingBehavior = Enum.TryParse<SessionTrackingBehavior>(
            sessionBehaviorValue,
            ignoreCase: true,
            out var sessionBehavior)
                ? sessionBehavior
                : SessionTrackingBehavior.StopTimer;
        _idleProtectionMonitor.Configure(
            CallsIdleProtectionEnabled,
            VideoIdleProtectionEnabled);
        ResetBreakReminderStreak();
        ResetAutomaticRecognitionPolicy();
        AutomaticRecognitionSettingsChanged?.Invoke(this, EventArgs.Empty);
        DataChanged?.Invoke(this, EventArgs.Empty);
        if (AutomaticRecognitionEnabled)
        {
            PollAutomaticForeground(force: true);
        }
        else if (_foregroundMonitor.GetCurrentActivity() is { } activity)
        {
            QueueActivity(activity);
        }
    }

    public async Task SetRecognitionEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        RecognitionEnabled = enabled;
        await _store.SetSettingAsync("recognition.enabled", enabled ? "true" : "false", cancellationToken);
        if (!enabled)
        {
            AutomaticRecognitionEnabled = false;
            await _store.SetSettingAsync(
                AutomaticRecognitionSettings.EnabledKey,
                "false",
                cancellationToken);
            _notificationService.DismissActive();
            _recognitionDebounce?.Cancel();
            ResetAutomaticRecognitionPolicy();
        }
        else if (AutomaticRecognitionEnabled)
        {
            PollAutomaticForeground(force: true);
        }
        else if (_foregroundMonitor.GetCurrentActivity() is { } activity)
        {
            QueueActivity(activity);
        }

        AutomaticRecognitionSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetAutomaticRecognitionEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (enabled && !RecognitionEnabled)
        {
            RecognitionEnabled = true;
            await _store.SetSettingAsync("recognition.enabled", "true", cancellationToken);
        }

        AutomaticRecognitionEnabled = enabled;
        await _store.SetSettingAsync(
            AutomaticRecognitionSettings.EnabledKey,
            enabled ? "true" : "false",
            cancellationToken);
        _notificationService.DismissActive();
        _recognitionDebounce?.Cancel();
        ResetAutomaticRecognitionPolicy();
        if (enabled)
        {
            PollAutomaticForeground(force: true);
        }
        else if (RecognitionEnabled && _foregroundMonitor.GetCurrentActivity() is { } activity)
        {
            QueueActivity(activity);
        }

        AutomaticRecognitionSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetAutomaticRecognitionGraceMinutesAsync(
        int minutes,
        CancellationToken cancellationToken = default)
    {
        if (!AutomaticRecognitionSettings.IsValidGraceMinutes(minutes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minutes),
                $"Automatic recognition grace must be between {AutomaticRecognitionSettings.MinimumAllowedMinutes} and {AutomaticRecognitionSettings.MaximumAllowedMinutes} minutes.");
        }

        AutomaticRecognitionGraceMinutes = minutes;
        _automaticRecognitionPolicy.SetGracePeriod(TimeSpan.FromMinutes(minutes));
        await _store.SetSettingAsync(
            AutomaticRecognitionSettings.GraceMinutesKey,
            minutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        AutomaticRecognitionSettingsChanged?.Invoke(this, EventArgs.Empty);
        if (AutomaticRecognitionEnabled)
        {
            _ = ProcessAutomaticRecognitionActionsAsync();
        }
    }

    public async Task SetCallsIdleProtectionEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        CallsIdleProtectionEnabled = enabled;
        await _store.SetSettingAsync(
            IdleProtectionSettings.CallsEnabledKey,
            enabled ? "true" : "false",
            cancellationToken);
        _idleProtectionMonitor.Configure(
            CallsIdleProtectionEnabled,
            VideoIdleProtectionEnabled);
    }

    public async Task SetVideoIdleProtectionEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        VideoIdleProtectionEnabled = enabled;
        await _store.SetSettingAsync(
            IdleProtectionSettings.VideoEnabledKey,
            enabled ? "true" : "false",
            cancellationToken);
        _idleProtectionMonitor.Configure(
            CallsIdleProtectionEnabled,
            VideoIdleProtectionEnabled);
    }

    public async Task SetSessionTrackingBehaviorAsync(
        SessionTrackingBehavior behavior,
        CancellationToken cancellationToken = default)
    {
        SessionTrackingBehavior = behavior;
        await _store.SetSettingAsync(
            SessionTrackingSettings.BehaviorKey,
            behavior.ToString(),
            cancellationToken);
    }

    public async Task SetExcludedSoftwareReviewMinimumMinutesAsync(
        int minutes,
        CancellationToken cancellationToken = default)
    {
        if (!ExcludedSoftwareReviewSettings.IsValidMinimumMinutes(minutes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minutes),
                $"Excluded-software review time must be between {ExcludedSoftwareReviewSettings.MinimumAllowedMinutes} and {ExcludedSoftwareReviewSettings.MaximumAllowedMinutes} minutes.");
        }

        ExcludedSoftwareReviewMinimumMinutes = minutes;
        await _store.SetSettingAsync(
            ExcludedSoftwareReviewSettings.MinimumMinutesKey,
            minutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
    }

    public async Task SetAccumulatedAwayReviewMinimumMinutesAsync(
        int minutes,
        CancellationToken cancellationToken = default)
    {
        if (!AccumulatedAwayReviewSettings.IsValidMinimumMinutes(minutes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minutes),
                $"Accumulated short-idle review must be between {AccumulatedAwayReviewSettings.MinimumAllowedMinutes} and {AccumulatedAwayReviewSettings.MaximumAllowedMinutes} minutes.");
        }

        AccumulatedAwayReviewMinimumMinutes = minutes;
        await _store.SetSettingAsync(
            AccumulatedAwayReviewSettings.MinimumMinutesKey,
            minutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
    }

    public async Task SetRecentEntryResumeMaximumGapMinutesAsync(
        int minutes,
        CancellationToken cancellationToken = default)
    {
        if (!RecentEntryResumeSettings.IsValidMaximumGapMinutes(minutes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minutes),
                $"Recent-entry resume time must be between {RecentEntryResumeSettings.MinimumAllowedMinutes} and {RecentEntryResumeSettings.MaximumAllowedMinutes} minutes.");
        }

        RecentEntryResumeMaximumGapMinutes = minutes;
        await _store.SetSettingAsync(
            RecentEntryResumeSettings.MaximumGapMinutesKey,
            minutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
    }

    public async Task SetShortIdleReportingMaximumMinutesAsync(
        int minutes,
        CancellationToken cancellationToken = default)
    {
        if (!ShortIdleReportingSettings.IsValidMaximumMinutes(minutes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minutes),
                $"The report idle limit must be between {ShortIdleReportingSettings.MinimumAllowedMinutes} and {ShortIdleReportingSettings.MaximumAllowedMinutes} minutes.");
        }

        ShortIdleReportingMaximumMinutes = minutes;
        await _store.SetSettingAsync(
            ShortIdleReportingSettings.MaximumMinutesKey,
            minutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
    }

    public async Task SetTargetReviewScheduleAsync(
        TargetReviewSchedule schedule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        TargetReviewSchedule = schedule;
        await _store.SetSettingAsync(
            TargetReviewSettings.EnabledKey,
            schedule.Enabled ? "true" : "false",
            cancellationToken);
        await _store.SetSettingAsync(
            TargetReviewSettings.DayOfWeekKey,
            schedule.DayOfWeek.ToString(),
            cancellationToken);
        await _store.SetSettingAsync(
            TargetReviewSettings.MonthWeekKey,
            schedule.MonthWeek.ToString(),
            cancellationToken);

        if (schedule.Enabled)
        {
            _ = TryShowScheduledTargetReviewAsync(cancellationToken);
        }
    }

    public async Task SetBreakReminderIntervalMinutesAsync(
        int minutes,
        CancellationToken cancellationToken = default)
    {
        if (!BreakReminderSettings.IsValidIntervalMinutes(minutes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minutes),
                $"Break reminder time must be between {BreakReminderSettings.MinimumAllowedMinutes} and {BreakReminderSettings.MaximumAllowedMinutes} minutes.");
        }

        BreakReminderIntervalMinutes = minutes;
        _breakReminderLastShownInterval = 0;
        await _store.SetSettingAsync(
            BreakReminderSettings.IntervalMinutesKey,
            minutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
    }

    public async Task SetBreakReminderPlacementAsync(
        BreakReminderPlacement placement,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(placement))
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }

        BreakReminderPlacement = placement;
        await _store.SetSettingAsync(
            BreakReminderSettings.PlacementKey,
            placement.ToString(),
            cancellationToken);
    }

    public async Task SetBreakReminderEnabledMessageIdsAsync(
        IEnumerable<string> messageIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedIds = BreakReminderSettings.NormalizeEnabledMessageIds(messageIds);
        await _store.SetSettingAsync(
            BreakReminderSettings.EnabledMessageIdsKey,
            BreakReminderSettings.SerializeEnabledMessageIds(normalizedIds),
            cancellationToken);
        _breakReminderEnabledMessageIds = normalizedIds;
    }

    public async Task ShowPendingSessionNotificationAsync(
        CancellationToken cancellationToken = default)
    {
        if (_sessionStoppedEntryPendingReview is { } stoppedEntry)
        {
            _sessionStoppedEntryPendingReview = null;
            await _store.SetSettingAsync(
                SessionTrackingSettings.ReviewEntryKey,
                string.Empty,
                cancellationToken);
            await RequestDetailsAsync(
                stoppedEntry,
                cancellationToken,
                heading: SessionReturnPromptTitle);
            return;
        }

        if (_idleCandidate is { Kind: IdleCandidateKind.SessionUnavailable, EndUtc: { } endUtc })
        {
            await ReviewIdleCandidateAsync(endUtc);
        }
    }

    public async Task ReloadRecognitionAsync(
        bool dismissActiveReminder = false,
        CancellationToken cancellationToken = default)
    {
        _recognitionCandidates = await _store.GetRecognitionCandidatesAsync(cancellationToken);
        if (dismissActiveReminder)
        {
            _notificationService.DismissActive();
            _recognitionDebounce?.Cancel();
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
        if (AutomaticRecognitionEnabled)
        {
            ResetAutomaticRecognitionPolicy();
            PollAutomaticForeground(force: true);
        }
        else if (_foregroundMonitor.GetCurrentActivity() is { } activity)
        {
            QueueActivity(activity);
        }
    }

    public async Task<TimeEntry> StartTimerAsync(
        Guid projectId,
        TrackingSource source,
        bool showDetails = true,
        string? initialDescription = null,
        Guid? initialTaskId = null,
        CancellationToken cancellationToken = default)
    {
        var startUtc = _clock.UtcNow;
        if (RunningEntry is not null)
        {
            await ReviewExcludedSoftwareVisitsAsync(startUtc);
            await ReviewIdleCandidateAsync(startUtc);
            var stopped = await _store.StopRunningTimerAsync(startUtc, cancellationToken);
            if (stopped?.DetailsPending == true)
            {
                await RequestDetailsAsync(stopped, cancellationToken);
            }
        }

        ResetBreakReminderStreak();

        initialDescription = string.IsNullOrWhiteSpace(initialDescription)
            ? null
            : initialDescription.Trim();
        var startResult = await _store.StartOrResumeTimerAsync(
            projectId,
            initialTaskId,
            initialDescription,
            source,
            startUtc,
            TimeSpan.FromMinutes(RecentEntryResumeMaximumGapMinutes),
            cancellationToken);
        RunningEntry = startResult.Entry;
        ResetExcludedSoftwareTracking();
        RunningExcludedSeconds = startResult.ResumedPreviousEntry
            ? await _store.GetEntryExcludedSecondsAsync(RunningEntry.Id, cancellationToken)
            : 0;
        BeginBreakReminderEntry();
        await RecordInitialSoftwareAsync(RunningEntry.Id, source, cancellationToken);

        RunningEntryChanged?.Invoke(this, RunningEntry);
        DataChanged?.Invoke(this, EventArgs.Empty);
        ReconcileAutomaticRecognitionAfterTimerMutation();
        if (showDetails)
        {
            await RequestDetailsAsync(RunningEntry, cancellationToken);
        }

        return RunningEntry;
    }

    public Task<TimeEntry> StartUnassignedTimerAsync(
        CancellationToken cancellationToken = default) =>
        StartTimerAsync(
            SystemEntityIds.UnassignedProjectId,
            TrackingSource.Manual,
            showDetails: true,
            cancellationToken: cancellationToken);

    public async Task<TimeEntry> ContinueTimerAsync(
        Guid projectId,
        Guid? taskId,
        string? description,
        CancellationToken cancellationToken = default)
    {
        if (RunningEntry is null)
        {
            return await StartTimerAsync(
                projectId,
                TrackingSource.Manual,
                showDetails: false,
                initialDescription: description,
                initialTaskId: taskId,
                cancellationToken: cancellationToken);
        }

        await ReviewExcludedSoftwareVisitsAsync(_clock.UtcNow);
        await ReviewIdleCandidateAsync(_clock.UtcNow);
        CompleteBreakReminderEntry();
        var switchUtc = _clock.UtcNow;
        RunningEntry = await _store.SwitchRunningTimerAsync(
            RunningEntry.Id,
            projectId,
            taskId,
            description,
            TrackingSource.Manual,
            switchUtc,
            cancellationToken);
        ResetExcludedSoftwareTracking();
        RunningExcludedSeconds = 0;
        BeginBreakReminderEntry();
        await RecordInitialSoftwareAsync(
            RunningEntry.Id,
            TrackingSource.Manual,
            cancellationToken);
        RunningEntryChanged?.Invoke(this, RunningEntry);
        DataChanged?.Invoke(this, EventArgs.Empty);
        ReconcileAutomaticRecognitionAfterTimerMutation();
        return RunningEntry;
    }

    public async Task SaveRunningDetailsAsync(Guid? taskId, string? description, CancellationToken cancellationToken = default)
    {
        if (RunningEntry is null)
        {
            return;
        }

        await _store.UpdateEntryDetailsAsync(RunningEntry.Id, taskId, description, _clock.UtcNow, cancellationToken);
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        RunningEntry = RunningEntry with
        {
            TaskId = taskId,
            Description = description,
            DetailsPending =
                RunningEntry.ProjectId == SystemEntityIds.UnassignedProjectId ||
                taskId is null && description is null,
            ModifiedUtc = _clock.UtcNow,
        };
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<TimeEntry> SetRunningCallTrackingAsync(
        bool isCall,
        CancellationToken cancellationToken = default)
    {
        if (RunningEntry is not { } runningEntry)
        {
            throw new InvalidOperationException("There is no running timer to mark as a call.");
        }

        if (runningEntry.IsCall == isCall)
        {
            return runningEntry;
        }

        var nowUtc = _clock.UtcNow;
        await ReviewExcludedSoftwareVisitsAsync(nowUtc);
        await ReviewIdleCandidateAsync(nowUtc);
        CompleteBreakReminderEntry();
        RunningEntry = await _store.SplitRunningTimerAsync(
            runningEntry.Id,
            runningEntry.TaskId,
            runningEntry.Description,
            nowUtc,
            cancellationToken,
            isCall);
        ResetExcludedSoftwareTracking();
        RunningExcludedSeconds = 0;
        BeginBreakReminderEntry();
        await RecordInitialSoftwareAsync(
            RunningEntry.Id,
            RunningEntry.Source,
            cancellationToken);
        RunningEntryChanged?.Invoke(this, RunningEntry);
        DataChanged?.Invoke(this, EventArgs.Empty);
        ReconcileAutomaticRecognitionAfterTimerMutation();
        return RunningEntry;
    }

    public async Task<TimeEntry> UpdateRunningStartAsync(
        Guid entryId,
        DateTimeOffset startUtc,
        CancellationToken cancellationToken = default)
    {
        if (RunningEntry?.Id != entryId)
        {
            throw new InvalidOperationException("This entry is no longer the running timer.");
        }

        RunningEntry = await _store.UpdateRunningEntryStartAsync(
            entryId,
            startUtc,
            _clock.UtcNow,
            cancellationToken);
        AdjustPendingActivityForRunningStart(entryId, RunningEntry.StartUtc);
        RunningExcludedSeconds = await _store.GetEntryExcludedSecondsAsync(
            entryId,
            cancellationToken);
        RunningEntryChanged?.Invoke(this, RunningEntry);
        DataChanged?.Invoke(this, EventArgs.Empty);
        ReconcileAutomaticRecognitionAfterTimerMutation();
        return RunningEntry;
    }

    private void AdjustPendingActivityForRunningStart(
        Guid entryId,
        DateTimeOffset startUtc)
    {
        if (_idleCandidate is { } idle && idle.EntryId == entryId)
        {
            _idleCandidate = idle.EndUtc is { } endUtc && endUtc <= startUtc
                ? null
                : idle with
                {
                    StartUtc = idle.StartUtc < startUtc
                        ? startUtc
                        : idle.StartUtc,
                };
        }

        foreach (var review in _excludedSoftwareReviews.Values.Where(item =>
                     item.EntryId == entryId))
        {
            if (review.ActiveSinceUtc is { } activeSinceUtc &&
                activeSinceUtc < startUtc)
            {
                review.ActiveSinceUtc = startUtc;
            }

            for (var index = review.PendingIntervals.Count - 1; index >= 0; index--)
            {
                var interval = review.PendingIntervals[index];
                if (interval.EndUtc <= startUtc)
                {
                    review.PendingIntervals.RemoveAt(index);
                }
                else if (interval.StartUtc < startUtc)
                {
                    review.PendingIntervals[index] = interval with
                    {
                        StartUtc = startUtc,
                    };
                }
            }
        }
    }

    public async Task SaveRunningAssignmentAsync(
        Guid projectId,
        Guid? taskId,
        string? description,
        CancellationToken cancellationToken = default)
    {
        if (RunningEntry is null)
        {
            return;
        }

        var previousProjectId = RunningEntry.ProjectId;
        await _store.UpdateEntryAssignmentAsync(
            RunningEntry.Id,
            projectId,
            taskId,
            description,
            _clock.UtcNow,
            cancellationToken);
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        RunningEntry = RunningEntry with
        {
            ProjectId = projectId,
            TaskId = taskId,
            Description = description,
            DetailsPending =
                projectId == SystemEntityIds.UnassignedProjectId ||
                taskId is null && description is null,
            ModifiedUtc = _clock.UtcNow,
        };
        DataChanged?.Invoke(this, EventArgs.Empty);
        if (projectId != previousProjectId)
        {
            ReconcileAutomaticRecognitionAfterTimerMutation();
        }
    }

    public Task ShowRunningEntryDetailsAsync(CancellationToken cancellationToken = default)
    {
        var runningEntry = RunningEntry;
        return runningEntry is null
            ? Task.CompletedTask
            : RequestDetailsAsync(runningEntry, cancellationToken, canRip: true);
    }

    public async Task<TimeEntry> RipRunningEntryAsync(
        Guid entryId,
        Guid projectId,
        Guid? taskId,
        string? description,
        CancellationToken cancellationToken = default)
    {
        if (RunningEntry?.Id != entryId)
        {
            throw new InvalidOperationException("This entry is no longer the running timer.");
        }

        await ReviewExcludedSoftwareVisitsAsync(_clock.UtcNow);
        await ReviewIdleCandidateAsync(_clock.UtcNow);
        await _store.UpdateEntryAssignmentAsync(
            entryId,
            projectId,
            taskId,
            description,
            _clock.UtcNow,
            cancellationToken);
        CompleteBreakReminderEntry();
        RunningEntry = await _store.SplitRunningTimerAsync(
            entryId,
            taskId,
            description,
            _clock.UtcNow,
            cancellationToken);
        ResetExcludedSoftwareTracking();
        RunningExcludedSeconds = 0;
        BeginBreakReminderEntry();
        await RecordInitialSoftwareAsync(RunningEntry.Id, TrackingSource.Manual, cancellationToken);
        RunningEntryChanged?.Invoke(this, RunningEntry);
        DataChanged?.Invoke(this, EventArgs.Empty);
        ReconcileAutomaticRecognitionAfterTimerMutation();
        return RunningEntry;
    }

    public async Task<TimeEntry?> StopTimerAsync(CancellationToken cancellationToken = default)
    {
        await ReviewExcludedSoftwareVisitsAsync(_clock.UtcNow);
        await ReviewIdleCandidateAsync(_clock.UtcNow);
        var stopped = await _store.StopRunningTimerAsync(_clock.UtcNow, cancellationToken);
        ResetExcludedSoftwareTracking();
        ResetBreakReminderStreak();
        RunningEntry = null;
        RunningExcludedSeconds = 0;
        RunningEntryChanged?.Invoke(this, null);
        DataChanged?.Invoke(this, EventArgs.Empty);
        ReconcileAutomaticRecognitionAfterTimerMutation();

        if (stopped is not null)
        {
            await RequestDetailsAsync(stopped, cancellationToken);
        }

        if (!AutomaticRecognitionEnabled && _foregroundMonitor.GetCurrentActivity() is { } activity)
        {
            QueueActivity(activity);
        }

        return stopped;
    }

    public async Task<bool> CancelRunningTimerAsync(CancellationToken cancellationToken = default)
    {
        if (RunningEntry is not { } runningEntry ||
            !await _store.CancelRunningTimerAsync(runningEntry.Id, cancellationToken))
        {
            return false;
        }

        if (_idleCandidate?.EntryId == runningEntry.Id)
        {
            _idleCandidate = null;
        }

        if (_accumulatedAwayReview is { } accumulatedAwayReview)
        {
            accumulatedAwayReview.PendingIntervals.RemoveAll(
                interval => interval.EntryId == runningEntry.Id);
            await PersistAccumulatedAwayReviewAsync(cancellationToken);
        }

        ResetExcludedSoftwareTracking();
        RunningEntry = null;
        RunningExcludedSeconds = 0;
        ResetBreakReminderStreak();
        RunningEntryChanged?.Invoke(this, null);
        DataChanged?.Invoke(this, EventArgs.Empty);
        ReconcileAutomaticRecognitionAfterTimerMutation();
        return true;
    }

    public async Task StopForShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _store.SetSettingAsync(
            SessionTrackingSettings.ResumeMarkerKey,
            string.Empty,
            cancellationToken);
        if (RunningEntry is null)
        {
            return;
        }

        await _store.StopRunningTimerAsync(_clock.UtcNow, cancellationToken);
        _idleCandidate = null;
        ResetExcludedSoftwareTracking();
        ResetBreakReminderStreak();
        RunningEntry = null;
        RunningExcludedSeconds = 0;
    }

    public void NotifyDataChanged() => DataChanged?.Invoke(this, EventArgs.Empty);

    public void NotifyEntryDetailsChanged(Guid entryId, Guid? taskId, string? description)
    {
        var projectId = RunningEntry?.Id == entryId
            ? RunningEntry.ProjectId
            : SystemEntityIds.UnassignedProjectId;
        NotifyEntryDetailsChanged(entryId, projectId, taskId, description);
    }

    public void NotifyEntryDetailsChanged(
        Guid entryId,
        Guid projectId,
        Guid? taskId,
        string? description)
    {
        var previousProjectId = RunningEntry?.Id == entryId
            ? RunningEntry.ProjectId
            : (Guid?)null;
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (RunningEntry?.Id == entryId)
        {
            RunningEntry = RunningEntry with
            {
                ProjectId = projectId,
                TaskId = taskId,
                Description = description,
                DetailsPending =
                    projectId == SystemEntityIds.UnassignedProjectId ||
                    taskId is null && description is null,
                ModifiedUtc = _clock.UtcNow,
            };
            RunningEntryChanged?.Invoke(this, RunningEntry);
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
        if (previousProjectId is not null && previousProjectId != projectId)
        {
            ReconcileAutomaticRecognitionAfterTimerMutation();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _secondTimer.Stop();
        _checkpointTimer.Stop();
        _targetReviewTimer.Stop();
        _recognitionDebounce?.Cancel();
        _recognitionDebounce?.Dispose();
        _recognitionSnooze?.Cancel();
        _recognitionSnooze?.Dispose();
        _foregroundMonitor.ActivityChanged -= OnForegroundActivityChanged;
        _idleMonitor.IdleStarted -= OnIdleStarted;
        _idleMonitor.ActivityResumed -= OnActivityResumed;
        _idleProtectionMonitor.StateChanged -= OnIdleProtectionStateChanged;
        _sessionMonitor.SessionChanged -= OnSessionChanged;
        _notificationService.Dispose();
        _foregroundMonitor.Dispose();
        _idleMonitor.Dispose();
        _idleProtectionMonitor.Dispose();
        _sessionMonitor.Dispose();
        _excludedSoftwareReviewGate.Dispose();
        _accumulatedAwayReviewGate.Dispose();
        _idleReviewGate.Dispose();
        _targetReviewGate.Dispose();
        _automaticRecognitionGate.Dispose();
        await _store.DisposeAsync();
    }

    private void OnForegroundActivityChanged(object? sender, WindowActivity activity)
    {
        _ = sender;
        _dispatcher.BeginInvoke(() =>
        {
            QueueActivity(activity);
        });
    }

    private void QueueActivity(WindowActivity activity)
    {
        if (_disposed)
        {
            return;
        }

        _lastActivity = activity;

        if (IsTrackableProcess(activity.ProcessName))
        {
            _lastExternalActivity = activity;
        }

        if (AutomaticRecognitionEnabled)
        {
            QueueAutomaticRecognition(activity);
        }
        else
        {
            QueueRecognition(activity);
        }
        if (RunningEntry is { } runningEntry &&
            TryGetExcludedSoftware(runningEntry.ProjectId, activity.ProcessName, out var excludedSoftware))
        {
            ObserveExcludedActivity(activity, excludedSoftware);
            return;
        }

        if (_activeExcludedSoftwareKey is not null)
        {
            _ = ResumeFromExcludedSoftwareAsync(activity);
        }
        else
        {
            ObserveTrackableActivity(activity);
        }
    }

    private void ObserveExcludedActivity(WindowActivity activity, SoftwareDefinition software)
    {
        _lastActivity = activity;
        _notificationService.DismissActive();

        if (RunningEntry is not { } entry)
        {
            return;
        }

        var processKey = NormalizeProcessName(software.ProcessName);
        string? completedProcessKey = null;
        if (_activeExcludedSoftwareKey is not null &&
            !string.Equals(
                _activeExcludedSoftwareKey,
                processKey,
                StringComparison.OrdinalIgnoreCase))
        {
            completedProcessKey = CompleteActiveExcludedSoftwareVisit(activity.ObservedUtc);
        }

        if (!_excludedSoftwareReviews.TryGetValue(processKey, out var review) ||
            review.EntryId != entry.Id)
        {
            review = new ExcludedSoftwareReview(entry.Id, processKey, software.Label);
            _excludedSoftwareReviews[processKey] = review;
        }
        else
        {
            review.Label = software.Label;
        }

        if (!string.Equals(
                _activeExcludedSoftwareKey,
                processKey,
                StringComparison.OrdinalIgnoreCase))
        {
            review.ActiveSinceUtc = activity.ObservedUtc < entry.StartUtc
                ? entry.StartUtc
                : activity.ObservedUtc;
            _activeExcludedSoftwareKey = processKey;
        }

        if (completedProcessKey is not null)
        {
            _ = ReviewExcludedSoftwareAfterVisitAsync(completedProcessKey);
        }
    }

    private async Task ResumeFromExcludedSoftwareAsync(WindowActivity activity)
    {
        var completedProcessKey = CompleteActiveExcludedSoftwareVisit(activity.ObservedUtc);
        if (completedProcessKey is not null)
        {
            await ReviewExcludedSoftwareAsync(completedProcessKey);
        }

        ObserveTrackableActivity(activity);
    }

    private string? CompleteActiveExcludedSoftwareVisit(DateTimeOffset endedUtc)
    {
        var processKey = _activeExcludedSoftwareKey;
        _activeExcludedSoftwareKey = null;
        if (processKey is null ||
            !_excludedSoftwareReviews.TryGetValue(processKey, out var review) ||
            review.ActiveSinceUtc is not { } startedUtc)
        {
            return null;
        }

        review.ActiveSinceUtc = null;
        endedUtc = endedUtc.ToUniversalTime();
        startedUtc = startedUtc.ToUniversalTime();
        if (endedUtc > startedUtc)
        {
            var interval = new ExcludedSoftwareInterval(startedUtc, endedUtc);
            review.PendingIntervals.Add(interval);
        }

        return processKey;
    }

    private async Task<bool> ReviewExcludedSoftwareAsync(
        string processKey,
        bool? removeOverride = null,
        bool ignoreThreshold = false)
    {
        await _excludedSoftwareReviewGate.WaitAsync();
        try
        {
            if (!_excludedSoftwareReviews.TryGetValue(processKey, out var review) ||
                RunningEntry?.Id != review.EntryId ||
                review.PendingIntervals.Count == 0)
            {
                return false;
            }

            var pendingDuration = review.PendingIntervals.Aggregate(
                TimeSpan.Zero,
                static (duration, interval) => duration + (interval.EndUtc - interval.StartUtc));
            if (review.RemoveDecision is null)
            {
                var minimumDuration = TimeSpan.FromMinutes(ExcludedSoftwareReviewMinimumMinutes);
                if (!ignoreThreshold && pendingDuration < minimumDuration)
                {
                    return false;
                }

                var visitCount = review.PendingIntervals.Count;
                var visitsText = visitCount == 1 ? "1 visit" : $"{visitCount} visits";
                var message =
                    $"You used {review.Label} for {FormatDuration(pendingDuration)} across {visitsText}.\n\n" +
                    "Remove this summed time from the running timer? This decision will also apply to later visits to this app during the current entry.";
                review.RemoveDecision = removeOverride ?? MessageBox.ShowTopmost(
                    message,
                    "Review excluded software time",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes) == MessageBoxResult.Yes;
                _excludedSoftwarePromptCountForPreview++;
            }

            var pendingCount = review.PendingIntervals.Count;
            var pendingIntervals = review.PendingIntervals.Take(pendingCount).ToArray();
            if (review.RemoveDecision == true)
            {
                var reason = $"Excluded software: {review.Label}";
                var exclusions = pendingIntervals
                    .Select(interval => new TimeExclusionPeriod(
                        interval.StartUtc,
                        interval.EndUtc,
                        reason))
                    .ToArray();
                await _store.AddExclusionsAsync(review.EntryId, exclusions);
                if (RunningEntry?.Id == review.EntryId)
                {
                    RunningExcludedSeconds = await _store.GetEntryExcludedSecondsAsync(review.EntryId);
                }

                DataChanged?.Invoke(this, EventArgs.Empty);
            }

            review.PendingIntervals.RemoveRange(
                0,
                Math.Min(pendingCount, review.PendingIntervals.Count));
            return true;
        }
        finally
        {
            _excludedSoftwareReviewGate.Release();
        }
    }

    private async Task ReviewExcludedSoftwareVisitsAsync(
        DateTimeOffset endedUtc,
        bool? removeOverride = null,
        bool ignoreThreshold = false)
    {
        _ = CompleteActiveExcludedSoftwareVisit(endedUtc);
        foreach (var processKey in _excludedSoftwareReviews.Keys.ToArray())
        {
            await ReviewExcludedSoftwareAsync(processKey, removeOverride, ignoreThreshold);
        }

    }

    private void ResetExcludedSoftwareTracking()
    {
        _activeExcludedSoftwareKey = null;
        _excludedSoftwareReviews.Clear();
        _excludedSoftwarePromptCountForPreview = 0;
    }

    private Task ReviewExcludedSoftwareAfterVisitAsync(string processKey)
    {
        return ReviewExcludedSoftwareAsync(processKey);
    }

    private bool AddAccumulatedAwayInterval(
        Guid entryId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        bool requireCurrentEntry = true)
    {
        startUtc = startUtc.ToUniversalTime();
        endUtc = endUtc.ToUniversalTime();
        var nowUtc = _clock.UtcNow;
        if (RunningEntry is null ||
            requireCurrentEntry && RunningEntry.Id != entryId ||
            !ShortIdleReviewPolicy.TryClipToAccumulationWindow(
                startUtc,
                endUtc,
                nowUtc,
                out startUtc,
                out endUtc))
        {
            return false;
        }

        EnsureAccumulatedAwayReview(nowUtc);
        var review = _accumulatedAwayReview!;
        review.PendingIntervals.Add(new AccumulatedAwayInterval(
            entryId,
            startUtc,
            endUtc));
        return true;
    }

    private async Task<bool> ReviewAccumulatedAwayTimeAsync(
        bool? removeOverride = null)
    {
        await _accumulatedAwayReviewGate.WaitAsync();
        try
        {
            var nowUtc = _clock.UtcNow;
            EnsureAccumulatedAwayReview(nowUtc);
            var review = _accumulatedAwayReview;
            if (review is null ||
                RunningEntry is null ||
                review.PendingIntervals.Count == 0)
            {
                await PersistAccumulatedAwayReviewAsync();
                return false;
            }

            var pendingSeconds = GetAccumulatedSeconds(
                review.PendingIntervals,
                nowUtc);
            if (!ShortIdleReviewPolicy.ShouldPrompt(
                    pendingSeconds,
                    AccumulatedAwayReviewMinimumMinutes,
                    review.NextPromptMultiplier))
            {
                await PersistAccumulatedAwayReviewAsync();
                return false;
            }

            var pendingIntervals = await GetValidAccumulatedAwayIntervalsAsync(
                review.PendingIntervals,
                nowUtc);
            review.PendingIntervals.Clear();
            review.PendingIntervals.AddRange(pendingIntervals);
            pendingSeconds = GetAccumulatedSeconds(pendingIntervals, nowUtc);
            if (!ShortIdleReviewPolicy.ShouldPrompt(
                    pendingSeconds,
                    AccumulatedAwayReviewMinimumMinutes,
                    review.NextPromptMultiplier))
            {
                await PersistAccumulatedAwayReviewAsync();
                return false;
            }

            var pendingDuration = TimeSpan.FromSeconds(pendingSeconds);
            var intervalsText = pendingIntervals.Length == 1
                ? "1 interval"
                : $"{pendingIntervals.Length} intervals";
            var shouldRemove = removeOverride ?? MessageBox.ShowTopmost(
                $"Short idle time totals {FormatDuration(pendingDuration)} across {intervalsText} within the last 4 hours.\n\nRemove these idle intervals from their tracked entries?",
                "Review accumulated short idle time",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes) == MessageBoxResult.Yes;
            _accumulatedAwayPromptCountForPreview++;

            if (shouldRemove)
            {
                foreach (var entryIntervals in pendingIntervals.GroupBy(interval => interval.EntryId))
                {
                    await _store.AddExclusionsAsync(
                        entryIntervals.Key,
                        entryIntervals
                            .Select(interval => new TimeExclusionPeriod(
                                interval.StartUtc,
                                interval.EndUtc,
                                "Accumulated short idle"))
                            .ToArray());
                }

                if (RunningEntry is { } runningEntry)
                {
                    RunningExcludedSeconds = await _store.GetEntryExcludedSecondsAsync(runningEntry.Id);
                }

                review.PendingIntervals.Clear();
                DataChanged?.Invoke(this, EventArgs.Empty);
            }

            // Reaching the user's short-idle review threshold means the work streak
            // was interrupted, whether the recorded time is kept or removed.
            ResetBreakReminderStreak();
            review.NextPromptMultiplier = ShortIdleReviewPolicy.NextPromptMultiplier(
                review.NextPromptMultiplier,
                shouldRemove);
            await PersistAccumulatedAwayReviewAsync();
            return true;
        }
        finally
        {
            _accumulatedAwayReviewGate.Release();
        }
    }

    private async Task<AccumulatedAwayInterval[]> GetValidAccumulatedAwayIntervalsAsync(
        IReadOnlyCollection<AccumulatedAwayInterval> intervals,
        DateTimeOffset nowUtc)
    {
        var valid = new List<AccumulatedAwayInterval>(intervals.Count);
        foreach (var entryGroup in intervals.GroupBy(interval => interval.EntryId))
        {
            var entry = RunningEntry?.Id == entryGroup.Key
                ? RunningEntry
                : await _store.GetTimeEntryAsync(entryGroup.Key);
            if (entry is null)
            {
                continue;
            }

            foreach (var interval in entryGroup)
            {
                var startUtc = interval.StartUtc < entry.StartUtc
                    ? entry.StartUtc
                    : interval.StartUtc;
                var endUtc = entry.EndUtc is { } entryEndUtc && interval.EndUtc > entryEndUtc
                    ? entryEndUtc
                    : interval.EndUtc;
                if (ShortIdleReviewPolicy.TryClipToAccumulationWindow(
                        startUtc,
                        endUtc,
                        nowUtc,
                        out var clippedStartUtc,
                        out var clippedEndUtc))
                {
                    valid.Add(interval with
                    {
                        StartUtc = clippedStartUtc,
                        EndUtc = clippedEndUtc,
                    });
                }
            }
        }

        return valid.ToArray();
    }

    private void EnsureAccumulatedAwayReview(DateTimeOffset nowUtc)
    {
        _accumulatedAwayReview ??= new AccumulatedAwayReview();
        PruneAccumulatedAwayIntervals(_accumulatedAwayReview, nowUtc);
    }

    private static bool PruneAccumulatedAwayIntervals(
        AccumulatedAwayReview review,
        DateTimeOffset nowUtc)
    {
        var changed = false;
        for (var index = review.PendingIntervals.Count - 1; index >= 0; index--)
        {
            var interval = review.PendingIntervals[index];
            if (!ShortIdleReviewPolicy.TryClipToAccumulationWindow(
                    interval.StartUtc,
                    interval.EndUtc,
                    nowUtc,
                    out var startUtc,
                    out var endUtc))
            {
                review.PendingIntervals.RemoveAt(index);
                changed = true;
                continue;
            }

            if (startUtc != interval.StartUtc || endUtc != interval.EndUtc)
            {
                review.PendingIntervals[index] = interval with
                {
                    StartUtc = startUtc,
                    EndUtc = endUtc,
                };
                changed = true;
            }
        }

        if (review.PendingIntervals.Count == 0 &&
            review.NextPromptMultiplier != 1)
        {
            review.NextPromptMultiplier = 1;
            changed = true;
        }

        return changed;
    }

    private async Task RestoreAccumulatedAwayReviewAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _clock.UtcNow;
        var serialized = await _store.GetSettingAsync(
            AccumulatedAwayReviewSettings.DailyStateKey,
            cancellationToken);
        try
        {
            var saved = string.IsNullOrWhiteSpace(serialized)
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<AccumulatedAwayState>(serialized);
            if (saved?.PendingIntervals is not null)
            {
                var review = new AccumulatedAwayReview
                {
                    NextPromptMultiplier = ShortIdleReviewPolicy.NormalizePromptMultiplier(
                        saved.NextPromptMultiplier),
                };
                review.PendingIntervals.AddRange(saved.PendingIntervals.Where(interval =>
                    ShortIdleReviewPolicy.IsAccumulatedInterval(
                        interval.EndUtc - interval.StartUtc)));
                _accumulatedAwayReview = review;
                PruneAccumulatedAwayIntervals(review, nowUtc);
                await PersistAccumulatedAwayReviewAsync(cancellationToken);
                return;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed transient state must never prevent the tracker from starting.
        }

        _accumulatedAwayReview = new AccumulatedAwayReview();
        await PersistAccumulatedAwayReviewAsync(cancellationToken);
    }

    private Task PersistAccumulatedAwayReviewAsync(
        CancellationToken cancellationToken = default)
    {
        var review = _accumulatedAwayReview ??
            new AccumulatedAwayReview();
        PruneAccumulatedAwayIntervals(review, _clock.UtcNow);
        var state = new AccumulatedAwayState(
            GetLocalDate(_clock.UtcNow),
            review.NextPromptMultiplier,
            review.PendingIntervals.ToArray());
        return _store.SetSettingAsync(
            AccumulatedAwayReviewSettings.DailyStateKey,
            System.Text.Json.JsonSerializer.Serialize(state),
            cancellationToken);
    }

    private static long GetAccumulatedSeconds(
        IEnumerable<AccumulatedAwayInterval> intervals,
        DateTimeOffset nowUtc) =>
        intervals.Sum(interval =>
            ShortIdleReviewPolicy.TryClipToAccumulationWindow(
                interval.StartUtc,
                interval.EndUtc,
                nowUtc,
                out var startUtc,
                out var endUtc)
                ? Math.Max(0, (long)(endUtc - startUtc).TotalSeconds)
                : 0);

    private static DateOnly GetLocalDate(DateTimeOffset value) =>
        DateOnly.FromDateTime(value.ToLocalTime().DateTime);

    private void ObserveTrackableActivity(WindowActivity activity)
    {
        if (!IsTrackableProcess(activity.ProcessName))
        {
            return;
        }

        _lastTrackableActivity = activity;
        if (RunningEntry is { } entry)
        {
            _ = RecordSoftwareAsync(entry.Id, activity.ProcessName, notify: true, CancellationToken.None);
        }
    }

    private async Task RecordInitialSoftwareAsync(
        Guid entryId,
        TrackingSource source,
        CancellationToken cancellationToken)
    {
        var current = _foregroundMonitor.GetCurrentActivity();
        var activity = current is not null && IsTrackableProcess(current.ProcessName)
            ? current
            : _lastExternalActivity is { } recent &&
              (source == TrackingSource.WindowReminder || _clock.UtcNow - recent.ObservedUtc <= TimeSpan.FromSeconds(15))
                ? recent
                : null;
        var projectId = RunningEntry?.ProjectId;
        if (activity is null || projectId is null)
        {
            return;
        }

        if (TryGetExcludedSoftware(
                projectId.Value,
                activity.ProcessName,
                out var excludedSoftware))
        {
            ObserveExcludedActivity(activity, excludedSoftware);
        }
        else
        {
            await RecordSoftwareAsync(entryId, activity.ProcessName, notify: false, cancellationToken);
        }
    }

    private async Task RecordSoftwareAsync(
        Guid entryId,
        string processName,
        bool notify,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await _store.RecordSoftwareUsageAsync(entryId, processName, cancellationToken) && notify)
            {
                DataChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private static bool IsTrackableProcess(string processName) =>
        !string.IsNullOrWhiteSpace(processName) &&
        !string.Equals(processName, CurrentProcessName, StringComparison.OrdinalIgnoreCase);

    private bool TryGetExcludedSoftware(
        Guid projectId,
        string processName,
        out SoftwareDefinition software)
    {
        software = null!;
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var normalizedProcessName = NormalizeProcessName(processName);
        if ((_projectSoftware.TryGetValue(
                 (projectId, normalizedProcessName),
                 out var setting) ||
             _projectSoftware.TryGetValue(
                 (SystemEntityIds.GlobalSoftwareScopeId, normalizedProcessName),
                 out setting)) &&
            setting.IsExcluded)
        {
            software = setting.Software;
            return true;
        }

        return false;
    }

    private async Task ReloadSoftwareSettingsCoreAsync(CancellationToken cancellationToken)
    {
        _projectSoftware = (await _store.GetProjectSoftwareAsync(cancellationToken: cancellationToken))
            .ToDictionary(
                setting => (setting.ProjectId, NormalizeProcessName(setting.Software.ProcessName)),
                setting => setting);
    }

    private static string NormalizeProcessName(string processName)
    {
        processName = processName.Trim();
        processName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return processName.ToUpperInvariant();
    }

    private void QueueRecognition(WindowActivity activity)
    {
        if (_disposed)
        {
            return;
        }

        _lastActivity = activity;
        var immediateMatch = GetRelevantRecognitionMatch(activity);
        _promptPolicy.Observe(
            immediateMatch.Candidates.Select(candidate => candidate.Project.Id).Distinct().ToArray(),
            _clock.MonotonicSeconds);

        _recognitionDebounce?.Cancel();
        _recognitionDebounce?.Dispose();
        _recognitionDebounce = new CancellationTokenSource();
        _ = EvaluateAfterDebounceAsync(activity, _recognitionDebounce.Token);
    }

    private void PollAutomaticForeground(bool force = false)
    {
        if (_disposed || !AutomaticRecognitionEnabled)
        {
            return;
        }

        var current = _foregroundMonitor.GetCurrentActivity();
        if (current is not null)
        {
            var key = AutomaticForegroundKey.From(current);
            if (!force && _automaticForegroundSnapshotInitialized &&
                _lastAutomaticForegroundKey == key)
            {
                return;
            }

            QueueActivity(current);
            return;
        }

        QueueAutomaticRecognition(activity: null, force);
    }

    private void QueueAutomaticRecognition(WindowActivity? activity, bool force = false)
    {
        if (_disposed || !AutomaticRecognitionEnabled)
        {
            return;
        }

        AutomaticForegroundKey? key = activity is null
            ? null
            : AutomaticForegroundKey.From(activity);
        if (!force && _automaticForegroundSnapshotInitialized &&
            _lastAutomaticForegroundKey == key)
        {
            return;
        }

        _automaticForegroundSnapshotInitialized = true;
        _lastAutomaticForegroundKey = key;
        var observedUtc = activity?.ObservedUtc ?? _clock.UtcNow;
        var observedMonotonicSeconds = _clock.MonotonicSeconds;
        _recognitionDebounce?.Cancel();
        _recognitionDebounce?.Dispose();
        _recognitionDebounce = new CancellationTokenSource();
        _ = EvaluateAutomaticAfterDebounceAsync(
            activity,
            observedUtc,
            observedMonotonicSeconds,
            _recognitionDebounce.Token);
    }

    private async Task EvaluateAutomaticAfterDebounceAsync(
        WindowActivity? activity,
        DateTimeOffset observedUtc,
        double observedMonotonicSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RecognitionStabilityDelay, cancellationToken);
            if (!AutomaticRecognitionEnabled || !_systemAvailable || _idleMonitor.IsIdle)
            {
                return;
            }

            AutomaticForegroundKey? currentKey = activity is null
                ? null
                : AutomaticForegroundKey.From(activity);
            if (!_automaticForegroundSnapshotInitialized ||
                _lastAutomaticForegroundKey != currentKey)
            {
                return;
            }

            await _automaticRecognitionGate.WaitAsync(cancellationToken);
            try
            {
                await ProcessAutomaticRecognitionActionsLockedAsync(
                    observedMonotonicSeconds,
                    cancellationToken);
                var match = activity is null
                    ? new RecognitionMatch([], 0)
                    : GetTrackableRecognitionMatch(activity);
                var projectId = _automaticRecognitionPolicy.ResolveProjectId(match);
                _automaticRecognitionPolicy.Observe(
                    projectId,
                    observedUtc,
                    observedMonotonicSeconds,
                    activity);
                await ProcessAutomaticRecognitionActionsLockedAsync(
                    _clock.MonotonicSeconds,
                    cancellationToken);
            }
            finally
            {
                _automaticRecognitionGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            ResetAutomaticRecognitionPolicy();
        }
    }

    private void ResetAutomaticRecognitionPolicy()
    {
        _automaticRecognitionPolicy.Reset(
            RunningEntry?.ProjectId,
            _clock.UtcNow,
            _clock.MonotonicSeconds);
        _automaticForegroundSnapshotInitialized = false;
        _lastAutomaticForegroundKey = null;
    }

    private void ReconcileAutomaticRecognitionAfterTimerMutation()
    {
        if (!AutomaticRecognitionEnabled || _disposed)
        {
            return;
        }

        _recognitionDebounce?.Cancel();
        ResetAutomaticRecognitionPolicy();
        PollAutomaticForeground(force: true);
    }

    private async Task ProcessAutomaticRecognitionActionsAsync()
    {
        if (_disposed || !AutomaticRecognitionEnabled ||
            !await _automaticRecognitionGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            await ProcessAutomaticRecognitionActionsLockedAsync(
                _clock.MonotonicSeconds,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            ResetAutomaticRecognitionPolicy();
        }
        finally
        {
            _automaticRecognitionGate.Release();
        }
    }

    private async Task ProcessAutomaticRecognitionActionsLockedAsync(
        double monotonicSeconds,
        CancellationToken cancellationToken)
    {
        while (AutomaticRecognitionEnabled &&
               _automaticRecognitionPolicy.TakeNextAction(monotonicSeconds) is { } action)
        {
            await ApplyAutomaticRecognitionActionAsync(action, cancellationToken);
        }
    }

    private async Task ApplyAutomaticRecognitionActionAsync(
        AutomaticRecognitionAction action,
        CancellationToken cancellationToken)
    {
        if (action.IsInitialStart)
        {
            if (RunningEntry is not null || action.StartingVisit is not { } initialVisit)
            {
                ResetAutomaticRecognitionPolicy();
                return;
            }

            var taskId = await ResolveAutomaticTaskAsync(initialVisit, cancellationToken);
            var startResult = await _store.StartOrResumeTimerAsync(
                initialVisit.ProjectId!.Value,
                taskId,
                null,
                TrackingSource.WindowReminder,
                initialVisit.StartedUtc,
                TimeSpan.FromMinutes(RecentEntryResumeMaximumGapMinutes),
                cancellationToken);
            RunningEntry = startResult.Entry;
            ResetExcludedSoftwareTracking();
            RunningExcludedSeconds = startResult.ResumedPreviousEntry
                ? await _store.GetEntryExcludedSecondsAsync(RunningEntry.Id, cancellationToken)
                : 0;
            ResetBreakReminderStreak();
            _breakReminderEntryBaselineSeconds = 0;
            await RecordAutomaticInitialSoftwareAsync(
                RunningEntry.Id,
                initialVisit,
                cancellationToken);
            RunningEntryChanged?.Invoke(this, RunningEntry);
            DataChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (RunningEntry is not { } runningEntry ||
            action.EndingProjectId != runningEntry.ProjectId ||
            action.EndUtc is not { } endUtc)
        {
            ResetAutomaticRecognitionPolicy();
            return;
        }

        await ReviewExcludedSoftwareVisitsAsync(endUtc);
        await ReviewIdleCandidateAsync(endUtc);
        var nextVisit = action.StartingVisit;
        var taskIdForNext = nextVisit is null
            ? null
            : await ResolveAutomaticTaskAsync(nextVisit, cancellationToken);
        CompleteBreakReminderEntry(endUtc);
        var transition = await _store.TransitionRunningTimerAsync(
            runningEntry.Id,
            endUtc,
            nextVisit is null
                ? null
                : new TimerStartRequest(
                    nextVisit.ProjectId!.Value,
                    taskIdForNext,
                    Description: null,
                    Source: TrackingSource.WindowReminder,
                    StartUtc: nextVisit.StartedUtc),
            cancellationToken);

        ResetExcludedSoftwareTracking();
        RunningEntry = transition.RunningEntry;
        RunningExcludedSeconds = 0;
        if (RunningEntry is null)
        {
            ResetBreakReminderStreak();
        }
        else if (nextVisit!.StartedUtc > endUtc)
        {
            ResetBreakReminderStreak();
            _breakReminderEntryBaselineSeconds = 0;
        }
        else
        {
            BeginBreakReminderEntry();
            _breakReminderEntryBaselineSeconds = 0;
        }

        if (RunningEntry is not null && nextVisit is not null)
        {
            await RecordAutomaticInitialSoftwareAsync(
                RunningEntry.Id,
                nextVisit,
                cancellationToken);
        }

        RunningEntryChanged?.Invoke(this, RunningEntry);
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<Guid?> ResolveAutomaticTaskAsync(
        AutomaticRecognitionVisit visit,
        CancellationToken cancellationToken)
    {
        var activity = visit.Activity;
        if (visit.ProjectId is not { } projectId ||
            activity is null ||
            string.IsNullOrWhiteSpace(activity.Title))
        {
            return null;
        }

        var projectTasks = await _store.GetTasksAsync(
            projectId,
            cancellationToken: cancellationToken);
        var suggestion = _taskTitleMatcher.Suggest(activity.Title, projectTasks);
        if (suggestion.ShouldCorrectSavedTaskName &&
            suggestion.SavedTask is { } savedTask &&
            suggestion.FileTaskName is { } fileTaskName &&
            !projectTasks.Any(task =>
                task.Id != savedTask.Id &&
                string.Equals(task.Name, fileTaskName, StringComparison.OrdinalIgnoreCase)))
        {
            await _store.RenameTaskAsync(savedTask.Id, fileTaskName, cancellationToken);
            return savedTask.Id;
        }

        if (suggestion.SavedTask is { } matchedTask)
        {
            return matchedTask.Id;
        }

        return string.IsNullOrWhiteSpace(suggestion.TaskName)
            ? null
            : (await _store.GetOrAddTaskAsync(
                projectId,
                suggestion.TaskName,
                SavedTaskOrigin.Notification,
                cancellationToken)).Id;
    }

    private Task RecordAutomaticInitialSoftwareAsync(
        Guid entryId,
        AutomaticRecognitionVisit visit,
        CancellationToken cancellationToken) =>
        visit.Activity is { } activity && IsTrackableProcess(activity.ProcessName)
            ? RecordSoftwareAsync(
                entryId,
                activity.ProcessName,
                notify: false,
                cancellationToken: cancellationToken)
            : Task.CompletedTask;

    private async Task EvaluateAfterDebounceAsync(WindowActivity activity, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RecognitionStabilityDelay, cancellationToken);
            if (!RecognitionEnabled || !_systemAvailable || _idleMonitor.IsIdle)
            {
                return;
            }

            if (_lastActivity is null ||
                _lastActivity.Handle != activity.Handle ||
                !string.Equals(_lastActivity.Title, activity.Title, StringComparison.Ordinal))
            {
                return;
            }

            var match = GetRelevantRecognitionMatch(activity);
            var projectIds = match.Candidates.Select(candidate => candidate.Project.Id).Distinct().ToArray();
            _promptPolicy.Observe(projectIds, _clock.MonotonicSeconds);
            if (!match.IsMatch || projectIds.All(id => !_promptPolicy.CanPrompt(
                    id,
                    timerRunning: false,
                    systemAvailable: _systemAvailable,
                    monotonicSeconds: _clock.MonotonicSeconds)))
            {
                return;
            }

            _promptPolicy.MarkPrompted(projectIds);
            if (match.IsAmbiguous)
            {
                var selected = await _notificationService.ShowAmbiguousReminderAsync(match.Candidates, cancellationToken);
                if (selected is not null)
                {
                    await ShowRecognitionReminderAsync(
                        selected,
                        activity.Title,
                        activity.ProcessName,
                        activity.Handle,
                        cancellationToken);
                }

                return;
            }

            await ShowRecognitionReminderAsync(
                match.Single!,
                activity.Title,
                activity.ProcessName,
                activity.Handle,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    private RecognitionMatch GetRelevantRecognitionMatch(WindowActivity activity)
    {
        var match = GetTrackableRecognitionMatch(activity);
        if (RunningEntry is null)
        {
            return match;
        }

        var switchCandidates = match.Candidates
            .Where(candidate => candidate.Project.Id != RunningEntry.ProjectId)
            .ToArray();
        return switchCandidates.Length == match.Candidates.Count
            ? match
            : new RecognitionMatch(switchCandidates, match.Score);
    }

    private RecognitionMatch GetTrackableRecognitionMatch(WindowActivity activity)
    {
        var match = _recognitionEngine.Match(activity, _recognitionCandidates);
        var trackableCandidates = match.Candidates
            .Where(candidate => !TryGetExcludedSoftware(
                candidate.Project.Id,
                activity.ProcessName,
                out _))
            .ToArray();
        if (trackableCandidates.Length != match.Candidates.Count)
        {
            match = new RecognitionMatch(trackableCandidates, match.Score);
        }

        return match;
    }

    private async Task ShowRecognitionReminderAsync(
        RecognitionCandidate candidate,
        string windowTitle,
        string processName,
        nint targetWindowHandle,
        CancellationToken cancellationToken)
    {
        if (RunningEntry?.ProjectId == candidate.Project.Id)
        {
            return;
        }

        var isProjectSwitch = RunningEntry is not null;
        var projectTasks = await _store.GetTasksAsync(
            candidate.Project.Id,
            cancellationToken: cancellationToken);
        var taskSuggestion = _taskTitleMatcher.Suggest(windowTitle, projectTasks);
        if (taskSuggestion.ShouldCorrectSavedTaskName &&
            taskSuggestion.SavedTask is { } savedTask &&
            taskSuggestion.FileTaskName is { } fileTaskName &&
            !projectTasks.Any(task =>
                task.Id != savedTask.Id &&
                string.Equals(task.Name, fileTaskName, StringComparison.OrdinalIgnoreCase)))
        {
            await _store.RenameTaskAsync(savedTask.Id, fileTaskName, cancellationToken);
            var correctedTask = savedTask with { Name = fileTaskName };
            projectTasks = projectTasks
                .Select(task => task.Id == correctedTask.Id ? correctedTask : task)
                .ToArray();
            taskSuggestion = taskSuggestion with { SavedTask = correctedTask };
        }
        var correlatedTags = await _store.GetSoftwareTagsByProcessAsync(
            candidate.Project.Id,
            processName,
            cancellationToken);
        var availableTags = await _store.GetTagsAsync(candidate.Project.Id, cancellationToken);
        var response = await _notificationService.ShowProjectReminderAsync(
            candidate,
            projectTasks,
            correlatedTags,
            availableTags,
            isProjectSwitch: isProjectSwitch,
            suggestedTaskId: taskSuggestion.SavedTask?.Id,
            suggestedTaskName: taskSuggestion.TaskName,
            targetWindowHandle: targetWindowHandle,
            cancellationToken: cancellationToken);
        if (response.Result == ReminderResult.Snoozed)
        {
            SnoozeRecognitionReminders();
        }
        else if (response.Result == ReminderResult.Started)
        {
            await StartRecognizedTimerAsync(candidate.Project.Id, response);
        }
    }

    private void SnoozeRecognitionReminders()
    {
        _promptPolicy.Snooze(_clock.MonotonicSeconds, RecognitionSnoozeDuration);
        _recognitionSnooze?.Cancel();
        _recognitionSnooze?.Dispose();
        _recognitionSnooze = new CancellationTokenSource();
        _ = ReevaluateRecognitionAfterSnoozeAsync(_recognitionSnooze.Token);
    }

    private async Task ReevaluateRecognitionAfterSnoozeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RecognitionSnoozeDuration, cancellationToken);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed ||
                    !RecognitionEnabled ||
                    !_systemAvailable ||
                    _idleMonitor.IsIdle ||
                    _lastActivity is not { } activity)
                {
                    return;
                }

                QueueActivity(activity);
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<TimeEntry> StartRecognizedTimerAsync(
        Guid projectId,
        ReminderResponse response)
    {
        if (RunningEntry?.ProjectId == projectId)
        {
            return RunningEntry;
        }

        var taskId = response.TaskId;
        var taskName = string.IsNullOrWhiteSpace(response.TaskName)
            ? null
            : response.TaskName.Trim();
        if (taskId is null && taskName is not null)
        {
            taskId = (await _store.GetOrAddTaskAsync(
                projectId,
                taskName,
                SavedTaskOrigin.Notification,
                CancellationToken.None)).Id;
        }

        var description = TagParser.AppendBracketedTags(
            response.Description,
            response.SelectedTags);
        if (RunningEntry is { } runningEntry)
        {
            await ReviewExcludedSoftwareVisitsAsync(_clock.UtcNow);
            await ReviewIdleCandidateAsync(_clock.UtcNow);
            CompleteBreakReminderEntry();
            var switchUtc = _clock.UtcNow;
            RunningEntry = await _store.SwitchRunningTimerAsync(
                runningEntry.Id,
                projectId,
                taskId,
                description,
                TrackingSource.WindowReminder,
                switchUtc,
                CancellationToken.None);
            ResetExcludedSoftwareTracking();
            RunningExcludedSeconds = 0;
            BeginBreakReminderEntry();
            await RecordInitialSoftwareAsync(
                RunningEntry.Id,
                TrackingSource.WindowReminder,
                CancellationToken.None);
            RunningEntryChanged?.Invoke(this, RunningEntry);
            DataChanged?.Invoke(this, EventArgs.Empty);
            return RunningEntry;
        }

        return await StartTimerAsync(
            projectId,
            TrackingSource.WindowReminder,
            showDetails: false,
            initialDescription: description,
            initialTaskId: taskId,
            cancellationToken: CancellationToken.None);
    }

    internal Task<TimeEntry> StartRecognizedTimerForPreviewAsync(
        Guid projectId,
        ReminderResponse response) =>
        StartRecognizedTimerAsync(projectId, response);

    private void OnIdleStarted(object? sender, DateTimeOffset startedUtc)
    {
        _ = sender;
        if (_idleProtectionState.IsProtected)
        {
            return;
        }

        _ = CompleteActiveExcludedSoftwareVisit(startedUtc);
        if (RunningEntry is not null && _idleCandidate is null)
        {
            _idleCandidate = new IdleCandidate(
                RunningEntry.Id,
                startedUtc,
                IdleCandidateKind.Idle,
                StartedWhileReviewVisible: _idleReviewVisible);
        }

        _notificationService.DismissActive();
    }

    private void OnIdleProtectionStateChanged(
        object? sender,
        IdleProtectionState state)
    {
        _ = sender;
        _dispatcher.BeginInvoke(async () =>
        {
            await ApplyIdleProtectionStateAsync(state);
        });
    }

    private async Task ApplyIdleProtectionStateAsync(IdleProtectionState state)
    {
        if (_disposed)
        {
            return;
        }

        var previous = _idleProtectionState;
        _idleProtectionState = state;
        if (!previous.IsProtected && state.IsProtected)
        {
            if (_idleCandidate is
                {
                    Kind: IdleCandidateKind.Idle,
                    EndUtc: null,
                } candidate)
            {
                var endedUtc = state.ObservedUtc.ToUniversalTime();
                if (endedUtc > candidate.StartUtc)
                {
                    _idleCandidate = candidate with { EndUtc = endedUtc };
                }
            }

            _notificationService.DismissActive();
        }
        else if (previous.IsProtected && !state.IsProtected)
        {
            await ReviewIdleCandidateAsync(state.ObservedUtc);
        }

        IdleProtectionChanged?.Invoke(this, state);
    }

    private async void OnActivityResumed(object? sender, DateTimeOffset resumedUtc)
    {
        _ = sender;
        await ReviewExcludedSoftwareVisitsAsync(resumedUtc);
        await ReviewIdleCandidateAsync(resumedUtc);
        if (AutomaticRecognitionEnabled)
        {
            ResetAutomaticRecognitionPolicy();
            PollAutomaticForeground(force: true);
        }
        else if (_foregroundMonitor.GetCurrentActivity() is { } activity)
        {
            QueueActivity(activity);
        }
    }

    private void OnSessionChanged(object? sender, SystemSessionEvent sessionEvent)
    {
        _ = sender;
        if (sessionEvent == SystemSessionEvent.SigningOut)
        {
            if (_signOutPrepared)
            {
                return;
            }

            try
            {
                PrepareForSignOutAsync(_clock.UtcNow).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }

            return;
        }

        if (sessionEvent == SystemSessionEvent.Ending)
        {
            if (_signOutPrepared)
            {
                return;
            }

            try
            {
                StopForShutdownAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }

            return;
        }

        _dispatcher.BeginInvoke(() => _ = HandleSessionChangedAsync(sessionEvent));
    }

    private async Task PrepareForSignOutAsync(DateTimeOffset signedOutUtc)
    {
        _systemAvailable = false;
        _notificationService.DismissActive();
        _recognitionDebounce?.Cancel();
        ResetAutomaticRecognitionPolicy();
        _ = CompleteActiveExcludedSoftwareVisit(signedOutUtc);
        if (RunningEntry is null)
        {
            return;
        }

        if (SessionTrackingBehavior == SessionTrackingBehavior.StopTimer)
        {
            await StopRunningForSignOutAsync(signedOutUtc);
            return;
        }

        signedOutUtc = signedOutUtc.ToUniversalTime();
        await _store.CheckpointRunningTimerAsync(signedOutUtc);
        await _store.SetSettingAsync(
            SessionTrackingSettings.ResumeMarkerKey,
            SessionTrackingSettings.FormatResumeMarker(RunningEntry.Id, signedOutUtc));
        _signOutPrepared = true;
    }

    private async Task HandleSessionChangedAsync(
        SystemSessionEvent sessionEvent,
        DateTimeOffset? eventUtc = null)
    {
        var nowUtc = (eventUtc ?? _clock.UtcNow).ToUniversalTime();
        switch (sessionEvent)
        {
            case SystemSessionEvent.Locked:
            case SystemSessionEvent.Suspending:
                _systemAvailable = false;
                _notificationService.DismissActive();
                _recognitionDebounce?.Cancel();
                ResetAutomaticRecognitionPolicy();
                _ = CompleteActiveExcludedSoftwareVisit(nowUtc);
                if (_idleCandidate is
                    {
                        Kind: IdleCandidateKind.Idle,
                        EndUtc: not null,
                    })
                {
                    await ReviewIdleCandidateAsync(nowUtc);
                }

                if (SessionTrackingBehavior == SessionTrackingBehavior.StopTimer)
                {
                    await StopRunningForUnavailableSessionAsync(nowUtc);
                    break;
                }

                if (RunningEntry is { } runningEntry)
                {
                    var unavailableSinceUtc = nowUtc < runningEntry.StartUtc
                        ? runningEntry.StartUtc
                        : nowUtc;
                    if (_idleCandidate?.EntryId == runningEntry.Id &&
                        _idleCandidate.StartUtc < unavailableSinceUtc)
                    {
                        unavailableSinceUtc = _idleCandidate.StartUtc;
                    }

                    var label = sessionEvent == SystemSessionEvent.Locked
                        ? "Windows locked"
                        : "Computer asleep";
                    if (_idleCandidate?.Kind == IdleCandidateKind.SessionUnavailable &&
                        !string.Equals(_idleCandidate.Label, label, StringComparison.Ordinal))
                    {
                        label = "Windows locked or asleep";
                    }

                    _idleCandidate = new IdleCandidate(
                        runningEntry.Id,
                        unavailableSinceUtc,
                        IdleCandidateKind.SessionUnavailable,
                        label);
                }

                break;

            case SystemSessionEvent.Unlocked:
            case SystemSessionEvent.Resumed:
                _systemAvailable = true;
                await ReviewIdleCandidateAsync(nowUtc);
                await ReviewExcludedSoftwareVisitsAsync(nowUtc);
                await ShowPendingStoppedSessionEntryAsync();
                await Task.Delay(300);
                if (AutomaticRecognitionEnabled)
                {
                    ResetAutomaticRecognitionPolicy();
                    PollAutomaticForeground(force: true);
                }
                else if (_foregroundMonitor.GetCurrentActivity() is { } activity)
                {
                    QueueActivity(activity);
                }

                break;

            case SystemSessionEvent.SigningOut:
            case SystemSessionEvent.Ending:
                break;
        }
    }

    private async Task StopRunningForUnavailableSessionAsync(DateTimeOffset stoppedUtc)
    {
        if (RunningEntry is null)
        {
            _idleCandidate = null;
            return;
        }

        _sessionStoppedEntryPendingReview = await _store.StopRunningTimerAsync(stoppedUtc);
        await _store.SetSettingAsync(
            SessionTrackingSettings.ReviewEntryKey,
            _sessionStoppedEntryPendingReview?.Id.ToString("D") ?? string.Empty);
        _idleCandidate = null;
        ResetExcludedSoftwareTracking();
        RunningEntry = null;
        RunningExcludedSeconds = 0;
        RunningEntryChanged?.Invoke(this, null);
        DataChanged?.Invoke(this, EventArgs.Empty);
        ResetAutomaticRecognitionPolicy();
    }

    private async Task StopRunningForSignOutAsync(DateTimeOffset stoppedUtc)
    {
        var stoppedEntry = await _store.StopRunningTimerAsync(stoppedUtc);
        await _store.SetSettingAsync(
            SessionTrackingSettings.ResumeMarkerKey,
            string.Empty);
        await _store.SetSettingAsync(
            SessionTrackingSettings.ReviewEntryKey,
            stoppedEntry?.Id.ToString("D") ?? string.Empty);
        _idleCandidate = null;
        ResetExcludedSoftwareTracking();
        RunningEntry = null;
        RunningExcludedSeconds = 0;
        _signOutPrepared = true;
        ResetAutomaticRecognitionPolicy();
    }

    private async Task ShowPendingStoppedSessionEntryAsync()
    {
        if (_sessionStoppedEntryPendingReview is not { } stoppedEntry)
        {
            return;
        }

        _sessionStoppedEntryPendingReview = null;
        await _store.SetSettingAsync(
            SessionTrackingSettings.ReviewEntryKey,
            string.Empty);
        await RequestDetailsAsync(
            stoppedEntry,
            CancellationToken.None,
                heading: SessionReturnPromptTitle);
    }

    private async Task ReviewIdleCandidateAsync(
        DateTimeOffset resumedUtc,
        bool? removeOverride = null)
    {
        var candidate = _idleCandidate;
        if (candidate?.Kind == IdleCandidateKind.SessionUnavailable && !_systemAvailable)
        {
            return;
        }

        _idleCandidate = null;
        var reviewedUntilUtc = candidate?.EndUtc ?? resumedUtc;
        if (candidate is null ||
            RunningEntry?.Id != candidate.EntryId ||
            reviewedUntilUtc <= candidate.StartUtc)
        {
            return;
        }

        var duration = reviewedUntilUtc - candidate.StartUtc;
        var isUnavailableSession = candidate.Kind == IdleCandidateKind.SessionUnavailable;
        if (!isUnavailableSession &&
            ShortIdleReviewPolicy.IsAccumulatedInterval(duration))
        {
            if (AddAccumulatedAwayInterval(
                    candidate.EntryId,
                    candidate.StartUtc,
                    reviewedUntilUtc))
            {
                await ReviewAccumulatedAwayTimeAsync(removeOverride);
            }

            return;
        }

        var message = isUnavailableSession
            ? $"{candidate.Label ?? "Windows was unavailable"} for {FormatDuration(duration)}.\n\nCut this inactive period from the work duration?"
            : $"You were idle for {FormatDuration(duration)}.\n\nCut this idle period from the work duration?";
        var title = isUnavailableSession
            ? SessionReturnPromptTitle
            : candidate.StartedWhileReviewVisible
                ? RepeatedIdlePromptTitle
                : "Review idle time";
        var shouldRemove = removeOverride ?? await ShowIdleReviewPromptAsync(message, title);
        if (shouldRemove)
        {
            var reason = isUnavailableSession
                ? candidate.Label ?? "Windows unavailable"
                : "Idle";
            await _store.AddExclusionAsync(
                candidate.EntryId,
                candidate.StartUtc,
                reviewedUntilUtc,
                reason);
            if (RunningEntry?.Id == candidate.EntryId)
            {
                RunningExcludedSeconds = await _store.GetEntryExcludedSecondsAsync(candidate.EntryId);
            }

            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        if (!isUnavailableSession)
        {
            ResetBreakReminderStreak();
        }

        if (isUnavailableSession)
        {
            await _store.SetSettingAsync(
                SessionTrackingSettings.ResumeMarkerKey,
                string.Empty);
        }
    }

    private async Task<bool> ShowIdleReviewPromptAsync(string message, string title)
    {
        await _idleReviewGate.WaitAsync();
        try
        {
            _idleReviewVisible = true;
            return _idleReviewPrompt(message, title) == MessageBoxResult.Yes;
        }
        finally
        {
            _idleReviewVisible = false;
            _idleReviewGate.Release();
        }
    }

    private static MessageBoxResult ShowIdleReviewPrompt(string message, string title) =>
        MessageBox.ShowTopmost(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

    internal void ObserveActivityForPreview(WindowActivity activity) => QueueActivity(activity);

    internal async Task AdvanceAutomaticRecognitionForPreviewAsync(double seconds)
    {
        await _automaticRecognitionGate.WaitAsync();
        try
        {
            await ProcessAutomaticRecognitionActionsLockedAsync(
                _clock.MonotonicSeconds + Math.Max(0, seconds),
                CancellationToken.None);
        }
        finally
        {
            _automaticRecognitionGate.Release();
        }
    }

    internal void BeginIdleForPreview(DateTimeOffset startedUtc) =>
        OnIdleStarted(this, startedUtc);

    internal Task CompleteIdleForPreviewAsync(DateTimeOffset resumedUtc) =>
        ReviewIdleCandidateAsync(resumedUtc);

    internal void SetIdleReviewPromptForPreview(
        Func<string, string, MessageBoxResult>? prompt) =>
        _idleReviewPrompt = prompt ?? ShowIdleReviewPrompt;

    internal Task ApplyIdleProtectionStateForPreviewAsync(
        IdleProtectionState state) =>
        ApplyIdleProtectionStateAsync(state);

    internal bool HasExcludedSoftwareCandidateForPreview =>
        _activeExcludedSoftwareKey is not null ||
        _excludedSoftwareReviews.Values.Any(review => review.PendingIntervals.Count > 0);

    internal bool IsSoftwareExcludedForPreview(Guid projectId, string processName) =>
        TryGetExcludedSoftware(projectId, processName, out _);

    internal int ExcludedSoftwarePromptCountForPreview =>
        _excludedSoftwarePromptCountForPreview;

    internal int AccumulatedAwayPromptCountForPreview =>
        _accumulatedAwayPromptCountForPreview;

    internal long PendingAccumulatedAwaySecondsForPreview =>
        GetAccumulatedSeconds(
            _accumulatedAwayReview?.PendingIntervals ?? [],
            _clock.UtcNow);

    internal long BreakReminderStreakSecondsForPreview =>
        RunningEntry is null ? 0 : CurrentBreakReminderStreakSeconds();

    internal int AccumulatedAwayNextPromptMultiplierForPreview =>
        _accumulatedAwayReview?.NextPromptMultiplier ?? 1;

    internal Task ReloadAccumulatedAwayReviewForPreviewAsync() =>
        RestoreAccumulatedAwayReviewAsync(CancellationToken.None);

    internal async Task PruneAccumulatedAwayReviewForPreviewAsync(
        DateTimeOffset nowUtc)
    {
        await _accumulatedAwayReviewGate.WaitAsync();
        try
        {
            EnsureAccumulatedAwayReview(nowUtc);
            await PersistAccumulatedAwayReviewAsync();
        }
        finally
        {
            _accumulatedAwayReviewGate.Release();
        }
    }

    internal long GetPendingExcludedSoftwareSecondsForPreview(string processName)
    {
        var processKey = NormalizeProcessName(processName);
        return _excludedSoftwareReviews.TryGetValue(processKey, out var review)
            ? review.PendingIntervals.Sum(interval =>
                Math.Max(0, (long)(interval.EndUtc - interval.StartUtc).TotalSeconds))
            : 0;
    }

    internal async Task CompleteExcludedSoftwareVisitForPreviewAsync(
        DateTimeOffset endUtc,
        bool remove)
    {
        var processKey = CompleteActiveExcludedSoftwareVisit(endUtc);
        if (processKey is not null)
        {
            await ReviewExcludedSoftwareAsync(processKey, remove);
        }
    }

    internal async Task AddIdleIntervalForPreviewAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        bool remove,
        Guid? entryId = null)
    {
        if (RunningEntry is not { } entry)
        {
            return;
        }

        var duration = endUtc.ToUniversalTime() - startUtc.ToUniversalTime();
        if (ShortIdleReviewPolicy.IsAccumulatedInterval(duration))
        {
            if (AddAccumulatedAwayInterval(
                    entryId ?? entry.Id,
                    startUtc,
                    endUtc,
                    requireCurrentEntry: entryId is null))
            {
                await ReviewAccumulatedAwayTimeAsync(remove);
            }

            return;
        }

        if (duration > TimeSpan.Zero)
        {
            _idleCandidate = new IdleCandidate(
                entry.Id,
                startUtc.ToUniversalTime(),
                IdleCandidateKind.Idle);
            await ReviewIdleCandidateAsync(endUtc.ToUniversalTime(), remove);
        }
    }

    internal Task ReviewTimeExclusionForPreviewAsync(
        DateTimeOffset endUtc,
        bool remove) =>
        ReviewExcludedSoftwareVisitsAsync(
            endUtc,
            remove,
            ignoreThreshold: true);

    internal Task HandleSessionChangedForPreviewAsync(
        SystemSessionEvent sessionEvent,
        DateTimeOffset eventUtc) =>
        sessionEvent == SystemSessionEvent.SigningOut
            ? PrepareForSignOutAsync(eventUtc)
            : HandleSessionChangedAsync(sessionEvent, eventUtc);

    internal async Task ResumeUnavailableSessionForPreviewAsync(
        DateTimeOffset resumedUtc,
        bool remove)
    {
        _systemAvailable = true;
        await ReviewIdleCandidateAsync(resumedUtc, remove);
    }

    internal Task ReviewRecoveredSessionForPreviewAsync(bool remove) =>
        _idleCandidate is { Kind: IdleCandidateKind.SessionUnavailable, EndUtc: { } endUtc }
            ? ReviewIdleCandidateAsync(endUtc, remove)
            : Task.CompletedTask;

    private async Task RequestDetailsAsync(
        TimeEntry entry,
        CancellationToken cancellationToken,
        bool canRip = false,
        string? heading = null)
    {
        var options = await _store.GetProjectOptionsAsync(cancellationToken);
        var isUnassigned = entry.ProjectId == SystemEntityIds.UnassignedProjectId;
        var project = options.FirstOrDefault(option => option.ProjectId == entry.ProjectId);
        DetailsRequested?.Invoke(this, new EntryDetailsRequest(
            entry.Id,
            entry.ProjectId,
            isUnassigned
                ? "Choose a project"
                : project?.DisplayName ?? "Archived project",
            entry.TaskId,
            entry.Description,
            canRip,
            AllowProjectSelection: isUnassigned,
            Heading: heading,
            Source: entry.Source));
    }

    private async void OnCheckpointTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (RunningEntry is null)
        {
            return;
        }

        try
        {
            await _store.CheckpointRunningTimerAsync(_clock.UtcNow);
            RunningEntry = RunningEntry with { LastCheckpointUtc = _clock.UtcNow };
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    private async void OnTargetReviewTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        await TryShowScheduledTargetReviewAsync();
    }

    private async Task TryShowScheduledTargetReviewAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed || !TargetReviewSchedule.Enabled || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var localDate = TimeZoneInfo.ConvertTime(_clock.UtcNow, TimeZoneInfo.Local).Date;
        if (!TargetReviewSchedule.IsDueOn(localDate))
        {
            return;
        }

        var enteredGate = false;
        try
        {
            await _targetReviewGate.WaitAsync(cancellationToken);
            enteredGate = true;
            var dueDate = TargetReviewSettings.FormatDate(localDate);
            if (string.Equals(
                    await _store.GetSettingAsync(TargetReviewSettings.LastShownDateKey, cancellationToken),
                    dueDate,
                    StringComparison.Ordinal))
            {
                return;
            }

            var items = await GetTargetReviewItemsAsync(cancellationToken);
            if (items.Count == 0)
            {
                return;
            }

            await _store.SetSettingAsync(TargetReviewSettings.LastShownDateKey, dueDate, cancellationToken);
            await _notificationService.ShowTargetReviewAsync(items, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
        finally
        {
            if (enteredGate)
            {
                _targetReviewGate.Release();
            }
        }
    }

    private async Task<IReadOnlyList<TargetReviewItem>> GetTargetReviewItemsAsync(
        CancellationToken cancellationToken)
    {
        var nowUtc = _clock.UtcNow;
        var week = TrackingPeriodCalculator.CurrentWeek(nowUtc, TimeZoneInfo.Local);
        var month = TrackingPeriodCalculator.CurrentMonth(nowUtc, TimeZoneInfo.Local);
        var projects = await _store.GetProjectsAsync(cancellationToken: cancellationToken);
        var clients = await _store.GetClientsAsync(cancellationToken: cancellationToken);
        var targets = await _store.GetCustomTargetsAsync(cancellationToken);
        var weeklyRows = await _store.GetReportAsync(
                week.StartUtc,
                week.EndUtc,
                new ReportFilter(),
                cancellationToken);
        var monthlyRows = await _store.GetReportAsync(
                month.StartUtc,
                month.EndUtc,
                new ReportFilter(),
                cancellationToken);
        var weeklyByProject = weeklyRows
            .GroupBy(row => row.ProjectId)
            .ToDictionary(
                group => group.Key,
                group => TargetCadenceUsesShortIdle(targets, group.Key, CustomTargetCadence.Weekly)
                    ? group.Sum(row => row.DurationWithShortIdleSeconds)
                    : group.Sum(row => row.DurationSeconds));
        var monthlyByProject = monthlyRows
            .GroupBy(row => row.ProjectId)
            .ToDictionary(
                group => group.Key,
                group => TargetCadenceUsesShortIdle(targets, group.Key, CustomTargetCadence.Monthly)
                    ? group.Sum(row => row.DurationWithShortIdleSeconds)
                    : group.Sum(row => row.DurationSeconds));
        var debtByProject = (await _store.GetProjectTargetDebtsAsync(
                nowUtc,
                TimeZoneInfo.Local,
                cancellationToken))
            .ToDictionary(debt => debt.ProjectId);
        var clientNames = clients.ToDictionary(client => client.Id, client => client.Name);

        return projects
            .Where(project =>
                project.WeeklyTargetHours is not null ||
                project.MonthlyTargetHours is not null ||
                debtByProject.TryGetValue(project.Id, out var debt) && debt.OutstandingSeconds > 0)
            .Select(project => new TargetReviewItem(
                project.Id,
                clientNames.GetValueOrDefault(project.ClientId, "Archived client"),
                project.Name,
                project.Color,
                weeklyByProject.GetValueOrDefault(project.Id),
                project.WeeklyTargetHours,
                monthlyByProject.GetValueOrDefault(project.Id),
                project.MonthlyTargetHours,
                debtByProject.GetValueOrDefault(project.Id)))
            .ToArray();
    }

    private static bool TargetCadenceUsesShortIdle(
        IReadOnlyCollection<CustomTarget> targets,
        Guid projectId,
        CustomTargetCadence cadence)
    {
        var matching = targets
            .Where(target => target.ProjectId == projectId && target.Cadence == cadence)
            .ToArray();
        return matching.Length > 0 && matching.All(target =>
            target.DurationMetric == TargetDurationMetric.IncludingShortIdle);
    }

    private async Task TryShowBreakReminderAsync()
    {
        if (_disposed || RunningEntry is null || !await _breakReminderGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            EnsureBreakReminderEntry();
            var intervalSeconds = (long)BreakReminderIntervalMinutes * 60;
            var completedIntervals = CurrentBreakReminderStreakSeconds() / intervalSeconds;
            if (completedIntervals == 0 || completedIntervals <= _breakReminderLastShownInterval)
            {
                return;
            }

            var today = GetLocalDate(_clock.UtcNow);
            _breakReminderDailyUsage = _breakReminderDailyUsage.LocalDate == today
                ? _breakReminderDailyUsage
                : BreakReminderSettings.ParseDailyUsage(
                    await _store.GetSettingAsync(BreakReminderSettings.DailyUsageKey),
                    today);
            var message = BreakReminderSettings.SelectMessage(
                _breakReminderEnabledMessageIds,
                _breakReminderDailyUsage,
                _clock.UtcNow);
            if (message is null)
            {
                return;
            }

            _breakReminderLastShownInterval = completedIntervals;
            await _notificationService.ShowBreakReminderAsync(BreakReminderPlacement, message.Text);
            _breakReminderDailyUsage.Counts[message.Id] =
                _breakReminderDailyUsage.Counts.GetValueOrDefault(message.Id) + 1;
            await _store.SetSettingAsync(
                BreakReminderSettings.DailyUsageKey,
                BreakReminderSettings.SerializeDailyUsage(_breakReminderDailyUsage));
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
        finally
        {
            _breakReminderGate.Release();
        }
    }

    private void ResetBreakReminderStreak()
    {
        _breakReminderCompletedSeconds = 0;
        _breakReminderLastShownInterval = 0;
        _breakReminderEntryId = null;
        _breakReminderEntryBaselineSeconds = 0;
        BeginBreakReminderEntry();
    }

    private async Task ReloadBreakReminderMessagesAsync(CancellationToken cancellationToken)
    {
        _breakReminderEnabledMessageIds = BreakReminderSettings.ParseEnabledMessageIds(
            await _store.GetSettingAsync(BreakReminderSettings.EnabledMessageIdsKey, cancellationToken));
        _breakReminderDailyUsage = BreakReminderSettings.ParseDailyUsage(
            await _store.GetSettingAsync(BreakReminderSettings.DailyUsageKey, cancellationToken),
            GetLocalDate(_clock.UtcNow));
    }

    private void BeginBreakReminderEntry()
    {
        _breakReminderEntryId = RunningEntry?.Id;
        _breakReminderEntryBaselineSeconds = CurrentRunningElapsedSeconds();
    }

    private void CompleteBreakReminderEntry(DateTimeOffset? endedUtc = null)
    {
        if (RunningEntry is null || _breakReminderEntryId != RunningEntry.Id)
        {
            return;
        }

        var elapsedSeconds = endedUtc is null
            ? CurrentRunningElapsedSeconds()
            : Math.Max(
                0,
                (long)Math.Floor(
                    (endedUtc.Value.ToUniversalTime() - RunningEntry.StartUtc).TotalSeconds) -
                RunningExcludedSeconds);
        _breakReminderCompletedSeconds += Math.Max(
            0,
            elapsedSeconds - _breakReminderEntryBaselineSeconds);
    }

    private void EnsureBreakReminderEntry()
    {
        if (_breakReminderEntryId != RunningEntry?.Id)
        {
            BeginBreakReminderEntry();
        }
    }

    private long CurrentBreakReminderStreakSeconds() =>
        _breakReminderCompletedSeconds + Math.Max(
            0,
            CurrentRunningElapsedSeconds() - _breakReminderEntryBaselineSeconds);

    private long CurrentRunningElapsedSeconds() =>
        Math.Max(0, (long)Math.Floor(RunningElapsed.TotalSeconds));

    private void OnSecondTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (AutomaticRecognitionEnabled && _systemAvailable && !_idleMonitor.IsIdle)
        {
            PollAutomaticForeground();
            _ = ProcessAutomaticRecognitionActionsAsync();
        }

        TimerTick?.Invoke(this, EventArgs.Empty);
        _ = TryShowBreakReminderAsync();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{Math.Max(1, (int)Math.Round(duration.TotalMinutes))} minutes";
    }

    private readonly record struct AutomaticForegroundKey(
        nint Handle,
        string Title,
        string ProcessName)
    {
        public static AutomaticForegroundKey From(WindowActivity activity) =>
            new(activity.Handle, activity.Title, activity.ProcessName);
    }

    private enum IdleCandidateKind
    {
        Idle,
        SessionUnavailable,
    }

    private sealed record ExcludedSoftwareInterval(
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc);

    private sealed record AccumulatedAwayInterval(
        Guid EntryId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc);

    private sealed class AccumulatedAwayReview
    {
        public int NextPromptMultiplier { get; set; } = 1;
        public List<AccumulatedAwayInterval> PendingIntervals { get; } = [];
    }

    private sealed record AccumulatedAwayState(
        DateOnly LocalDate,
        int NextPromptMultiplier,
        AccumulatedAwayInterval[] PendingIntervals);

    private sealed class ExcludedSoftwareReview(
        Guid entryId,
        string processKey,
        string label)
    {
        public Guid EntryId { get; } = entryId;
        public string ProcessKey { get; } = processKey;
        public string Label { get; set; } = label;
        public DateTimeOffset? ActiveSinceUtc { get; set; }
        public bool? RemoveDecision { get; set; }
        public List<ExcludedSoftwareInterval> PendingIntervals { get; } = [];
    }

    private sealed record IdleCandidate(
        Guid EntryId,
        DateTimeOffset StartUtc,
        IdleCandidateKind Kind,
        string? Label = null,
        DateTimeOffset? EndUtc = null,
        bool StartedWhileReviewVisible = false);
}

public sealed record EntryDetailsRequest(
    Guid EntryId,
    Guid ProjectId,
    string DisplayProject,
    Guid? TaskId,
    string? Description,
    bool CanRip = false,
    bool AllowProjectSelection = false,
    string? Heading = null,
    TrackingSource Source = TrackingSource.Manual);
