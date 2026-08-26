using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class AutomaticRecognitionPolicyTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartsImmediatelyWhenNoTimerIsRunning()
    {
        var project = Guid.NewGuid();
        var policy = CreatePolicy(runningProjectId: null);

        policy.Observe(project, Origin, 0, Activity(project, Origin));

        var action = Assert.IsType<AutomaticRecognitionAction>(policy.TakeNextAction(0));
        Assert.True(action.IsInitialStart);
        Assert.Equal(project, action.StartingVisit?.ProjectId);
        Assert.Equal(Origin, action.StartingVisit?.StartedUtc);
    }

    [Fact]
    public void SameProjectAcrossDifferentWindowsDoesNotCreateABoundary()
    {
        var project = Guid.NewGuid();
        var policy = CreatePolicy(project);

        policy.Observe(project, Origin.AddMinutes(1), 60, Activity(project, Origin.AddMinutes(1)));
        policy.Observe(project, Origin.AddMinutes(2), 120, Activity(project, Origin.AddMinutes(2)));

        Assert.Null(policy.TakeNextAction(1_000));
        Assert.Single(policy.Timeline);
    }

    [Fact]
    public void ReturningBeforeGraceCancelsStop()
    {
        var project = Guid.NewGuid();
        var policy = CreatePolicy(project);
        policy.Observe(null, Origin.AddMinutes(1), 60, activity: null);
        policy.Observe(project, Origin.AddMinutes(10).AddSeconds(59), 659.999, Activity(project, Origin));
        Assert.Null(policy.TakeNextAction(660));

        policy.Observe(null, Origin.AddMinutes(11), 660, activity: null);
        var action = Assert.IsType<AutomaticRecognitionAction>(policy.TakeNextAction(1_260));
        Assert.True(action.IsStop);
        Assert.Equal(Origin.AddMinutes(11), action.EndUtc);
    }

    [Fact]
    public void ReturningAtExactDeadlineFinalizesOldBoundary()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var policy = CreatePolicy(first);
        var switchedAt = Origin.AddMinutes(1);
        var returnedAt = Origin.AddMinutes(11);
        policy.Observe(second, switchedAt, 60, Activity(second, switchedAt));

        policy.Observe(first, returnedAt, 660, Activity(first, returnedAt));

        var action = Assert.IsType<AutomaticRecognitionAction>(policy.TakeNextAction(660));
        Assert.Equal(first, action.EndingProjectId);
        Assert.Equal(switchedAt, action.EndUtc);
        Assert.Equal(second, action.StartingVisit?.ProjectId);
        Assert.Equal(returnedAt, policy.Timeline[1].StartedUtc);
    }

    [Fact]
    public void DirectProjectSwitchUsesTheRememberedBoundary()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var policy = CreatePolicy(first);
        var boundary = Origin.AddMinutes(2);

        policy.Observe(second, boundary, 120, Activity(second, boundary));
        Assert.Null(policy.TakeNextAction(719.999));

        var action = Assert.IsType<AutomaticRecognitionAction>(policy.TakeNextAction(720));
        Assert.True(action.IsTransition);
        Assert.Equal(first, action.EndingProjectId);
        Assert.Equal(boundary, action.EndUtc);
        Assert.Equal(boundary, action.StartingVisit?.StartedUtc);
        Assert.Equal(second, action.StartingVisit?.ProjectId);
    }

    [Fact]
    public void ReturningToOriginalProjectCancelsEveryDescendant()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var policy = CreatePolicy(first);

        policy.Observe(second, Origin.AddMinutes(1), 60, Activity(second, Origin));
        policy.Observe(third, Origin.AddMinutes(2), 120, Activity(third, Origin));
        policy.Observe(first, Origin.AddMinutes(3), 180, Activity(first, Origin));

        Assert.Null(policy.TakeNextAction(1_000));
        Assert.Single(policy.Timeline);
        Assert.Equal(first, policy.Timeline[0].ProjectId);
    }

    [Fact]
    public void NestedTransitionsRetainIndependentGraceBoundaries()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var policy = CreatePolicy(first);
        var firstBoundary = Origin.AddMinutes(1);
        var secondBoundary = Origin.AddMinutes(3);
        policy.Observe(second, firstBoundary, 60, Activity(second, firstBoundary));
        policy.Observe(third, secondBoundary, 180, Activity(third, secondBoundary));

        var firstAction = Assert.IsType<AutomaticRecognitionAction>(policy.TakeNextAction(660));
        Assert.Equal(firstBoundary, firstAction.EndUtc);
        Assert.Equal(second, firstAction.StartingVisit?.ProjectId);
        Assert.Null(policy.TakeNextAction(779.999));

        var secondAction = Assert.IsType<AutomaticRecognitionAction>(policy.TakeNextAction(780));
        Assert.Equal(secondBoundary, secondAction.EndUtc);
        Assert.Equal(third, secondAction.StartingVisit?.ProjectId);
    }

    [Fact]
    public void UnknownGapEndsOldProjectAndStartsNewProjectLater()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var policy = CreatePolicy(first);
        var stoppedAt = Origin.AddMinutes(1);
        var startedAt = Origin.AddMinutes(4);
        policy.Observe(null, stoppedAt, 60, activity: null);
        policy.Observe(second, startedAt, 240, Activity(second, startedAt));

        var action = Assert.IsType<AutomaticRecognitionAction>(policy.TakeNextAction(660));
        Assert.Equal(stoppedAt, action.EndUtc);
        Assert.Equal(startedAt, action.StartingVisit?.StartedUtc);
    }

    [Fact]
    public void AmbiguityKeepsPreferredProjectOnlyWhenItIsAmongTheMatches()
    {
        var current = Guid.NewGuid();
        var other = Guid.NewGuid();
        var third = Guid.NewGuid();
        var policy = CreatePolicy(current);

        Assert.Equal(current, policy.ResolveProjectId(Match(current, other)));
        Assert.Null(policy.ResolveProjectId(Match(other, third)));
    }

    [Fact]
    public void ShorteningGraceMakesAnExistingBoundaryImmediatelyDue()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var policy = CreatePolicy(first);
        policy.Observe(second, Origin.AddMinutes(1), 60, Activity(second, Origin));
        Assert.Null(policy.TakeNextAction(180));

        policy.SetGracePeriod(TimeSpan.FromMinutes(2));

        Assert.NotNull(policy.TakeNextAction(180));
    }

    [Fact]
    public void DeadlinesUseMonotonicTimeAndRememberedBoundariesAreNormalizedToUtc()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var policy = CreatePolicy(first);
        var observedWithOffset = new DateTimeOffset(2026, 8, 26, 15, 0, 0, TimeSpan.FromHours(2));
        policy.Observe(second, observedWithOffset, 60, Activity(second, observedWithOffset));

        Assert.Null(policy.TakeNextAction(659.999));

        var action = Assert.IsType<AutomaticRecognitionAction>(policy.TakeNextAction(660));
        Assert.Equal(observedWithOffset.ToUniversalTime(), action.EndUtc);
        Assert.Equal(TimeSpan.Zero, action.EndUtc?.Offset);
    }

    [Theory]
    [InlineData(null, false, 10)]
    [InlineData("true", true, 10)]
    [InlineData("TRUE", true, 10)]
    [InlineData("false", false, 10)]
    [InlineData("1", false, 1)]
    [InlineData("1440", false, 1440)]
    [InlineData("0", false, 10)]
    [InlineData("1441", false, 10)]
    public void SettingsUseSafeDefaults(string? stored, bool enabled, int graceMinutes)
    {
        Assert.Equal(enabled, AutomaticRecognitionSettings.ParseEnabled(stored));
        Assert.Equal(graceMinutes, AutomaticRecognitionSettings.ParseGraceMinutes(stored));
    }

    private static AutomaticRecognitionPolicy CreatePolicy(Guid? runningProjectId)
    {
        var policy = new AutomaticRecognitionPolicy(TimeSpan.FromMinutes(10));
        policy.Reset(runningProjectId, Origin, 0);
        return policy;
    }

    private static WindowActivity Activity(Guid projectId, DateTimeOffset observedUtc) =>
        new(1, $"Project {projectId:N}", "editor", observedUtc);

    private static RecognitionMatch Match(params Guid[] projectIds)
    {
        var client = new Client(Guid.NewGuid(), "Client", "#000000");
        return new RecognitionMatch(
            projectIds.Select(projectId => new RecognitionCandidate(
                new Project(projectId, client.Id, projectId.ToString("N"), "#000000"),
                client,
                new RecognitionRule(Guid.NewGuid(), projectId, "match", null))).ToArray(),
            5);
    }
}
