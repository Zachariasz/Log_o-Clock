using System.Globalization;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Infrastructure;

public sealed partial class GoogleSheetsSyncService : IGoogleSheetsSyncService
{
    public const int CurrentSyncProtocolVersion = 3;

    private const string ProfileWorksheet = "__LogOClockProfile";
    private const string ChangesWorksheet = "__LogOClockChanges";
    private const string DevicesWorksheet = "__LogOClockDevices";
    private const string LegacyReviewWorksheet = "Legacy Review";
    private static readonly string[] TechnicalWorksheets = [ProfileWorksheet, ChangesWorksheet, DevicesWorksheet];
    private static readonly string[] ChangeHeaders =
    [
        "RevisionId", "EntityType", "EntityId", "ParentRevisionIds", "Operation",
        "DeviceId", "DeviceName", "ChangedUtc", "ContentHash", "PayloadJson",
    ];
    private static readonly string[] DeviceHeaders =
    [
        "DeviceId", "DeviceName", "LastSeenUtc", "EntryId", "Client", "Project",
        "Task", "StartedUtc", "IsRunning",
    ];
    private static readonly string[] Headers =
    [
        "EntryId", "Date", "Start", "End", "Duration", "Client", "Project",
        "Task", "Description", "Tags", "Software", "Paid", "PendingDetails",
        "HourlyRate", "Currency", "Amount", "Source", "Call", "Created at", "Last modified",
    ];

    private readonly ITrackerStore _store;
    private readonly IGoogleSheetsApiClient _api;
    private readonly IGoogleAuthorizationBroker _authorizationBroker;
    private readonly ICredentialStore _credentials;
    private readonly Guid _profileId;
    private string _profileName;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _defaultTimeZone;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _queuedSync;
    private Task? _periodicTask;
    private bool _joiningExisting;
    private bool _localProfileNamePending;
    private bool _localTimeZonePending;
    private TimeSpan _retryDelay = TimeSpan.FromSeconds(15);
    private IReadOnlyList<RemoteTimerStatus> _remoteTimers = [];

    public GoogleSheetsSyncService(
        ITrackerStore store,
        IGoogleSheetsApiClient api,
        IGoogleAuthorizationBroker authorizationBroker,
        ICredentialStore credentials,
        Guid profileId,
        string profileName,
        IClock clock,
        TimeZoneInfo? timeZone = null)
    {
        _store = store;
        _api = api;
        _authorizationBroker = authorizationBroker;
        _credentials = credentials;
        _profileId = profileId;
        _profileName = profileName;
        _clock = clock;
        _defaultTimeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public event EventHandler<GoogleSheetsSyncResult>? SyncCompleted;

    public IReadOnlyList<RemoteTimerStatus> RemoteTimers => _remoteTimers;

    public async Task<GoogleSheetsConnection?> GetConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var json = await _store.GetSettingAsync(GoogleSheetsSettings.ConnectionKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var connection = JsonSerializer.Deserialize<GoogleSheetsConnection>(json)
                ?? throw new InvalidDataException("The saved Google Sheets connection metadata is empty.");
            var normalized = NormalizeConnectionMetadata(connection);
            if (normalized != connection)
            {
                await SaveConnectionAsync(normalized, cancellationToken);
            }
            return normalized;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The saved Google Sheets connection metadata is invalid.", exception);
        }
    }

