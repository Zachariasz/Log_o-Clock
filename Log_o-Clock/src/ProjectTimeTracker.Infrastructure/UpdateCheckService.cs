using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Infrastructure;

public sealed class UpdateCheckService
{
    private readonly IUpdateSettingsStore _store;
    private readonly IGitHubReleaseClient _releaseClient;
    private readonly IClock _clock;
    private readonly Version _installedVersion;
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    public UpdateCheckService(
        IUpdateSettingsStore store,
        IGitHubReleaseClient releaseClient,
        IClock clock,
        Version installedVersion)
    {
        _store = store;
        _releaseClient = releaseClient;
        _clock = clock;
        _installedVersion = new Version(installedVersion.Major, installedVersion.Minor, Math.Max(0, installedVersion.Build));
        State = new UpdateCheckState(true, UpdateCheckStatus.NotChecked, _installedVersion, null, null, null, null);
    }

    public event EventHandler<UpdateCheckState>? StateChanged;

    public UpdateCheckState State { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var automaticChecksEnabled = UpdateCheckSettings.ParseAutomaticChecksEnabled(
            await _store.GetSettingAsync(UpdateCheckSettings.AutomaticChecksEnabledKey, cancellationToken));
        var lastSuccessfulCheckUtc = ParseTimestamp(await _store.GetSettingAsync(
            UpdateCheckSettings.LastSuccessfulCheckUtcKey,
            cancellationToken));
        var latestVersion = ParseVersion(await _store.GetSettingAsync(
            UpdateCheckSettings.LatestVersionKey,
            cancellationToken));
        var releasePageUri = ParseReleasePageUri(await _store.GetSettingAsync(
            UpdateCheckSettings.ReleasePageUrlKey,
            cancellationToken));
        var lastResult = await _store.GetSettingAsync(UpdateCheckSettings.LastResultKey, cancellationToken);
        var status = latestVersion is not null && releasePageUri is not null
            ? latestVersion.CompareTo(_installedVersion) > 0
                ? UpdateCheckStatus.UpdateAvailable
                : UpdateCheckStatus.UpToDate
            : string.Equals(lastResult, UpdateCheckSettings.NoReleaseResult, StringComparison.Ordinal)
                ? UpdateCheckStatus.NoRelease
                : UpdateCheckStatus.NotChecked;
        Publish(new UpdateCheckState(
            automaticChecksEnabled,
            status,
            _installedVersion,
            latestVersion,
            releasePageUri,
            lastSuccessfulCheckUtc,
            null));
    }

    public async Task CheckAutomaticallyAsync(CancellationToken cancellationToken = default)
    {
        if (!State.AutomaticChecksEnabled ||
            State.LastSuccessfulCheckUtc is { } lastCheck &&
            _clock.UtcNow - lastCheck < UpdateCheckSettings.AutomaticCheckInterval)
        {
            return;
        }

        await CheckAsync(isManual: false, cancellationToken);
    }

    public async Task CheckManuallyAsync(CancellationToken cancellationToken = default) =>
        await CheckAsync(isManual: true, cancellationToken);

    public async Task SetAutomaticChecksEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _store.SetSettingAsync(
            UpdateCheckSettings.AutomaticChecksEnabledKey,
            enabled ? "true" : "false",
            cancellationToken);
        Publish(State with { AutomaticChecksEnabled = enabled });
    }

    private async Task CheckAsync(bool isManual, CancellationToken cancellationToken)
    {
        await _checkGate.WaitAsync(cancellationToken);
        try
        {
            var release = await _releaseClient.GetLatestReleaseAsync(cancellationToken);
            var checkedAtUtc = _clock.UtcNow;
            if (release is null)
            {
                await PersistSuccessfulResultAsync(
                    checkedAtUtc,
                    latestVersion: null,
                    releasePageUri: null,
                    UpdateCheckSettings.NoReleaseResult,
                    cancellationToken);
                Publish(State with
                {
                    Status = UpdateCheckStatus.NoRelease,
                    LatestVersion = null,
                    ReleasePageUri = null,
                    LastSuccessfulCheckUtc = checkedAtUtc,
                    ErrorMessage = null,
                });
                return;
            }

            if (!UpdateCheckSettings.TryParseReleaseVersion(release.TagName, out var latestVersion))
            {
                throw new InvalidDataException("GitHub returned a release tag that is not a supported version.");
            }

            await PersistSuccessfulResultAsync(
                checkedAtUtc,
                latestVersion,
                release.ReleasePageUri,
                UpdateCheckSettings.ReleaseResult,
                cancellationToken);
            Publish(State with
            {
                Status = latestVersion.CompareTo(_installedVersion) > 0
                    ? UpdateCheckStatus.UpdateAvailable
                    : UpdateCheckStatus.UpToDate,
                LatestVersion = latestVersion,
                ReleasePageUri = release.ReleasePageUri,
                LastSuccessfulCheckUtc = checkedAtUtc,
                ErrorMessage = null,
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            if (isManual)
            {
                Publish(State with
                {
                    Status = UpdateCheckStatus.Failed,
                    ErrorMessage = "Could not check for updates. Check your internet connection and try again.",
                });
            }
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task PersistSuccessfulResultAsync(
        DateTimeOffset checkedAtUtc,
        Version? latestVersion,
        Uri? releasePageUri,
        string result,
        CancellationToken cancellationToken)
    {
        await _store.SetSettingAsync(
            UpdateCheckSettings.LastSuccessfulCheckUtcKey,
            checkedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        await _store.SetSettingAsync(
            UpdateCheckSettings.LatestVersionKey,
            latestVersion?.ToString(3) ?? string.Empty,
            cancellationToken);
        await _store.SetSettingAsync(
            UpdateCheckSettings.ReleasePageUrlKey,
            releasePageUri?.AbsoluteUri ?? string.Empty,
            cancellationToken);
        await _store.SetSettingAsync(UpdateCheckSettings.LastResultKey, result, cancellationToken);
    }

    private void Publish(UpdateCheckState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        UpdateCheckSettings.TryParseUtc(value, out var timestamp) ? timestamp : null;

    private static Version? ParseVersion(string? value) =>
        UpdateCheckSettings.TryParseReleaseVersion(value, out var version) ? version : null;

    private static Uri? ParseReleasePageUri(string? value) =>
        UpdateCheckSettings.TryParseGitHubReleasePageUri(value, out var uri) ? uri : null;
}
