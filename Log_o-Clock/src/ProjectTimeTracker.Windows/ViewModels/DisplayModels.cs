using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.ViewModels;

public sealed record TimeEntryRow(
    TimeEntryView Entry,
    DateTimeOffset NowUtc,
    IReadOnlyList<TagDefinition> TagDefinitions,
    bool HasTimeOverlap = false)
{
    public string Client => Entry.ClientName;
    public string Project => Entry.ProjectName;
    public string Task => Entry.TaskName ?? "—";
    public string Description => string.IsNullOrWhiteSpace(Entry.Description) ? "—" : Entry.Description;
    public string DescriptionSource => Entry.Description ?? string.Empty;
    public IReadOnlyList<string> TagList => TagParser.Extract(Entry.Description);
    public string TagsSource => TagList.Count == 0 ? string.Empty : string.Join(' ', TagList.Select(tag => $"#{tag}"));
    public string Tags => TagList.Count == 0 ? "—" : string.Join(' ', TagList.Select(tag => $"#{tag}"));
    public string Software => string.IsNullOrWhiteSpace(Entry.SoftwareLabels) ? "-" : Entry.SoftwareLabels;
    public string Day => AppTextCulture.FormatLongDate(Entry.StartUtc.ToLocalTime().Date);
    public DateTimeOffset StartUtc => Entry.StartUtc;
    public DateTimeOffset? EndUtc => Entry.EndUtc;
    public long NetDurationSeconds => Entry.NetDurationSeconds(NowUtc);
    public string Start => AppTextCulture.FormatShortTime(Entry.StartUtc.ToLocalTime());
    public string End => Entry.EndUtc is { } endUtc
        ? AppTextCulture.FormatShortTime(endUtc.ToLocalTime())
        : "Running";
    public string Duration => FormatDuration(TimeSpan.FromSeconds(Entry.NetDurationSeconds(NowUtc)));
    public bool HasExcludedTime => Entry.ExcludedSeconds > 0;
    public string ExcludedDuration => Entry.ExcludedSeconds > 0
        ? $"− {FormatDuration(TimeSpan.FromSeconds(Entry.ExcludedSeconds))} idle"
        : string.Empty;
    public string Payment => Entry.IsPaid ? "Paid" : "Unpaid";
    public string Status => Entry.DetailsPending ? "Pending details" : Entry.IsRunning() ? "Running" : "Complete";

    private static string FormatDuration(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
}

public sealed record ClientRow(Client Client, IReadOnlyList<ClientProjectRow> Projects)
{
    public string Name => Client.Name;
    public string ProjectCount => Projects.Count == 1 ? "1 project" : $"{Projects.Count} projects";
}

public sealed record ClientProjectRow(Project Project)
{
    public string Name => Project.Name;
    public string Color => Project.Color;
}

public sealed record ProjectRow(Project Project, string ClientName, ProjectWorkSummary? WorkSummary)
{
    public string Name => Project.Name;
    public string Client => ClientName;
    public string Color => Project.Color;
    public string TotalTime => FormatDuration(WorkSummary?.TotalSeconds ?? 0);
    public string ActivityDates
    {
        get
        {
            if (WorkSummary?.FirstStartUtc is not { } first || WorkSummary.LastEndUtc is not { } last)
            {
                return "No time logged";
            }

            var firstDate = first.ToLocalTime().Date;
            var lastDate = last.ToLocalTime().Date;
            var firstText = AppTextCulture.FormatShortDate(firstDate);
            return firstDate == lastDate
                ? firstText
                : $"{firstText} – {AppTextCulture.FormatShortDate(lastDate)}";
        }
    }
    public string DailyTarget => FormatHours(Project.DailyTargetHours);
    public string WeeklyTarget => FormatHours(Project.WeeklyTargetHours);
    public string MonthlyTarget => FormatHours(Project.MonthlyTargetHours);
    public string Rate => Project.HourlyRate is null ? "—" : $"{Project.HourlyRate.Value:N2} {Project.Currency}/h";
    public string Status => Project.IsFrozen ? "Frozen" : "Active";

    private static string FormatHours(double? hours) => hours is null ? "—" : $"{hours.Value:0.##} h";
    private static string FormatDuration(long seconds) => $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}";
}