    public async Task<GoogleSheetsConnection> ConnectAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken = default)
    {
        var (credentials, tokens, user) = await AuthorizeAsync(clientId, clientSecret, cancellationToken);
        await _credentials.SetGoogleSheetsCredentialsAsync(_profileId, credentials, cancellationToken);
        var existing = await GetConnectionAsync(cancellationToken);
        var connection = new GoogleSheetsConnection(
            user.Email,
            user.DisplayName,
            existing?.SpreadsheetId,
            existing?.SpreadsheetUrl,
            StoreExportsInGoogleSheets: true,
            SyncProfileId: existing?.SyncProfileId ?? Guid.NewGuid(),
            DeviceId: existing?.DeviceId ?? Guid.NewGuid(),
            DeviceName: NormalizeDeviceName(existing?.DeviceName),
            PinnedTimeZoneId: existing?.PinnedTimeZoneId ?? _defaultTimeZone.Id,
            SyncProtocolVersion: existing?.SyncProtocolVersion ?? 0);
        _ = tokens;
        _localProfileNamePending = true;
        _localTimeZonePending = true;
        await SaveConnectionAndEnableAsync(connection, cancellationToken);
        return connection;
    }

    public async Task<GoogleSheetsConnection> ConnectExistingAsync(
        string clientId,
        string clientSecret,
        string spreadsheetUrlOrId,
        CancellationToken cancellationToken = default)
    {
        if (await _store.HasUserProfileDataAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "An existing synchronized spreadsheet can be joined only from an empty local profile.");
        }

        var spreadsheetId = ParseSpreadsheetId(spreadsheetUrlOrId);
        var (credentials, tokens, user) = await AuthorizeAsync(clientId, clientSecret, cancellationToken);
        var metadataRows = await _api.ReadRangeAsync(
            tokens.AccessToken,
            spreadsheetId,
            WorksheetRange(ProfileWorksheet, "A1:B10"),
            cancellationToken);
        var metadata = ParseProfileMetadata(metadataRows)
            ?? throw new InvalidOperationException(
                "This spreadsheet has not been upgraded for device synchronization. " +
                "Open it once from the source computer with the current Log O'clock version, then try again.");
        if (metadata.ProtocolVersion > CurrentSyncProtocolVersion)
        {
            throw new InvalidOperationException(
                $"This spreadsheet uses synchronization protocol {metadata.ProtocolVersion}, " +
                $"but this app supports up to {CurrentSyncProtocolVersion}. Update Log O'clock first.");
        }
        if (metadata.ProtocolVersion < CurrentSyncProtocolVersion)
        {
            throw new InvalidOperationException(
                "The source computer has not finished upgrading this spreadsheet for synchronization. " +
                "Let it complete one successful sync, then try again.");
        }

        await _credentials.SetGoogleSheetsCredentialsAsync(_profileId, credentials, cancellationToken);
        _profileName = metadata.ProfileName;
        var connection = new GoogleSheetsConnection(
            user.Email,
            user.DisplayName,
            spreadsheetId,
            $"https://docs.google.com/spreadsheets/d/{spreadsheetId}/edit",
            StoreExportsInGoogleSheets: true,
            SyncProfileId: metadata.ProfileId,
            DeviceId: Guid.NewGuid(),
            DeviceName: NormalizeDeviceName(null),
            PinnedTimeZoneId: metadata.PinnedTimeZoneId,
            SyncProtocolVersion: metadata.ProtocolVersion);
        _joiningExisting = true;
        await SaveConnectionAndEnableAsync(connection, cancellationToken);
        return connection;
    }

    public async Task SetCloudExportEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken)
            ?? throw new InvalidOperationException("Connect Google Sheets before changing its synchronization mode.");
        await SaveConnectionAsync(connection with { StoreExportsInGoogleSheets = enabled }, cancellationToken);
        await _store.SetSettingAsync(
            LogExportDestinationSettings.DestinationKey,
            enabled ? LogExportDestinationSettings.GoogleSheets : LogExportDestinationSettings.Local,
            cancellationToken);
        if (enabled)
        {
            QueueSync();
        }
    }

    public async Task SetDeviceNameAsync(
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken)
            ?? throw new InvalidOperationException("Connect Google Sheets before naming this device.");
        await SaveConnectionAsync(
            connection with { DeviceName = NormalizeDeviceName(deviceName) },
            cancellationToken);
        QueueSync();
    }

    public Task SetProfileNameAsync(
        string profileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _profileName = Required(profileName, nameof(profileName));
        _localProfileNamePending = true;
        QueueSync();
        return Task.CompletedTask;
    }

    public async Task SetPinnedTimeZoneAsync(
        string timeZoneId,
        CancellationToken cancellationToken = default)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(Required(timeZoneId, nameof(timeZoneId)));
        var connection = await GetConnectionAsync(cancellationToken)
            ?? throw new InvalidOperationException("Connect Google Sheets before changing the worksheet time zone.");
        await SaveConnectionAsync(connection with { PinnedTimeZoneId = timeZone.Id }, cancellationToken);
        _localTimeZonePending = true;
        QueueSync();
    }

    public Task<IReadOnlyList<ProfileSyncConflict>> GetConflictsAsync(
        CancellationToken cancellationToken = default) =>
        _store.GetProfileSyncConflictsAsync(cancellationToken);

    public async Task ResolveConflictAsync(
        Guid conflictId,
        ProfileSyncResolution resolution,
        Guid? cloudRevisionId = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken)
            ?? throw new InvalidOperationException("Google Sheets is not connected for this profile.");
        await _store.ResolveProfileSyncConflictAsync(
            conflictId,
            resolution,
            cloudRevisionId,
            connection.DeviceId ?? throw new InvalidOperationException("The local synchronization device ID is missing."),
            NormalizeDeviceName(connection.DeviceName),
            _clock.UtcNow,
            cancellationToken);
        QueueSync();
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _queuedSync?.Cancel();
        await _credentials.DeleteGoogleSheetsCredentialsAsync(_profileId, cancellationToken);
        await _store.SetSettingAsync(GoogleSheetsSettings.ConnectionKey, string.Empty, cancellationToken);
        await _store.SetSettingAsync(
            LogExportDestinationSettings.DestinationKey,
            LogExportDestinationSettings.Local,
            cancellationToken);
        _remoteTimers = [];
    }

    public async Task<GoogleSheetsSyncResult> SyncNowAsync(
        CancellationToken cancellationToken = default)
    {
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            var connection = await GetConnectionAsync(cancellationToken)
                ?? throw new InvalidOperationException("Google Sheets is not connected for this profile.");
            if (!connection.StoreExportsInGoogleSheets)
            {
                return new GoogleSheetsSyncResult(0, 0, _clock.UtcNow);
            }

            var credentials = await _credentials.GetGoogleSheetsCredentialsAsync(_profileId, cancellationToken)
                ?? throw new InvalidOperationException("Google Sheets credentials are missing. Reconnect this profile.");
            try
            {
                var tokens = await _api.RefreshAccessTokenAsync(credentials, cancellationToken);
                var spreadsheet = await EnsureSpreadsheetAsync(tokens.AccessToken, connection, cancellationToken);
                connection = (await GetConnectionAsync(cancellationToken))!;
                var worksheetNames = (await _api.GetWorksheetNamesAsync(
                        tokens.AccessToken,
                        spreadsheet.Id,
                        cancellationToken))
                    .ToHashSet(StringComparer.Ordinal);
                var legacyUpgrade = connection.SyncProtocolVersion < CurrentSyncProtocolVersion && !_joiningExisting;
                await EnsureTechnicalWorksheetsAsync(
                    tokens.AccessToken,
                    spreadsheet.Id,
                    worksheetNames,
                    connection,
                    cancellationToken);
                connection = (await GetConnectionAsync(cancellationToken))!;

                var cloudCursor = await _store.GetProfileSyncCloudCursorAsync(cancellationToken);
                var cloudBatch = await ReadCloudChangesAsync(
                    tokens.AccessToken,
                    spreadsheet.Id,
                    cloudCursor,
                    cancellationToken);
                var cloudChanges = cloudBatch.Changes;
                var imported = 0;
                var changed = false;
                if (_joiningExisting && cloudChanges.Count > 0)
                {
                    var initialPull = await _store.ReconcileProfileSyncChangesAsync(cloudChanges, cancellationToken);
                    imported += initialPull.ImportedCount;
                    changed |= initialPull.DataChanged;
                }

                var seedAll = connection.SyncProtocolVersion < CurrentSyncProtocolVersion && !_joiningExisting;
                var outbox = await _store.CaptureProfileSyncChangesAsync(
                    connection.DeviceId ?? throw new InvalidOperationException("The local synchronization device ID is missing."),
                    NormalizeDeviceName(connection.DeviceName),
                    seedAll,
                    cancellationToken);
                var knownRevisionIds = cloudChanges.Select(change => change.RevisionId).ToHashSet();
                var pendingUploads = outbox.Where(change => !knownRevisionIds.Contains(change.RevisionId)).ToArray();
                if (pendingUploads.Length > 0)
                {
                    await _api.AppendRowsAsync(
                        tokens.AccessToken,
                        spreadsheet.Id,
                        WorksheetRange(ChangesWorksheet, "A:J"),
                        pendingUploads.Select(ToChangeRow).ToArray(),
                        cancellationToken);
                }

                cloudBatch = await ReadCloudChangesAsync(
                    tokens.AccessToken,
                    spreadsheet.Id,
                    cloudCursor,
                    cancellationToken);
                cloudChanges = cloudBatch.Changes;
                var cloudRevisionIds = cloudChanges.Select(change => change.RevisionId).ToHashSet();
                var acknowledged = outbox
                    .Where(change => cloudRevisionIds.Contains(change.RevisionId))
                    .Select(change => change.RevisionId)
                    .ToArray();
                await _store.AcknowledgeProfileSyncChangesAsync(acknowledged, cancellationToken);
                var reconcile = await _store.ReconcileProfileSyncChangesAsync(cloudChanges, cancellationToken);
                imported += reconcile.ImportedCount;
                changed |= reconcile.DataChanged;
                await _store.SetProfileSyncCloudCursorAsync(cloudBatch.NextCursor, cancellationToken);

                var timeZone = ResolvePinnedTimeZone(connection.PinnedTimeZoneId);
                var entries = await _store.GetEntriesAsync(
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.MaxValue,
                    cancellationToken);
                var completedEntries = entries.Where(entry => entry.EndUtc is not null).ToArray();
                var dailyResult = await WriteDailyWorksheetsAsync(
                    tokens.AccessToken,
                    spreadsheet.Id,
                    worksheetNames,
                    completedEntries,
                    timeZone,
                    legacyUpgrade,
                    cancellationToken);
                _remoteTimers = await UpdateAndReadDeviceStatesAsync(
                    tokens.AccessToken,
                    spreadsheet.Id,
                    connection,
                    entries,
                    cancellationToken);

                var conflictCount = (await _store.GetProfileSyncConflictsAsync(cancellationToken)).Count;
                var completed = _clock.UtcNow;
                connection = connection with
                {
                    SpreadsheetId = spreadsheet.Id,
                    SpreadsheetUrl = spreadsheet.Url,
                    LastSuccessfulSyncUtc = completed,
                    LastError = null,
                    RequiresReconnect = false,
                    SyncProtocolVersion = CurrentSyncProtocolVersion,
                };
                await SaveConnectionAsync(connection, cancellationToken);
                await WriteProfileMetadataAsync(tokens.AccessToken, spreadsheet.Id, connection, cancellationToken);
                _joiningExisting = false;
                _retryDelay = TimeSpan.FromSeconds(15);
                var result = new GoogleSheetsSyncResult(
                    dailyResult.WorksheetCount,
                    dailyResult.EntryCount,
                    completed,
                    ImportedCount: imported,
                    UploadedCount: pendingUploads.Length,
                    ConflictCount: conflictCount,
                    DataChanged: changed,
                    RemoteTimers: _remoteTimers,
                    SharedProfileName: _profileName);
                SyncCompleted?.Invoke(this, result);
                return result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var requiresReconnect = exception.Message.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
                                        exception.Message.Contains("credential", StringComparison.OrdinalIgnoreCase);
                await SaveConnectionAsync(connection with
                {
                    LastError = SanitizeError(exception.Message),
                    RequiresReconnect = requiresReconnect,
                }, cancellationToken);
                throw;
            }
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public void QueueSync()
    {
        _queuedSync?.Cancel();
        _queuedSync?.Dispose();
        _queuedSync = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _queuedSync.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                var connection = await GetConnectionAsync(token);
                if (connection?.StoreExportsInGoogleSheets == true)
                {
                    await SyncNowAsync(token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch
            {
                // SyncNow stores a redacted error and the SQLite outbox remains durable.
                // Retry with bounded exponential backoff without interrupting local tracking.
                try
                {
                    await Task.Delay(_retryDelay, token);
                    _retryDelay = TimeSpan.FromSeconds(Math.Min(900, _retryDelay.TotalSeconds * 2));
                    if (!token.IsCancellationRequested)
                    {
                        QueueSync();
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
            }
        }, token);
    }

    public void Start()
    {
        if (_periodicTask is not null)
        {
            return;
        }

        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        QueueSync();
        _periodicTask = RunPeriodicAsync(_lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        _lifetime.Cancel();
        _queuedSync?.Cancel();
        if (_periodicTask is not null)
        {
            try
            {
                await _periodicTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _queuedSync?.Dispose();
        _syncGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task RunPeriodicAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            QueueSync();
        }
    }

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        _ = sender;
        if (e.IsAvailable)
        {
            _retryDelay = TimeSpan.FromSeconds(15);
            QueueSync();
        }
    }

    private async Task<(GoogleSheetsCredentials Credentials, GoogleOAuthTokens Tokens, GoogleUser User)> AuthorizeAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        clientId = Required(clientId, nameof(clientId));
        clientSecret = Required(clientSecret, nameof(clientSecret));
        var authorization = await _authorizationBroker.AuthorizeAsync(clientId, cancellationToken);
        var tokens = await _api.ExchangeAuthorizationCodeAsync(
            clientId,
            clientSecret,
            authorization,
            cancellationToken);
        var refreshToken = tokens.RefreshToken
            ?? throw new InvalidOperationException("Google did not return an offline refresh token.");
        var user = await _api.GetCurrentUserAsync(tokens.AccessToken, cancellationToken);
        return (new GoogleSheetsCredentials(clientId, clientSecret, refreshToken), tokens, user);
    }

    private async Task<GoogleSpreadsheet> EnsureSpreadsheetAsync(
        string accessToken,
        GoogleSheetsConnection connection,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(connection.SpreadsheetId))
        {
            return new GoogleSpreadsheet(
                connection.SpreadsheetId,
                connection.SpreadsheetUrl
                ?? $"https://docs.google.com/spreadsheets/d/{connection.SpreadsheetId}/edit");
        }

        var spreadsheet = await _api.CreateSpreadsheetAsync(
            accessToken,
            $"Log O'clock - {_profileName}",
            cancellationToken);
        await SaveConnectionAsync(connection with
        {
            SpreadsheetId = spreadsheet.Id,
            SpreadsheetUrl = spreadsheet.Url,
            LastError = null,
            RequiresReconnect = false,
        }, cancellationToken);
        return spreadsheet;
    }

    private async Task EnsureTechnicalWorksheetsAsync(
        string accessToken,
        string spreadsheetId,
        ISet<string> worksheetNames,
        GoogleSheetsConnection connection,
        CancellationToken cancellationToken)
    {
        var missing = TechnicalWorksheets.Where(name => !worksheetNames.Contains(name)).ToArray();
        await _api.AddHiddenWorksheetsAsync(accessToken, spreadsheetId, missing, cancellationToken);
        worksheetNames.UnionWith(missing);

        var existingMetadata = ParseProfileMetadata(await _api.ReadRangeAsync(
            accessToken,
            spreadsheetId,
            WorksheetRange(ProfileWorksheet, "A1:B10"),
            cancellationToken));
        if (existingMetadata is not null &&
            connection.SyncProfileId is { } expectedProfileId &&
            existingMetadata.ProfileId != expectedProfileId)
        {
            throw new InvalidOperationException(
                "This spreadsheet belongs to a different synchronized Log O'clock profile.");
        }

        if (existingMetadata is not null)
        {
            if (!_localProfileNamePending)
            {
                _profileName = existingMetadata.ProfileName;
            }
            if (!_localTimeZonePending &&
                !string.Equals(connection.PinnedTimeZoneId, existingMetadata.PinnedTimeZoneId, StringComparison.Ordinal))
            {
                connection = connection with { PinnedTimeZoneId = existingMetadata.PinnedTimeZoneId };
                await SaveConnectionAsync(connection, cancellationToken);
            }
        }

        await WriteProfileMetadataAsync(accessToken, spreadsheetId, connection, cancellationToken);
        _localProfileNamePending = false;
        _localTimeZonePending = false;
        var changeRows = await _api.ReadRangeAsync(
            accessToken,
            spreadsheetId,
            WorksheetRange(ChangesWorksheet, "A1:J1"),
            cancellationToken);
        if (changeRows.Count == 0)
        {
            await _api.WriteRangeAsync(
                accessToken,
                spreadsheetId,
                WorksheetRange(ChangesWorksheet, "A1:J1"),
                [ChangeHeaders.Cast<object?>().ToArray()],
                cancellationToken);
        }
        var deviceRows = await _api.ReadRangeAsync(
            accessToken,
            spreadsheetId,
            WorksheetRange(DevicesWorksheet, "A1:I1"),
            cancellationToken);
        if (deviceRows.Count == 0)
        {
            await _api.WriteRangeAsync(
                accessToken,
                spreadsheetId,
                WorksheetRange(DevicesWorksheet, "A1:I1"),
                [DeviceHeaders.Cast<object?>().ToArray()],
                cancellationToken);
        }
    }

    private Task WriteProfileMetadataAsync(
        string accessToken,
        string spreadsheetId,
        GoogleSheetsConnection connection,
        CancellationToken cancellationToken)
    {
        var metadata = new IReadOnlyList<object?>[]
        {
            ["Key", "Value"],
            ["ProtocolVersion", connection.SyncProtocolVersion],
            ["ProfileId", (connection.SyncProfileId ?? Guid.NewGuid()).ToString("D")],
            ["ProfileName", _profileName],
            ["PinnedTimeZoneId", connection.PinnedTimeZoneId ?? _defaultTimeZone.Id],
            ["UpdatedUtc", _clock.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)],
        };
        return _api.WriteRangeAsync(
            accessToken,
            spreadsheetId,
            WorksheetRange(ProfileWorksheet, "A1:B6"),
            metadata,
            cancellationToken);
    }

    private async Task<CloudChangeBatch> ReadCloudChangesAsync(
        string accessToken,
        string spreadsheetId,
        long cursor,
        CancellationToken cancellationToken)
    {
        var startRow = checked(cursor + 2);
        var rows = await _api.ReadRangeAsync(
            accessToken,
            spreadsheetId,
            WorksheetRange(ChangesWorksheet, $"A{startRow}:J"),
            cancellationToken);
        var changes = new List<ProfileSyncChange>();
        foreach (var row in rows)
        {
            if (TryParseChange(row, out var change))
            {
                changes.Add(change);
            }
        }
        return new CloudChangeBatch(changes, checked(cursor + rows.Count));
    }

    private async Task<(int WorksheetCount, int EntryCount)> WriteDailyWorksheetsAsync(
        string accessToken,
        string spreadsheetId,
        ISet<string> worksheetNames,
        IReadOnlyList<TimeEntryView> entries,
        TimeZoneInfo timeZone,
        bool legacyUpgrade,
        CancellationToken cancellationToken)
    {
        var groupsBySheet = entries
            .GroupBy(entry => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.StartUtc, timeZone).DateTime))
            .ToDictionary(
                group => group.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                group => group.AsEnumerable(),
                StringComparer.Ordinal);
        var localSheetNames = groupsBySheet.Keys.Order(StringComparer.Ordinal).ToArray();
        var existingDailyNames = worksheetNames.Where(IsDailyWorksheetName).Order(StringComparer.Ordinal).ToArray();
        if (legacyUpgrade && existingDailyNames.Length > 0 && !worksheetNames.Contains(LegacyReviewWorksheet))
        {
            await ArchiveLegacyRowsAsync(
                accessToken,
                spreadsheetId,
                existingDailyNames,
                entries.Select(entry => entry.Id).ToHashSet(),
                cancellationToken);
            worksheetNames.Add(LegacyReviewWorksheet);
        }

        var missing = localSheetNames.Where(name => !worksheetNames.Contains(name)).ToArray();
        await _api.AddWorksheetsAsync(accessToken, spreadsheetId, missing, cancellationToken);
        worksheetNames.UnionWith(missing);
        var managedNames = existingDailyNames.Union(localSheetNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var remoteRows = await _api.ReadWorksheetsAsync(accessToken, spreadsheetId, managedNames, cancellationToken);
        var rowsBySheet = new Dictionary<string, IReadOnlyList<IReadOnlyList<object?>>>(StringComparer.Ordinal);
        foreach (var sheetName in managedNames)
        {
            var rows = new IReadOnlyList<object?>[] { Headers.Cast<object?>().ToArray() }
                .Concat(groupsBySheet.TryGetValue(sheetName, out var group)
                    ? group.OrderBy(entry => entry.StartUtc).Select(entry => ToRow(entry, timeZone))
                    : [])
                .ToArray();
            rowsBySheet[sheetName] = PadRowsForOverwrite(
                rows,
                remoteRows.GetValueOrDefault(sheetName)?.Count ?? 0);
        }
        await _api.WriteWorksheetsAsync(accessToken, spreadsheetId, rowsBySheet, cancellationToken);
        return (managedNames.Length, entries.Count);
    }

    private async Task ArchiveLegacyRowsAsync(
        string accessToken,
        string spreadsheetId,
        IReadOnlyList<string> dailyNames,
        IReadOnlySet<Guid> localEntryIds,
        CancellationToken cancellationToken)
    {
        var remote = await _api.ReadWorksheetsAsync(accessToken, spreadsheetId, dailyNames, cancellationToken);
        var archiveRows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "Source worksheet", "Legacy row JSON" },
        };
        var candidates = new List<LegacyProfileSyncCandidate>();
        foreach (var name in dailyNames)
        {
            foreach (var row in remote.GetValueOrDefault(name)?.Skip(1) ?? [])
            {
                if (row.Count > 0 && row.Any(cell => !string.IsNullOrWhiteSpace(cell)))
                {
                    archiveRows.Add(new object?[] { name, JsonSerializer.Serialize(row) });
                    var entryId = row.Count > 0 && Guid.TryParse(row[0], out var parsedId)
                        ? parsedId
                        : (Guid?)null;
                    if (entryId is null || !localEntryIds.Contains(entryId.Value))
                    {
                        candidates.Add(ParseLegacyCandidate(name, row, entryId));
                    }
                }
            }
        }
        if (archiveRows.Count == 1)
        {
            return;
        }
        await _store.RegisterLegacyProfileSyncCandidatesAsync(candidates, cancellationToken);
        await _api.AddWorksheetsAsync(accessToken, spreadsheetId, [LegacyReviewWorksheet], cancellationToken);
        await _api.WriteRangeAsync(
            accessToken,
            spreadsheetId,
            WorksheetRange(LegacyReviewWorksheet, $"A1:B{archiveRows.Count}"),
            archiveRows,
            cancellationToken);
    }

    private static LegacyProfileSyncCandidate ParseLegacyCandidate(
        string worksheet,
        IReadOnlyList<string> row,
        Guid? entryId)
    {
        var serialized = JsonSerializer.Serialize(row);
        var candidateId = entryId?.ToString("D")
            ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{worksheet}\n{serialized}")));
        var start = row.Count > 2 && DateTimeOffset.TryParse(
            row[2],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out var parsedStart)
                ? parsedStart.ToUniversalTime()
                : (DateTimeOffset?)null;
        var end = row.Count > 3 && DateTimeOffset.TryParse(
            row[3],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out var parsedEnd)
                ? parsedEnd.ToUniversalTime()
                : (DateTimeOffset?)null;
        string? validationError = null;
        if (start is null || end is null)
        {
            validationError = "the row is missing a valid completed start/end time";
        }
        else if (end.Value - start.Value < TimeSpan.FromMinutes(1))
        {
            validationError = "the row is shorter than one minute";
        }
        var source = row.Count > 16 && Enum.TryParse<TrackingSource>(row[16], ignoreCase: true, out var parsedSource)
            ? parsedSource
            : TrackingSource.Manual;
        return new LegacyProfileSyncCandidate(
            candidateId,
            worksheet,
            row,
            entryId,
            start,
            end,
            row.Count > 5 ? NullIfEmpty(row[5]) : null,
            row.Count > 6 ? NullIfEmpty(row[6]) : null,
            row.Count > 7 ? NullIfEmpty(row[7]) : null,
            row.Count > 8 ? NullIfEmpty(row[8]) : null,
            row.Count > 11 && string.Equals(row[11], "Yes", StringComparison.OrdinalIgnoreCase),
            row.Count > 17 && string.Equals(row[17], "Yes", StringComparison.OrdinalIgnoreCase),
            source,
            validationError);
    }

    private async Task<IReadOnlyList<RemoteTimerStatus>> UpdateAndReadDeviceStatesAsync(
        string accessToken,
        string spreadsheetId,
        GoogleSheetsConnection connection,
        IReadOnlyList<TimeEntryView> entries,
        CancellationToken cancellationToken)
    {
        var deviceId = connection.DeviceId
            ?? throw new InvalidOperationException("The local synchronization device ID is missing.");
        var running = entries.SingleOrDefault(entry => entry.EndUtc is null);
        var local = new RemoteTimerStatus(
            deviceId,
            NormalizeDeviceName(connection.DeviceName),
            _clock.UtcNow,
            running?.Id,
            running?.ClientName,
            running?.ProjectName,
            running?.TaskName,
            running?.StartUtc);
        var rows = await _api.ReadRangeAsync(
            accessToken,
            spreadsheetId,
            WorksheetRange(DevicesWorksheet, "A2:I"),
            cancellationToken);
        var rowIndex = -1;
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].Count > 0 && Guid.TryParse(rows[index][0], out var existingId) && existingId == deviceId)
            {
                rowIndex = index + 2;
                break;
            }
        }
        if (rowIndex < 0)
        {
            await _api.AppendRowsAsync(
                accessToken,
                spreadsheetId,
                WorksheetRange(DevicesWorksheet, "A:I"),
                [ToDeviceRow(local)],
                cancellationToken);
        }
        else
        {
            await _api.WriteRangeAsync(
                accessToken,
                spreadsheetId,
                WorksheetRange(DevicesWorksheet, $"A{rowIndex}:I{rowIndex}"),
                [ToDeviceRow(local)],
                cancellationToken);
        }

        rows = await _api.ReadRangeAsync(
            accessToken,
            spreadsheetId,
            WorksheetRange(DevicesWorksheet, "A2:I"),
            cancellationToken);
        var staleBefore = _clock.UtcNow.Subtract(TimeSpan.FromMinutes(2));
        return rows
            .Select(ParseDeviceRow)
            .Where(status => status is not null && status.DeviceId != deviceId && status.IsRunning && status.LastSeenUtc >= staleBefore)
            .Select(status => status!)
            .GroupBy(status => status.DeviceId)
            .Select(group => group.OrderByDescending(status => status.LastSeenUtc).First())
            .OrderBy(status => status.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task SaveConnectionAndEnableAsync(
        GoogleSheetsConnection connection,
        CancellationToken cancellationToken)
    {
        await SaveConnectionAsync(connection, cancellationToken);
        await _store.SetSettingAsync(
            LogExportDestinationSettings.DestinationKey,
            LogExportDestinationSettings.GoogleSheets,
            cancellationToken);
    }

    private async Task SaveConnectionAsync(
        GoogleSheetsConnection connection,
        CancellationToken cancellationToken) =>
        await _store.SetSettingAsync(
            GoogleSheetsSettings.ConnectionKey,
            JsonSerializer.Serialize(connection),
            cancellationToken);

    private GoogleSheetsConnection NormalizeConnectionMetadata(GoogleSheetsConnection connection)
    {
        var syncProfileId = connection.SyncProfileId is { } profileId && profileId != Guid.Empty
            ? profileId
            : Guid.NewGuid();
        var deviceId = connection.DeviceId is { } existingDeviceId && existingDeviceId != Guid.Empty
            ? existingDeviceId
            : Guid.NewGuid();
        return connection with
        {
            SyncProfileId = syncProfileId,
            DeviceId = deviceId,
            DeviceName = NormalizeDeviceName(connection.DeviceName),
            PinnedTimeZoneId = ResolvePinnedTimeZone(connection.PinnedTimeZoneId).Id,
        };
    }

    private TimeZoneInfo ResolvePinnedTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }
        return _defaultTimeZone;
    }

    private static GoogleSyncProfileMetadata? ParseProfileMetadata(
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var values = rows
            .Where(row => row.Count >= 2)
            .ToDictionary(row => row[0], row => row[1], StringComparer.OrdinalIgnoreCase);
        return values.TryGetValue("ProfileId", out var profileValue) && Guid.TryParse(profileValue, out var profileId) &&
               values.TryGetValue("ProfileName", out var profileName) && !string.IsNullOrWhiteSpace(profileName) &&
               values.TryGetValue("PinnedTimeZoneId", out var timeZoneId) && !string.IsNullOrWhiteSpace(timeZoneId) &&
               values.TryGetValue("ProtocolVersion", out var protocolValue) && int.TryParse(protocolValue, NumberStyles.None, CultureInfo.InvariantCulture, out var protocol)
            ? new GoogleSyncProfileMetadata(profileId, profileName, timeZoneId, protocol)
            : null;
    }

    private static IReadOnlyList<object?> ToChangeRow(ProfileSyncChange change) =>
    [
        change.RevisionId.ToString("D"),
        change.EntityType,
        change.EntityId,
        JsonSerializer.Serialize(change.ParentRevisionIds.Select(parent => parent.ToString("D")).ToArray()),
        change.Operation.ToString(),
        change.DeviceId.ToString("D"),
        change.DeviceName,
        change.ChangedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        change.ContentHash,
        change.PayloadJson,
    ];

    private static bool TryParseChange(
        IReadOnlyList<string> row,
        out ProfileSyncChange change)
    {
        change = null!;
        if (row.Count < 9 ||
            !Guid.TryParse(row[0], out var revisionId) ||
            string.IsNullOrWhiteSpace(row[1]) ||
            string.IsNullOrWhiteSpace(row[2]) ||
            !Enum.TryParse<ProfileSyncOperation>(row[4], ignoreCase: true, out var operation) ||
            !Guid.TryParse(row[5], out var deviceId) ||
            !DateTimeOffset.TryParseExact(row[7], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var changedUtc))
        {
            return false;
        }
        string[] parentValues;
        try
        {
            parentValues = JsonSerializer.Deserialize<string[]>(row[3]) ?? [];
        }
        catch (JsonException)
        {
            return false;
        }
        var parents = parentValues
            .Select(value => Guid.TryParse(value, out var parent) ? parent : Guid.Empty)
            .Where(parent => parent != Guid.Empty)
            .ToArray();
        change = new ProfileSyncChange(
            revisionId,
            row[1],
            row[2],
            parents,
            operation,
            deviceId,
            row[6],
            changedUtc,
            row[8],
            row.Count > 9 && !string.IsNullOrEmpty(row[9]) ? row[9] : null);
        return true;
    }

    private static IReadOnlyList<object?> ToDeviceRow(RemoteTimerStatus status) =>
    [
        status.DeviceId.ToString("D"),
        status.DeviceName,
        status.LastSeenUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        status.EntryId?.ToString("D"),
        status.ClientName,
        status.ProjectName,
        status.TaskName,
        status.StartedUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        status.IsRunning ? "Yes" : "No",
    ];

    private static RemoteTimerStatus? ParseDeviceRow(IReadOnlyList<string> row)
    {
        if (row.Count < 3 ||
            !Guid.TryParse(row[0], out var deviceId) ||
            !DateTimeOffset.TryParseExact(row[2], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastSeen))
        {
            return null;
        }
        var running = row.Count > 8 && string.Equals(row[8], "Yes", StringComparison.OrdinalIgnoreCase);
        var entryId = running && row.Count > 3 && Guid.TryParse(row[3], out var parsedEntryId)
            ? parsedEntryId
            : (Guid?)null;
        var started = running && row.Count > 7 && DateTimeOffset.TryParseExact(
            row[7],
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsedStarted)
            ? parsedStarted
            : (DateTimeOffset?)null;
        return new RemoteTimerStatus(
            deviceId,
            row.Count > 1 && !string.IsNullOrWhiteSpace(row[1]) ? row[1] : "Another computer",
            lastSeen,
            entryId,
            row.Count > 4 ? NullIfEmpty(row[4]) : null,
            row.Count > 5 ? NullIfEmpty(row[5]) : null,
            row.Count > 6 ? NullIfEmpty(row[6]) : null,
            started);
    }

    private static IReadOnlyList<IReadOnlyList<object?>> PadRowsForOverwrite(
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int remoteRowCount)
    {
        if (rows.Count >= remoteRowCount)
        {
            return rows;
        }
        var padded = rows.ToList();
        while (padded.Count < remoteRowCount)
        {
            padded.Add(Enumerable.Repeat<object?>(string.Empty, Headers.Length).ToArray());
        }
        return padded;
    }

    private static bool IsDailyWorksheetName(string name) =>
        DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static IReadOnlyList<object?> ToRow(TimeEntryView entry, TimeZoneInfo timeZone)
    {
        var start = TimeZoneInfo.ConvertTime(entry.StartUtc, timeZone);
        var end = entry.EndUtc is null ? (DateTimeOffset?)null : TimeZoneInfo.ConvertTime(entry.EndUtc.Value, timeZone);
        var durationSeconds = entry.EndUtc is null ? (long?)null : entry.NetDurationSeconds(entry.EndUtc.Value);
        decimal? amount = durationSeconds is null || entry.HourlyRate is null
            ? null
            : entry.HourlyRate.Value * (decimal)TimeSpan.FromSeconds(durationSeconds.Value).TotalHours;
        var created = TimeZoneInfo.ConvertTime(entry.CreatedUtc ?? entry.StartUtc, timeZone);
        var modified = TimeZoneInfo.ConvertTime(entry.ModifiedUtc ?? entry.CreatedUtc ?? entry.StartUtc, timeZone);
        return
        [
            entry.Id.ToString("D"),
            start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            start.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            end?.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            durationSeconds is null ? null : FormatDuration(TimeSpan.FromSeconds(durationSeconds.Value)),
            entry.ClientName,
            entry.ProjectName,
            entry.TaskName,
            entry.Description,
            string.Join(' ', TagParser.Extract(entry.Description).Select(tag => $"#{tag}")),
            entry.SoftwareLabels,
            entry.IsPaid ? "Yes" : "No",
            entry.DetailsPending ? "Yes" : "No",
            entry.HourlyRate,
            entry.Currency,
            amount,
            entry.Source.ToString(),
            entry.IsCall ? "Yes" : "No",
            created.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            modified.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        ];
    }

    private static string FormatDuration(TimeSpan duration) =>
        $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    private static string ParseSpreadsheetId(string value)
    {
        value = Required(value, nameof(value));
        var match = Regex.Match(value, @"/spreadsheets/d/([A-Za-z0-9_-]+)", RegexOptions.CultureInvariant);
        var id = match.Success ? match.Groups[1].Value : value;
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{8,}$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Paste a valid Google Sheets URL or spreadsheet ID.", nameof(value));
        }
        return id;
    }

    private static string WorksheetRange(string worksheetName, string cells) =>
        $"'{worksheetName.Replace("'", "''", StringComparison.Ordinal)}'!{cells}";

    private static string NormalizeDeviceName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Environment.MachineName : value.Trim()[..Math.Min(80, value.Trim().Length)];

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Required(string value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException("A value is required.", name);

    private static string SanitizeError(string error)
    {
        var singleLine = string.Join(' ', error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 240 ? singleLine : singleLine[..240];
    }

    private sealed record CloudChangeBatch(IReadOnlyList<ProfileSyncChange> Changes, long NextCursor);
}
