using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Tests;

public sealed class IdleProtectionSettingsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void ProtectionSettingsDefaultToEnabled(string? storedValue, bool expected)
    {
        Assert.Equal(expected, IdleProtectionSettings.ParseEnabled(storedValue));
    }

    [Fact]
    public void CombinedReasonsRemainProtectedUntilEveryReasonEnds()
    {
        var combined = new IdleProtectionState(
            IdleProtectionReason.CommunicationAudio | IdleProtectionReason.VideoPlayback,
            CallsAvailable: true,
            VideoAvailable: true,
            IsInitialized: true,
            DateTimeOffset.UtcNow);
        var videoOnly = combined with
        {
            ActiveReasons = IdleProtectionReason.VideoPlayback,
        };
        var none = combined with
        {
            ActiveReasons = IdleProtectionReason.None,
        };

        Assert.True(combined.IsProtected);
        Assert.True(videoOnly.IsProtected);
        Assert.False(none.IsProtected);
    }
}