public sealed record TaskRow(SavedTask Task, string ProjectName, string ClientName, TaskWorkSummary? WorkSummary)
{
    public Guid ProjectId => Task.ProjectId;
    public string Name => Task.Name;
    public string Project => ProjectName;
    public string Client => ClientName;
    public string TotalTime => FormatDuration(WorkSummary?.TotalSeconds ?? 0);
    public bool IsTrelloLinked => Task.IsTrelloLinked;
    public string? ExternalUrl => Task.ExternalUrl;

    private static string FormatDuration(long seconds) => $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}";
}

public sealed record TrelloMappingRow(
    TrelloBoardMapping Mapping,
    string ProjectName,
    string ClientName)
{
    public string Board => Mapping.BoardName;
    public string Project => ProjectName;
    public string Client => ClientName;
    public string Lists => string.Join(", ", Mapping.Lists.Select(list => list.ListName));
}

public sealed record RuleRow(RecognitionRule Rule, string ProjectName, string ClientName)
{
    public Guid ProjectId => Rule.ProjectId;
    public string Project => ProjectName;
    public string Client => ClientName;
    public string ProjectGroup => $"{ProjectName} · {ClientName}";
    public string TitlePattern => Rule.TitlePattern;
    public string Process => Rule.ProcessName ?? "Any application";
}

public sealed record TagRow(TagSummary Summary, IReadOnlyList<ProjectOption> Projects)
{
    public TagDefinition Tag => Summary.Tag;
    public string Name => Summary.Tag.Name;
    public string Color => Summary.Tag.Color;
    public string Project => Summary.Tag.IsGlobal
        ? "All projects"
        : ScopeProjects.Count switch
        {
            0 => "No active project",
            1 => ScopeProjects[0].ProjectName,
            _ => $"{ScopeProjects.Count} projects",
        };
    public string Client => Summary.Tag.IsGlobal
        ? "Global"
        : ScopeProjects.Count switch
        {
            0 => "Archived project",
            1 => ScopeProjects[0].ClientName,
            _ => "Project-specific",
        };
    public string Usage => Summary.EntryCount == 1 ? "1 log" : $"{Summary.EntryCount} logs";

    private IReadOnlyList<ProjectOption> ScopeProjects => Projects
        .Where(project => Summary.Tag.AssignedProjectIds.Contains(project.ProjectId))
        .ToArray();
}

public sealed record SoftwareRow(ProjectSoftwareDefinition Setting)
{
    public SoftwareDefinition Software => Setting.Software;
    public Guid ProjectId => Setting.ProjectId;
    public bool IsGlobal => Setting.IsGlobal;
    public string Project => Setting.ProjectName;
    public string Client => Setting.ClientName;
    public string Label => Software.Label;
    public string Process => Software.ProcessName;
    public string TrackingBehavior => Setting.IsExcluded ? "Excluded" : "Tracked";
    public IReadOnlyList<TagDefinition> TagDefinitions => Setting.Tags;
    public string TagsSource => Setting.Tags.Count > 0
        ? string.Join(' ', Setting.Tags.Select(tag => $"#{tag.Name}"))
        : "—";
    public string Usage => Software.EntryCount == 1 ? "1 log" : $"{Software.EntryCount} logs";
}

