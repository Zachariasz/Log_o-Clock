using Microsoft.Data.Sqlite;
using ProjectTimeTracker.Core;
using ProjectTimeTracker.Infrastructure;

namespace ProjectTimeTracker.Tests;

public sealed class AutomationLaunchTrackingTests : IAsyncLifetime
{
    private static int _sqliteInitialized;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ProjectTimeTracker.Tests", Guid.NewGuid().ToString("N"));
    private SqliteTrackerStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        if (Interlocked.Exchange(ref _sqliteInitialized, 1) == 0)
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
            SQLitePCL.raw.FreezeProvider();
        }

        Directory.CreateDirectory(_directory);
        _store = new SqliteTrackerStore(Path.Combine(_directory, "test.db"));
        await _store.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("invalid", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    public void ParseEnabledRecognizesBooleanValues(string? stored, bool expected)
    {
        Assert.Equal(expected, AutomationLaunchTrackingSettings.ParseEnabled(stored));
    }

    [Fact]
    public async Task LaunchTrackingSettingCanBeSavedAndRetrieved()
    {
        Assert.False(AutomationLaunchTrackingSettings.ParseEnabled(
            await _store.GetSettingAsync(AutomationLaunchTrackingSettings.EnabledKey)));

        await _store.SetSettingAsync(AutomationLaunchTrackingSettings.EnabledKey, "true");

        Assert.True(AutomationLaunchTrackingSettings.ParseEnabled(
            await _store.GetSettingAsync(AutomationLaunchTrackingSettings.EnabledKey)));

        await _store.SetSettingAsync(AutomationLaunchTrackingSettings.EnabledKey, "false");

        Assert.False(AutomationLaunchTrackingSettings.ParseEnabled(
            await _store.GetSettingAsync(AutomationLaunchTrackingSettings.EnabledKey)));
    }
}
