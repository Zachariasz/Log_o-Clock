using ProjectTimeTracker.Infrastructure;

namespace ProjectTimeTracker.Tests;

public sealed class ProfileCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ProjectTimeTracker.ProfileCatalogTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void FirstLoadPreservesOriginalDataDirectoryAsDefaultProfile()
    {
        Directory.CreateDirectory(_directory);
        var originalDatabase = Path.Combine(_directory, "TimeTracker.db");
        File.WriteAllText(originalDatabase, "existing data marker");

        var catalog = ProfileCatalog.Load(_directory);

        Assert.Single(catalog.Profiles);
        Assert.Equal(ProfileCatalog.DefaultProfileId, catalog.ActiveProfile.Id);
        Assert.Equal("Default", catalog.ActiveProfile.Name);
        Assert.Equal(Path.GetFullPath(_directory), catalog.GetDataDirectory(catalog.ActiveProfile.Id));
        Assert.True(File.Exists(originalDatabase));
        Assert.True(File.Exists(Path.Combine(_directory, "profiles.json")));
    }

    [Fact]
    public void AddedProfilesUseDistinctDirectoriesAndPersistTheActiveProfile()
    {
        var catalog = ProfileCatalog.Load(_directory);
        var first = catalog.Add("Alice");
        var second = catalog.Add("Bob");

        Assert.NotEqual(
            catalog.GetDataDirectory(first.Id),
            catalog.GetDataDirectory(second.Id));
        Assert.DoesNotContain(
            catalog.GetDataDirectory(first.Id),
            catalog.GetDataDirectory(ProfileCatalog.DefaultProfileId),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(catalog.GetDataDirectory(first.Id)));
        Assert.True(Directory.Exists(catalog.GetDataDirectory(second.Id)));

        catalog.SetActive(second.Id);
        var reloaded = ProfileCatalog.Load(_directory);

        Assert.Equal(second.Id, reloaded.ActiveProfile.Id);
        Assert.Equal("Bob", reloaded.ActiveProfile.Name);
        Assert.Equal(3, reloaded.Profiles.Count);
    }

    [Fact]
    public void NamesAreCaseInsensitiveAndRenamesPersist()
    {
        var catalog = ProfileCatalog.Load(_directory);
        var profile = catalog.Add("Alice");

        Assert.Throws<InvalidOperationException>(() => catalog.Add(" alice "));
        var renamed = catalog.Rename(profile.Id, "Personal");
        var reloaded = ProfileCatalog.Load(_directory);

        Assert.Equal("Personal", renamed.Name);
        Assert.Equal(
            "Personal",
            reloaded.Profiles.Single(candidate => candidate.Id == profile.Id).Name);
        Assert.Throws<InvalidOperationException>(
            () => catalog.Rename(ProfileCatalog.DefaultProfileId, "personal"));
    }

    [Fact]
    public void RemovingSecondaryProfileArchivesItsDataAndKeepsDefault()
    {
        var catalog = ProfileCatalog.Load(_directory);
        var profile = catalog.Add("Temporary");
        var profileDirectory = catalog.GetDataDirectory(profile.Id);
        File.WriteAllText(Path.Combine(profileDirectory, "TimeTracker.db"), "profile data");
        catalog.SetActive(profile.Id);

        catalog.Remove(profile.Id);

        Assert.Equal(ProfileCatalog.DefaultProfileId, catalog.ActiveProfile.Id);
        Assert.DoesNotContain(catalog.Profiles, candidate => candidate.Id == profile.Id);
        Assert.False(Directory.Exists(profileDirectory));
        var archivedDatabase = Directory.GetFiles(
            Path.Combine(_directory, "Profiles", "Removed"),
            "TimeTracker.db",
            SearchOption.AllDirectories);
        Assert.Single(archivedDatabase);
        Assert.Equal("profile data", File.ReadAllText(archivedDatabase[0]));
        Assert.Throws<InvalidOperationException>(
            () => catalog.Remove(ProfileCatalog.DefaultProfileId));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