public sealed record ReportDisplayRow(ReportRow Report)
{
    public string Client => Report.ClientName;
    public string Project => Report.ProjectName;
    public string Task => Report.TaskName;
    public string Duration => $"{Report.DurationSeconds / 3600:00}:{Report.DurationSeconds % 3600 / 60:00}:{Report.DurationSeconds % 60:00}";
    public string Paid => FormatDuration(Report.PaidDurationSeconds);
    public string Unpaid => FormatDuration(Report.UnpaidDurationSeconds);
    public string Rate => Report.HourlyRate is null ? "—" : $"{Report.HourlyRate.Value:N2} {Report.Currency}/h";
    public string Value => Report.HourlyRate is null
        ? "—"
        : $"{Report.HourlyRate.Value * Report.DurationSeconds / 3600m:N2} {Report.Currency}";
    public int Entries => Report.EntryCount;

    private static string FormatDuration(long seconds) => $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}";
}

public sealed record ProjectReportSummaryRow(
    Guid ProjectId,
    string Client,
    string Project,
    string Color,
    long TotalSeconds,
    long TotalWithShortIdleSeconds,
    long PaidSeconds,
    long UnpaidSeconds,
    int EntryCount,
    string Value,
    double Percentage,
    IReadOnlyList<ReportTaskSummaryRow> Tasks,
    long CallSeconds = 0)
{
    public string TotalTime => FormatDuration(TotalSeconds);
    public string TotalWithShortIdle => FormatDuration(TotalWithShortIdleSeconds);
    public string CallTime => FormatDuration(CallSeconds);
    public string Paid => FormatDuration(PaidSeconds);
    public string Unpaid => FormatDuration(UnpaidSeconds);
    public string Share => $"{Percentage:0.#}%";
    public string Entries => EntryCount == 1 ? "1 log" : $"{EntryCount} logs";
    public string LegendDetail => $"{FormatHoursMinutes(TotalSeconds)} h · {Share}";

    private static string FormatDuration(long seconds) => $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}";
    private static string FormatHoursMinutes(long seconds) => $"{seconds / 3600}:{seconds % 3600 / 60:00}";
}

public sealed record ClientReportSummaryRow(
    string Client,
    string Color,
    long TotalSeconds,
    double Percentage)
{
    public string Share => $"{Percentage:0.#}%";
    public string LegendDetail => $"{FormatHoursMinutes(TotalSeconds)} h · {Share}";

    private static string FormatHoursMinutes(long seconds) =>
        $"{seconds / 3600}:{seconds % 3600 / 60:00}";
}

public sealed record ReportTaskSummaryRow(
    Guid ProjectId,
    Guid? TaskId,
    string Task,
    long TotalSeconds,
    long TotalWithShortIdleSeconds,
    long PaidSeconds,
    long UnpaidSeconds,
    int EntryCount,
    decimal? HourlyRate,
    string Currency,
    DateTimeOffset? LatestActivityUtc,
    long CallSeconds = 0)
{
    public bool IsUnassigned => TaskId is null;
    public string TotalTime => FormatDuration(TotalSeconds);
    public string TotalWithShortIdle => FormatDuration(TotalWithShortIdleSeconds);
    public string CallTime => FormatDuration(CallSeconds);
    public string Paid => FormatDuration(PaidSeconds);
    public string Unpaid => FormatDuration(UnpaidSeconds);
    public string Value => HourlyRate is null ? "—" : $"{HourlyRate.Value * TotalSeconds / 3600m:N2} {Currency}";
    public string Entries => EntryCount == 1 ? "1 log" : $"{EntryCount} logs";

    private static string FormatDuration(long seconds) => $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}";
}

