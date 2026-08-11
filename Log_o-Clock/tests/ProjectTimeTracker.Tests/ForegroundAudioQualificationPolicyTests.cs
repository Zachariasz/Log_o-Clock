using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class ForegroundAudioQualificationPolicyTests
{
    [Fact]
    public void ContinuousForegroundRenderQualifiesAtTenSeconds()
    {
        var policy = new ForegroundAudioQualificationPolicy();

        Assert.False(policy.Observe("browser", true, false, 100));
        Assert.False(policy.Observe("browser", true, false, 109.999));
        Assert.True(policy.Observe("browser", true, false, 110));
    }

    [Fact]
    public void SessionEndingBeforeThresholdResetsQualification()
    {
        var policy = new ForegroundAudioQualificationPolicy();

        Assert.False(policy.Observe("browser", true, false, 10));
        Assert.False(policy.Observe("browser", false, false, 19));
        Assert.False(policy.Observe("browser", true, false, 20));
        Assert.False(policy.Observe("browser", true, false, 29));
        Assert.True(policy.Observe("browser", true, false, 30));
    }

    [Fact]
    public void ForegroundApplicationChangeRestartsQualification()
    {
        var policy = new ForegroundAudioQualificationPolicy();

        Assert.False(policy.Observe("browser", true, false, 1));
        Assert.False(policy.Observe("browser", true, false, 9));
        Assert.False(policy.Observe("meeting", true, false, 10));
        Assert.False(policy.Observe("meeting", true, false, 19.9));
        Assert.True(policy.Observe("meeting", true, false, 20));
    }

    [Fact]
    public void ExplicitMusicOrImageNeverQualifiesAndResetsCandidate()
    {
        var policy = new ForegroundAudioQualificationPolicy();

        Assert.False(policy.Observe("player", true, false, 5));
        Assert.False(policy.Observe("player", true, true, 14));
        Assert.False(policy.Observe("player", true, false, 15));
        Assert.False(policy.Observe("player", true, false, 24.9));
        Assert.True(policy.Observe("player", true, false, 25));
    }

    [Fact]
    public void MonotonicClockRollbackRestartsQualification()
    {
        var policy = new ForegroundAudioQualificationPolicy();

        Assert.False(policy.Observe("browser", true, false, 100));
        Assert.False(policy.Observe("browser", true, false, 90));
        Assert.False(policy.Observe("browser", true, false, 99.9));
        Assert.True(policy.Observe("browser", true, false, 100));
    }
}
