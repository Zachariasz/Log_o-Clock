using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class RecognitionPromptPolicyTests
{
    [Fact]
    public void DismissedVisitRemainsSuppressedWhileProjectStaysActive()
    {
        var project = Guid.NewGuid();
        var policy = new RecognitionPromptPolicy(TimeSpan.Zero);
        policy.Observe([project], 0);
        policy.MarkPrompted([project]);
        policy.Observe([project], 20);
        Assert.False(policy.CanPrompt(project, timerRunning: false, systemAvailable: true, monotonicSeconds: 20));
    }

    [Fact]
    public void LeavingAndReturningImmediatelyAllowsAnotherPrompt()
    {
        var project = Guid.NewGuid();
        var policy = new RecognitionPromptPolicy(TimeSpan.Zero);
        policy.Observe([project], 0);
        policy.MarkPrompted([project]);
        policy.Observe([], 10);
        policy.Observe([project], 10.01);
        Assert.True(policy.CanPrompt(project, timerRunning: false, systemAvailable: true, monotonicSeconds: 10.01));
    }

    [Fact]
    public void SwitchingDirectlyToAnotherProjectAndBackStartsANewVisit()
    {
        var firstProject = Guid.NewGuid();
        var secondProject = Guid.NewGuid();
        var policy = new RecognitionPromptPolicy(TimeSpan.Zero);
        policy.Observe([firstProject], 0);
        policy.MarkPrompted([firstProject]);
        policy.Observe([secondProject], 1);
        policy.Observe([firstProject], 2);
        Assert.True(policy.CanPrompt(firstProject, timerRunning: false, systemAvailable: true, monotonicSeconds: 2));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void TimerOrUnavailableSystemSuppressesPrompt(bool timerRunning, bool systemAvailable)
    {
        var project = Guid.NewGuid();
        var policy = new RecognitionPromptPolicy(TimeSpan.Zero);
        policy.Observe([project], 0);
        Assert.False(policy.CanPrompt(project, timerRunning, systemAvailable, monotonicSeconds: 0));
    }

    [Fact]
    public void SnoozeSuppressesAllProjectRemindersForTheRequestedDuration()
    {
        var firstProject = Guid.NewGuid();
        var secondProject = Guid.NewGuid();
        var policy = new RecognitionPromptPolicy(TimeSpan.Zero);

        policy.Observe([firstProject, secondProject], 100);
        policy.MarkPrompted([firstProject]);
        policy.Snooze(100, TimeSpan.FromMinutes(5));

        Assert.False(policy.CanPrompt(firstProject, timerRunning: false, systemAvailable: true, monotonicSeconds: 399.99));
        Assert.False(policy.CanPrompt(secondProject, timerRunning: false, systemAvailable: true, monotonicSeconds: 399.99));
        Assert.True(policy.CanPrompt(firstProject, timerRunning: false, systemAvailable: true, monotonicSeconds: 400));
        Assert.True(policy.CanPrompt(secondProject, timerRunning: false, systemAvailable: true, monotonicSeconds: 400));
    }
}