public sealed record ProjectTargetRow(
    Project Project,
    string ClientName,
    long DailySeconds,
    long WeeklySeconds,
    long MonthlySeconds,
    ProjectTargetDebt? TargetDebt = null,
    CustomTarget? CustomTarget = null,
    string? DisplayNameOverride = null,
    string? ScopeOverride = null,
    long OneTimeSeconds = 0,
    bool MonthlyOnly = false,
    double? OneTimeTargetHoursOverride = null,
    bool IsGlobalAggregate = false)
{
    public string Client => ScopeOverride ?? ClientName;
    public string ProjectName => DisplayNameOverride ?? Project.Name;
    public Guid? ScopedProjectId => IsGlobalAggregate
        ? null
        : CustomTarget is null
        ? Project.Id
        : CustomTarget.ProjectId;
    public double? DailyTargetHours => MonthlyOnly
        ? null
        : CustomTarget?.Cadence == CustomTargetCadence.Daily
            ? CustomTarget.TargetHours
            : CustomTarget is null ? Project.DailyTargetHours : null;
    public double? WeeklyTargetHours => MonthlyOnly
        ? null
        : CustomTarget?.Cadence == CustomTargetCadence.Weekly
            ? CustomTarget.TargetHours
            : CustomTarget is null ? Project.WeeklyTargetHours : null;
    public double? MonthlyTargetHours => CustomTarget?.Cadence == CustomTargetCadence.Monthly
        ? CustomTarget.TargetHours
        : CustomTarget is null ? Project.MonthlyTargetHours : null;
    public double? OneTimeTargetHours => MonthlyOnly
        ? null
        : OneTimeTargetHoursOverride is { } aggregateOneTime
            ? aggregateOneTime
            : CustomTarget?.Cadence == CustomTargetCadence.OneTime
            ? CustomTarget.TargetHours
            : null;
    public string Daily => FormatProgress(DailySeconds, DailyTargetHours);
    public string Weekly => FormatProgress(WeeklySeconds, WeeklyTargetHours);
    public string Monthly => FormatProgress(MonthlySeconds, MonthlyTargetHours);
    public string OneTime => FormatProgress(OneTimeSeconds, OneTimeTargetHours);
    public double DailyProgress => CalculateProgress(DailySeconds, DailyTargetHours);
    public double WeeklyProgress => CalculateProgress(WeeklySeconds, WeeklyTargetHours);
    public double MonthlyProgress => CalculateProgress(MonthlySeconds, MonthlyTargetHours);
    public bool HasMonthlyTarget => MonthlyTargetHours is > 0;
    public bool IsOneTimeTarget =>
        OneTimeTargetHours is not null &&
        DailyTargetHours is null &&
        WeeklyTargetHours is null &&
        MonthlyTargetHours is null;
    public bool IsDailyReached =>
        DailyTargetHours is > 0 &&
        DailySeconds >= DailyTargetHours.Value * 3600d;

    public bool HasDebt => TargetDebt is { OutstandingSeconds: > 0 };
    public bool CanCancelDebt =>
        (CustomTarget is
         {
             ProjectId: not null,
             Cadence: CustomTargetCadence.Monthly,
         } ||
         (CustomTarget is null &&
          !IsGlobalAggregate &&
          Project.MonthlyTargetHours is > 0)) &&
        HasDebt;
    public bool HasCanceledDebt => TargetDebt?.HasCanceledDebt == true;

    public string Debt => TargetDebt is { OutstandingSeconds: > 0 } debt
        ? TargetDebtText.Format(debt.OutstandingSeconds)
        : string.Empty;

    public string CanceledDebt => TargetDebt is { HasCanceledDebt: true, LastCanceledUtc: { } canceledUtc } debt
        ? debt.OutstandingSeconds > 0
            ? $"Lowered by {TargetDebtText.Format(debt.CanceledSeconds)} · {AppTextCulture.FormatShortDate(canceledUtc.ToLocalTime())}"
            : $"Canceled {TargetDebtText.Format(debt.CanceledSeconds)} · {AppTextCulture.FormatShortDate(canceledUtc.ToLocalTime())}"
        : string.Empty;

    public ProjectTargetRow AsMonthlyOnly() => this with { MonthlyOnly = true };

    public static ProjectTargetRow FromCustomTarget(CustomTargetRow row)
    {
        var target = row.Target;
        var project = row.ScopedProject ?? new Project(
            target.ProjectId ?? target.Id,
            SystemEntityIds.UnassignedClientId,
            row.Project,
            "#766F80",
            IsArchived: target.ProjectId is not null);
        var scope = target.ProjectId is null
            ? "All projects"
            : $"{row.Project} · {row.Client}";
        return new ProjectTargetRow(
            project,
            row.Client,
            target.Cadence == CustomTargetCadence.Daily ? row.CompletedSeconds : 0,
            target.Cadence == CustomTargetCadence.Weekly ? row.CompletedSeconds : 0,
            target.Cadence == CustomTargetCadence.Monthly ? row.CompletedSeconds : 0,
            CustomTarget: target,
            DisplayNameOverride: row.Name,
            ScopeOverride: scope,
            OneTimeSeconds: target.Cadence == CustomTargetCadence.OneTime ? row.CompletedSeconds : 0,
            TargetDebt: row.TargetDebt);
    }

    private static string FormatProgress(long seconds, double? targetHours)
    {
        if (targetHours is null)
        {
            return "—";
        }

        var percent = targetHours.Value <= 0 ? 0 : seconds / 3600d / targetHours.Value * 100d;
        return $"{seconds / 3600:00}:{seconds % 3600 / 60:00} / {targetHours.Value:0.##} h ({percent:0}%)";
    }

    private static double CalculateProgress(long seconds, double? targetHours) =>
        targetHours is > 0
            ? Math.Clamp(seconds / 3600d / targetHours.Value, 0d, 1d)
            : -1d;

    private static string FormatDuration(long seconds) =>
        $"{seconds / 3600:00}:{seconds % 3600 / 60:00}";

}

