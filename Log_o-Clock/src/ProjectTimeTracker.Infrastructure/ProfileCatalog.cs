using System.Text.Json;

namespace ProjectTimeTracker.Infrastructure;

public sealed record TrackerProfile(
    Guid Id,
    string Name,
    DateTimeOffset CreatedUtc,
    bool UsesRootDirectory);

public sealed class ProfileCatalog
{
    public static readonly Guid DefaultProfileId =
        Guid.Parse("37f12cd6-7d8f-49f5-bfd1-3fe88df07b3d");

    private const int CurrentVersion = 1;
    private const int MaximumNameLength = 40;
    private const string CatalogFileName = "profiles.json";
    private const string ProfilesDirectoryName = "Profiles";
    private const string RemovedDirectoryName = "Removed";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _rootDirectory;
    private readonly string _catalogPath;
    private CatalogState _state;

    private ProfileCatalog(string rootDirectory, CatalogState state)
    {
        _rootDirectory = rootDirectory;
        _catalogPath = Path.Combine(rootDirectory, CatalogFileName);
        _state = state;
    }

    public string RootDirectory => _rootDirectory;

    public IReadOnlyList<TrackerProfile> Profiles
    {
        get
        {
            lock (_gate)
            {
                return _state.Profiles
                    .OrderBy(profile => profile.UsesRootDirectory ? 0 : 1)
                    .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
            }
        }
    }

    public TrackerProfile ActiveProfile
    {
        get
        {
            lock (_gate)
            {
                return FindProfile(_state.ActiveProfileId);
            }
        }
    }

    public static ProfileCatalog Load(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var fullRoot = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(fullRoot);
        var catalogPath = Path.Combine(fullRoot, CatalogFileName);
        if (!File.Exists(catalogPath))
        {
            var created = new TrackerProfile(
                DefaultProfileId,
                "Default",
                DateTimeOffset.UtcNow,
                UsesRootDirectory: true);
            var initialState = new CatalogState
            {
                Version = CurrentVersion,
                ActiveProfileId = created.Id,
                Profiles = [created],
            };
            var catalog = new ProfileCatalog(fullRoot, initialState);
            catalog.Save();
            return catalog;
        }

        CatalogState? state;
        try
        {
            state = JsonSerializer.Deserialize<CatalogState>(
                File.ReadAllText(catalogPath),
                JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The profile catalog at '{catalogPath}' is not valid JSON.",
                exception);
        }

        ValidateState(state, catalogPath);
        return new ProfileCatalog(fullRoot, state!);
    }

    public string GetDataDirectory(Guid profileId)
    {
        lock (_gate)
        {
            var profile = FindProfile(profileId);
            return profile.UsesRootDirectory
                ? _rootDirectory
                : Path.Combine(
                    _rootDirectory,
                    ProfilesDirectoryName,
                    profile.Id.ToString("N"));
        }
    }

    public TrackerProfile Add(string name)
    {
        lock (_gate)
        {
            var normalized = ValidateName(name);
            EnsureUniqueName(normalized);
            var profile = new TrackerProfile(
                Guid.NewGuid(),
                normalized,
                DateTimeOffset.UtcNow,
                UsesRootDirectory: false);
            Directory.CreateDirectory(GetProfileDirectory(profile));
            _state.Profiles.Add(profile);
            Save();
            return profile;
        }
    }

    public TrackerProfile Rename(Guid profileId, string name)
    {
        lock (_gate)
        {
            var normalized = ValidateName(name);
            EnsureUniqueName(normalized, profileId);
            var index = _state.Profiles.FindIndex(profile => profile.Id == profileId);
            if (index < 0)
            {
                throw new KeyNotFoundException("The selected profile no longer exists.");
            }

            var renamed = _state.Profiles[index] with { Name = normalized };
            _state.Profiles[index] = renamed;
            Save();
            return renamed;
        }
    }

    public TrackerProfile SetActive(Guid profileId)
    {
        lock (_gate)
        {
            var profile = FindProfile(profileId);
            Directory.CreateDirectory(GetProfileDirectory(profile));
            _state.ActiveProfileId = profile.Id;
            Save();
            return profile;
        }
    }

