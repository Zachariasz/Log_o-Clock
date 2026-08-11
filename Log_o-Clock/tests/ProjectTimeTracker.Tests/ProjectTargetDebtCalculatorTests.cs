using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class ProjectTargetDebtCalculatorTests
{
    [Fact]
    public void DailyTargetTakesPriorityWhenComputingRepaymentCapacity()
    {
        var project = CreateProject(daily: 8, weekly: 10, monthly: 160);

        var capacity = ProjectTargetDebtCalculator.GetRepaymentCapacitySeconds(
            project,
            [9 * 3600L, 7 * 3600L],
            [20 * 3600L],
            200 * 3600L);

        Assert.Equal(3600, capacity);
    }

    [Fact]
    public void WeeklyThenMonthlyTargetProvideTheFallbackRepaymentBaseline()
    {
        var weeklyProject = CreateProject(weekly: 10, monthly: 160);
        var monthlyProject = CreateProject(monthly: 160);

        var weeklyCapacity = ProjectTargetDebtCalculator.GetRepaymentCapacitySeconds(
            weeklyProject,
            [],
            [8 * 3600L, 14 * 3600L],
            190 * 3600L);
        var monthlyCapacity = ProjectTargetDebtCalculator.GetRepaymentCapacitySeconds(
            monthlyProject,
            [],
            [],
            170 * 3600L);

        Assert.Equal(4 * 3600, weeklyCapacity);
        Assert.Equal(10 * 3600, monthlyCapacity);
    }

    [Fact]
    public void SurplusAtMonthEndPaysExistingDebtBeforeNewMonthlyShortfallIsAdded()
    {
        var project = CreateProject(daily: 8, monthly: 160);
        var monthEnd = new DateTimeOffset(2026, 7, 31, 22, 0, 0, TimeSpan.Zero);

        var debt = ProjectTargetDebtCalculator.Calculate(
            project,
            [
                new TargetDebtAdjustment(monthEnd.AddMonths(-1), 5 * 3600, 0),
                new TargetDebtAdjustment(monthEnd, 6 * 3600, 2 * 3600),
            ]);

        Assert.Equal(9 * 3600, debt.OutstandingSeconds);
    }

    [Fact]
    public void DisabledCarryOverDoesNotProduceDebt()
    {
        var project = CreateProject(daily: 8, monthly: 160, carryDebt: false);

        var debt = ProjectTargetDebtCalculator.Calculate(
            project,
            [new TargetDebtAdjustment(DateTimeOffset.UtcNow, 6 * 3600, 0)]);

        Assert.Equal(0, debt.OutstandingSeconds);
        Assert.Equal(TargetDebtRepaymentBasis.None, debt.RepaymentBasis);
    }

    [Fact]
    public void DatedCancellationClearsExistingDebtWithoutSuppressingLaterDebt()
    {
        var project = CreateProject(monthly: 160);
        var firstMonthEnd = new DateTimeOffset(2026, 6, 30, 22, 0, 0, TimeSpan.Zero);
        var canceledAt = firstMonthEnd.AddDays(5);
        var secondMonthEnd = firstMonthEnd.AddMonths(1);

        var debt = ProjectTargetDebtCalculator.Calculate(
            project,
            [
                new TargetDebtAdjustment(firstMonthEnd, 6 * 3600, 0),
                new TargetDebtAdjustment(canceledAt, 0, 0, 6 * 3600),
                new TargetDebtAdjustment(secondMonthEnd, 4 * 3600, 0),
            ]);

        Assert.Equal(4 * 3600, debt.OutstandingSeconds);
    }

    private static Project CreateProject(
        double? daily = null,
        double? weekly = null,
        double? monthly = null,
        bool carryDebt = true) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Target project",
            "#339CFF",
            DailyTargetHours: daily,
            WeeklyTargetHours: weekly,
            MonthlyTargetHours: monthly,
            CarryOverTargetDebtEnabled: carryDebt);
}