public interface ITargetManagementRow
{
    string Name { get; }
    string Project { get; }
    string Client { get; }
    string Type { get; }
    string TargetTime { get; }
    string Counts { get; }
    string Progress { get; }
    string Debt { get; }
}

public sealed record CustomTargetRow(
    CustomTarget Target,
    string ProjectName,
    string ClientName,
    long CompletedSeconds,
    Project? ScopedProject = null,
    ProjectTargetDebt? TargetDebt = null) : ITargetManagementRow
{
    public string Name => Target.Name;
    public string Project => ProjectName;
    public string Client => ClientName;
    public string Type => Target.Cadence switch
    {
        CustomTargetCadence.Daily => "Daily",
        CustomTargetCadence.Weekly => "Weekly",
        CustomTargetCadence.Monthly => "Monthly",
        CustomTargetCadence.OneTime => "One time",
        _ => "Unknown",
    };
    public string TargetTime => $"{Target.TargetHours:0.##} h";
    public string Counts => Target.DurationMetric == TargetDurationMetric.IncludingShortIdle
        ? "Time + short idle"
        : "Active time";
    public string Progress
    {
        get
        {
            var percentage = CompletedSeconds / 3600d / Target.TargetHours * 100d;
            return $"{FormatDuration(CompletedSeconds)} / {Target.TargetHours:0.##} h ({percentage:0}%)";
        }
    }
    public bool CanCancelDebt =>
        Target.Cadence == CustomTargetCadence.Monthly &&
        Target.ProjectId is not null &&
        TargetDebt is { OutstandingSeconds: > 0 };
    public string Debt
    {
        get
        {
            if (Target.Cadence != CustomTargetCadence.Monthly || Target.ProjectId is null)
            {
                return "\u2014";
            }

            if (TargetDebt is { HasCanceledDebt: true, LastCanceledUtc: { } canceledUtc } canceledDebt)
            {
                return canceledDebt.OutstandingSeconds > 0
                    ? $"{TargetDebtText.Format(canceledDebt.OutstandingSeconds)} remaining \u00B7 lowered by " +
                      $"{TargetDebtText.Format(canceledDebt.CanceledSeconds)} \u00B7 {AppTextCulture.FormatShortDate(canceledUtc.ToLocalTime())}"
                    : $"Canceled {TargetDebtText.Format(canceledDebt.CanceledSeconds)} \u00B7 {AppTextCulture.FormatShortDate(canceledUtc.ToLocalTime())}";
            }

            return TargetDebt is { OutstandingSeconds: > 0 } debt
                ? TargetDebtText.Format(debt.OutstandingSeconds)
                : "\u2014";
        }
    }

    private static string FormatDuration(long seconds) =>
        $"{seconds / 3600:00}:{seconds % 3600 / 60:00}";
}