    public void Remove(Guid profileId)
    {
        lock (_gate)
        {
            var profile = FindProfile(profileId);
            if (profile.UsesRootDirectory)
            {
                throw new InvalidOperationException(
                    "The built-in Default profile cannot be removed because it contains the original app data.");
            }

            if (_state.Profiles.Count <= 1)
            {
                throw new InvalidOperationException("At least one profile must remain.");
            }

            var originalActiveProfileId = _state.ActiveProfileId;
            if (_state.ActiveProfileId == profileId)
            {
                _state.ActiveProfileId = _state.Profiles
                    .First(candidate => candidate.Id != profileId)
                    .Id;
            }

            _state.Profiles.RemoveAll(candidate => candidate.Id == profileId);
            try
            {
                Save();
                ArchiveProfileDirectory(profile);
            }
            catch
            {
                _state.Profiles.Add(profile);
                _state.ActiveProfileId = originalActiveProfileId;
                Save();
                throw;
            }
        }
    }

    private static string ValidateName(string name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A profile name is required.", nameof(name));
        }

        if (normalized.Length > MaximumNameLength)
        {
            throw new ArgumentException(
                $"Profile names can contain at most {MaximumNameLength} characters.",
                nameof(name));
        }

        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Profile names cannot contain control characters.",
                nameof(name));
        }

        return normalized;
    }

    private void EnsureUniqueName(string name, Guid? exceptProfileId = null)
    {
        if (_state.Profiles.Any(profile =>
                profile.Id != exceptProfileId &&
                string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"A profile named '{name}' already exists.");
        }
    }

    private TrackerProfile FindProfile(Guid profileId) =>
        _state.Profiles.FirstOrDefault(profile => profile.Id == profileId)
        ?? throw new KeyNotFoundException("The selected profile no longer exists.");

    private string GetProfileDirectory(TrackerProfile profile) =>
        profile.UsesRootDirectory
            ? _rootDirectory
            : Path.Combine(
                _rootDirectory,
                ProfilesDirectoryName,
                profile.Id.ToString("N"));

    private void ArchiveProfileDirectory(TrackerProfile profile)
    {
        var source = GetProfileDirectory(profile);
        if (!Directory.Exists(source))
        {
            return;
        }

        var removedRoot = Path.Combine(
            _rootDirectory,
            ProfilesDirectoryName,
            RemovedDirectoryName);
        Directory.CreateDirectory(removedRoot);
        var safeTimestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var destination = Path.Combine(
            removedRoot,
            $"{profile.Id:N}-{safeTimestamp}");
        Directory.Move(source, destination);
    }

    private void Save()
    {
        Directory.CreateDirectory(_rootDirectory);
        var temporaryPath = _catalogPath + ".tmp";
        var json = JsonSerializer.Serialize(_state, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _catalogPath, overwrite: true);
    }

    private static void ValidateState(CatalogState? state, string catalogPath)
    {
        if (state is null ||
            state.Version != CurrentVersion ||
            state.Profiles.Count == 0 ||
            state.Profiles.Any(profile =>
                profile.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(profile.Name)) ||
            state.Profiles.Select(profile => profile.Id).Distinct().Count() != state.Profiles.Count ||
            state.Profiles.Select(profile => profile.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != state.Profiles.Count ||
            state.Profiles.Count(profile => profile.UsesRootDirectory) != 1 ||
            state.Profiles.All(profile =>
                profile.Id != DefaultProfileId ||
                !profile.UsesRootDirectory) ||
            state.Profiles.All(profile => profile.Id != state.ActiveProfileId))
        {
            throw new InvalidDataException(
                $"The profile catalog at '{catalogPath}' is invalid or unsupported.");
        }
    }

    private sealed class CatalogState
    {
        public int Version { get; set; }
        public Guid ActiveProfileId { get; set; }
        public List<TrackerProfile> Profiles { get; set; } = [];
    }
}
