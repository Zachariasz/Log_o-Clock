using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class AutomationLearningPolicyTests
{
    private readonly AutomationLearningPolicy _policy = new();

    [Fact]
    public void ProjectNameIsPreferredAndOnlyStoredTextIsReturned()
    {
        var intent = Intent("Phoenix", "Rig polish");
        var activity = Activity("Private customer file - Phoenix - Blender");

        var decision = _policy.Evaluate(intent, activity, 10);

        Assert.Equal(AutomationLearningDecisionKind.Learn, decision.Kind);
        Assert.Equal("Phoenix", decision.TitlePattern);
        Assert.DoesNotContain("Private", decision.TitlePattern, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskNameIsUsedWhenProjectNameIsAbsent()
    {
        var decision = _policy.Evaluate(
            Intent("Phoenix", "Rig polish"),
            Activity("Rig polish - Blender"),
            10);

        Assert.Equal(AutomationLearningDecisionKind.Learn, decision.Kind);
        Assert.Equal("Rig polish", decision.TitlePattern);
    }

    [Fact]
    public void UnknownTitleRequiresReviewWithoutReturningRawTitle()
    {
        var decision = _policy.Evaluate(
            Intent("Phoenix", "Rig polish"),
            Activity("Confidential-scene-42.blend"),
            10);

        Assert.Equal(AutomationLearningDecisionKind.NeedsTitleReview, decision.Kind);
        Assert.Null(decision.TitlePattern);
    }

    [Fact]
    public void IntentExpiresAfterSixtySeconds()
    {
        var intent = Intent("Phoenix", null);

        Assert.Equal(
            AutomationLearningDecisionKind.Learn,
            _policy.Evaluate(intent, Activity("Phoenix"), 60).Kind);
        Assert.Equal(
            AutomationLearningDecisionKind.Expired,
            _policy.Evaluate(intent, Activity("Phoenix"), 60.001).Kind);
    }

    [Theory]
    [InlineData("blender.exe", "Blender")]
    [InlineData("visual_studio", "Visual Studio")]
    [InlineData("my-app", "My App")]
    public void DefaultLabelsAreReadable(string processName, string expected)
    {
        Assert.Equal(expected, AutomationLearningPolicy.DefaultSoftwareLabel(processName));
    }

    private static AutomationLearningIntent Intent(string projectName, string? taskName) =>
        new(Guid.NewGuid(), projectName, taskName, 0);

    private static WindowActivity Activity(string title) =>
        new(1, title, "blender.exe", DateTimeOffset.UtcNow);
}
