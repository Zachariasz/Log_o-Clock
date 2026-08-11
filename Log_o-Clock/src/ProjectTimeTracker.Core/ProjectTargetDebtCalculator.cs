namespace ProjectTimeTracker.Core;

/// <summary>
/// Calculates an opt-in monthly target debt. A month can add debt only from
/// its unmet monthly target; repayment comes only from surplus relative to the
/// most specific configured baseline: daily, then weekly, then monthly.
/// </summary>
public static class ProjectTargetDebtCalculator
{
    public static TargetDebtRepaymentBasis GetRepaymentBasis(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.DailyTargetHours is > 0
            ? TargetDebtRepaymentBasis.Daily
            : project.WeeklyTargetHours is > 0
                ? TargetDebtRepaymentBasis.Weekly
                : project.MonthlyTargetHours is > 0
                    ? TargetDebtRepaymentBasis.Monthly
                    : TargetDebtRepaymentBasis.None;
    }

    public static long GetRepaymentCapacitySeconds(
        Project project,
        IEnumerable<long> dailyPeriodSeconds,
        IEnumerable<long> weeklyPeriodSeconds,
        long monthlyPeriodSeconds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(dailyPeriodSeconds);
        ArgumentNullException.ThrowIfNull(weeklyPeriodSeconds);

        return GetRepaymentBasis(project) switch
        {
            TargetDebtRepaymentBasis.Daily => SumSurplus(
                dailyPeriodSeconds,
                ToSeconds(project.DailyTargetHours)),
            TargetDebtRepaymentBasis.Weekly => SumSurplus(
                weeklyPeriodSeconds,
                ToSeconds(project.WeeklyTargetHours)),
            TargetDebtRepaymentBasis.Monthly => Math.Max(
                0,
                monthlyPeriodSeconds - ToSeconds(project.MonthlyTargetHours)),
            _ => 0,
        };
    }

    public static ProjectTargetDebt Calculate(
        Project project,
        IEnumerable<TargetDebtAdjustment> adjustments)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(adjustments);

        var basis = GetRepaymentBasis(project);
        if (!project.CarryOverTargetDebtEnabled ||
            project.MonthlyTargetHours is not > 0 ||
            basis == TargetDebtRepaymentBasis.None)
        {
            return ProjectTargetDebt.None(project.Id);
        }

        long outstanding = 0;
        foreach (var adjustment in adjustments
                     .Where(adjustment =>
                         adjustment.DebtAddedSeconds > 0 ||
                         adjustment.RepaymentCapacitySeconds > 0 ||
                         adjustment.DebtCanceledSeconds > 0)
                     .OrderBy(adjustment => adjustment.OccurredUtc)
                     // A period's surplus pays pre-existing debt first. Its
                     // just-finished month's shortfall is added afterward, so
                     // the same worked hour can never reduce debt twice.
                     .ThenBy(adjustment => adjustment.DebtCanceledSeconds > 0
                         ? 2
                         : adjustment.DebtAddedSeconds > 0 ? 1 : 0))
        {
            var capacity = Math.Max(0, adjustment.RepaymentCapacitySeconds);
            outstanding -= Math.Min(outstanding, capacity);
            outstanding = checked(outstanding + Math.Max(0, adjustment.DebtAddedSeconds));
            var canceled = Math.Max(0, adjustment.DebtCanceledSeconds);
            outstanding -= Math.Min(outstanding, canceled);
        }

        return new ProjectTargetDebt(project.Id, outstanding, basis);
    }

    public static long MonthlyShortfallSeconds(Project project, long monthlyPeriodSeconds)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Math.Max(0, ToSeconds(project.MonthlyTargetHours) - Math.Max(0, monthlyPeriodSeconds));
    }

    private static long SumSurplus(IEnumerable<long> periods, long baselineSeconds) =>
        periods.Sum(seconds => Math.Max(0, seconds - baselineSeconds));

    private static long ToSeconds(double? targetHours) =>
        targetHours is > 0
            ? checked((long)Math.Round(targetHours.Value * TimeSpan.TicksPerHour / TimeSpan.TicksPerSecond,
                MidpointRounding.AwayFromZero))
            : 0;
}