public sealed record ProjectConfiguredTargetRow(
    Project ConfiguredProject,
    string ClientName,
    CustomTargetCadence Cadence,
    long CompletedSeconds,
    ProjectTargetDebt? TargetDebt = null) : ITargetManagementRow
{
    public string Name => $"{Type} target";
    public string Project => ConfiguredProject.Name;
    public string Client => ClientName;
    public string Type => Cadence switch
    {
        CustomTargetCadence.Daily => "Daily",
        CustomTargetCadence.Weekly => "Weekly",
        CustomTargetCadence.Monthly => "Monthly",
        _ => "Unknown",
    };
    public double TargetHours => Cadence switch
    {
        CustomTargetCadence.Daily => ConfiguredProject.DailyTargetHours ?? 0,
        CustomTargetCadence.Weekly => ConfiguredProject.WeeklyTargetHours ?? 0,
        CustomTargetCadence.Monthly => ConfiguredProject.MonthlyTargetHours ?? 0,
        _ => 0,
    };
    public string TargetTime => $"{TargetHours:0.##} h";
    public string Counts => "Active time";
    public string Date => "—";
    public bool CanCancelDebt =>
        Cadence == CustomTargetCadence.Monthly &&
        TargetDebt is { OutstandingSeconds: > 0 };
    public string Debt
    {
        get
        {
            if (Cadence != CustomTargetCadence.Monthly)
            {
                return "\u2014";
            }

            if (TargetDebt is { HasCanceledDebt: true, LastCanceledUtc: { } canceledUtc } canceledDebt)
            {
                return canceledDebt.OutstandingSeconds > 0
                    ? $"{TargetDebtText.Format(canceledDebt.OutstandingSeconds)} remaining · lowered by " +
                      $"{TargetDebtText.Format(canceledDebt.CanceledSeconds)} · {AppTextCulture.FormatShortDate(canceledUtc.ToLocalTime())}"
                    : $"Canceled {TargetDebtText.Format(canceledDebt.CanceledSeconds)} · {AppTextCulture.FormatShortDate(canceledUtc.ToLocalTime())}";
            }

            return TargetDebt is { OutstandingSeconds: > 0 } debt
                ? TargetDebtText.Format(debt.OutstandingSeconds)
                : "\u2014";
        }
    }
    public string Progress
    {
        get
        {
            var percentage = TargetHours <= 0 ? 0 : CompletedSeconds / 3600d / TargetHours * 100d;
            return $"{CompletedSeconds / 3600:00}:{CompletedSeconds % 3600 / 60:00} / {TargetHours:0.##} h ({percentage:0}%)";
        }
    }
}

public sealed record TagOption(string? Value, string Color = "#989BA3")
{
    public string DisplayName => Value is null ? "All tags" : Value;
}

public sealed record ClientFilterOption(Guid? ClientId, string DisplayName);

public sealed record ProjectFilterOption(Guid? ProjectId, Guid? ClientId, string DisplayName);

public sealed record TargetProjectFilterOption(Guid? ProjectId, string DisplayName, bool IsGlobal = false);

public sealed record TaskFilterOption(Guid? TaskId, Guid? ProjectId, string DisplayName, bool IsUnassigned = false);

public sealed record PaidFilterOption(PaidStatusFilter Value, string DisplayName);

internal static class TimeEntryViewExtensions
{
    public static bool IsRunning(this TimeEntryView entry) => entry.EndUtc is null;
}
