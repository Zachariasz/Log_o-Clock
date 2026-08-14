using ProjectTimeTracker.Core;
using ProjectTimeTracker.Infrastructure;

namespace ProjectTimeTracker.Tests;

public sealed class UpdateCheckServiceTests
{
    [Fact]
    public async Task AutomaticCheckPersistsAvailableReleaseAndRunsOnlyOncePerDay()
    {
        var settings = new MemorySettingsStore();
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero));
        var client = new FakeReleaseClient(new GitHubRelease(
            "v1.143.0",
            new Uri("https://github.com/Zachariasz/Log_o-Clock/releases/tag/v1.143.0")));
        var service = new UpdateCheckService(settings, client, clock, new Version(1, 142, 2, 0));
        await service.InitializeAsync();

        await service.CheckAutomaticallyAsync();
        await service.CheckAutomaticallyAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, service.State.Status);
        Assert.True(service.State.IsUpdateAvailable);
        Assert.Equal(1, client.RequestCount);
        Assert.Equal("1.143.0", await settings.GetSettingAsync(UpdateCheckSettings.LatestVersionKey));
        Assert.NotNull(await settings.GetSettingAsync(UpdateCheckSettings.LastSuccessfulCheckUtcKey));

        await service.CheckManuallyAsync();

        Assert.Equal(2, client.RequestCount);
    }

    [Fact]
    public async Task DisabledAutomaticChecksDoNotContactGitHubButManualCheckStillWorks()
    {
        var settings = new MemorySettingsStore();
        var client = new FakeReleaseClient(new GitHubRelease(
            "v1.143.0",
            new Uri("https://github.com/Zachariasz/Log_o-Clock/releases/tag/v1.143.0")));
        var service = new UpdateCheckService(
            settings,
            client,
            new FixedClock(DateTimeOffset.UtcNow),
            new Version(1, 142, 2));
        await service.InitializeAsync();
        await service.SetAutomaticChecksEnabledAsync(false);

        await service.CheckAutomaticallyAsync();
        Assert.Equal(0, client.RequestCount);

        await service.CheckManuallyAsync();
        Assert.Equal(1, client.RequestCount);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, service.State.Status);
    }

    [Fact]
    public async Task BackgroundFailuresRemainSilentWhileManualFailuresAreReported()
    {
        var settings = new MemorySettingsStore();
        var client = new FakeReleaseClient(new HttpRequestException("offline"));
        var service = new UpdateCheckService(
            settings,
            client,
            new FixedClock(DateTimeOffset.UtcNow),
            new Version(1, 142, 2));
        await service.InitializeAsync();

        await service.CheckAutomaticallyAsync();
        Assert.Equal(UpdateCheckStatus.NotChecked, service.State.Status);

        await service.CheckManuallyAsync();
        Assert.Equal(UpdateCheckStatus.Failed, service.State.Status);
        Assert.NotNull(service.State.ErrorMessage);
    }

    [Fact]
    public async Task NoReleaseAndOlderReleaseDoNotOfferAnUpdate()
    {
        var settings = new MemorySettingsStore();
        var client = new FakeReleaseClient((GitHubRelease?)null);
        var service = new UpdateCheckService(
            settings,
            client,
            new FixedClock(DateTimeOffset.UtcNow),
            new Version(1, 142, 2));
        await service.InitializeAsync();

        await service.CheckManuallyAsync();

        Assert.Equal(UpdateCheckStatus.NoRelease, service.State.Status);
        Assert.False(service.State.IsUpdateAvailable);
        Assert.Equal(UpdateCheckSettings.NoReleaseResult, await settings.GetSettingAsync(UpdateCheckSettings.LastResultKey));
    }

    private sealed class MemorySettingsStore : IUpdateSettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReleaseClient : IGitHubReleaseClient
    {
        private readonly GitHubRelease? _release;
        private readonly Exception? _exception;

        public FakeReleaseClient(GitHubRelease? release) => _release = release;

        public FakeReleaseClient(Exception exception) => _exception = exception;

        public int RequestCount { get; private set; }

        public Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            RequestCount++;
            return _exception is null
                ? Task.FromResult(_release)
                : Task.FromException<GitHubRelease?>(_exception);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
        public double MonotonicSeconds => 0;
    }
}
