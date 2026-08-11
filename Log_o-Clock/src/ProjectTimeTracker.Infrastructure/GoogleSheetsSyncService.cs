using System.Globalization;
using System.Text.Json;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Infrastructure;

public sealed class GoogleSheetsSyncService : IGoogleSheetsSyncService
{
    private static readonly string[] Headers =
    [
        "EntryId", "Date", "Start", "End", "Duration", "Client", "Project",
        "Task", "Description", "Tags", "Software", "Paid", "PendingDetails",
        "HourlyRate", "Currency", "Amount", "Source",
    ];

    private readonly ITrackerStore _store;
    private readonly IGoogleSheetsApiClient _api;
    private readonly IGoogleAuthorizationBroker _authorizationBroker;
    private readonly ICredentialStore _credentials;
    private readonly Guid _profileId;
    private readonly string _profileName;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _timeZone;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _queuedSync;
    private Task? _periodicTask;

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
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public event EventHandler<GoogleSheetsSyncResult>? SyncCompleted;

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
            return JsonSerializer.Deserialize<GoogleSheetsConnection>(json);
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
        await _credentials.SetGoogleSheetsCredentialsAsync(
            _profileId,
            new GoogleSheetsCredentials(clientId, clientSecret, refreshToken),
            cancellationToken);

        var existing = await GetConnectionAsync(cancellationToken);
        var connection = new GoogleSheetsConnection(
            user.Email,
            user.DisplayName,
            existing?.SpreadsheetId,
            existing?.SpreadsheetUrl,
            StoreExportsInGoogleSheets: true);
        await SaveConnectionAsync(connection, cancellationToken);
        await _store.SetSettingAsync(
            LogExportDestinationSettings.DestinationKey,
            LogExportDestinationSettings.GoogleSheets,
            cancellationToken);
        return connection;
    }

    public async Task SetCloudExportEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken)
            ?? throw new InvalidOperationException("Connect Google Sheets before changing its export mode.");
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

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _queuedSync?.Cancel();
        await _credentials.DeleteGoogleSheetsCredentialsAsync(_profileId, cancellationToken);
        await _store.SetSettingAsync(GoogleSheetsSettings.ConnectionKey, string.Empty, cancellationToken);
        await _store.SetSettingAsync(
            LogExportDestinationSettings.DestinationKey,
            LogExportDestinationSettings.Local,
            cancellationToken);
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
                GoogleSpreadsheet spreadsheet;
                if (string.IsNullOrWhiteSpace(connection.SpreadsheetId))
                {
                    spreadsheet = await _api.CreateSpreadsheetAsync(
                        tokens.AccessToken,
                        $"Log O'clock - {_profileName}",
                        cancellationToken);
                    connection = connection with
                    {
                        SpreadsheetId = spreadsheet.Id,
                        SpreadsheetUrl = spreadsheet.Url,
                        LastError = null,
                        RequiresReconnect = false,
                    };
                    // Keep the created file identity even if a later history batch is rate limited.
                    // A retry must continue the same spreadsheet instead of creating an orphan duplicate.
                    await SaveConnectionAsync(connection, cancellationToken);
                }
                else
                {
                    spreadsheet = new GoogleSpreadsheet(
                        connection.SpreadsheetId,
                        connection.SpreadsheetUrl
                        ?? $"https://docs.google.com/spreadsheets/d/{connection.SpreadsheetId}/edit");
                }

                var entries = await _store.GetEntriesAsync(
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.MaxValue,
                    cancellationToken);
                var worksheets = (await _api.GetWorksheetNamesAsync(
                        tokens.AccessToken,
                        spreadsheet.Id,
                        cancellationToken))
                    .ToHashSet(StringComparer.Ordinal);
                var groups = entries.GroupBy(entry =>
                        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.StartUtc, _timeZone).DateTime))
                    .OrderBy(group => group.Key)
                    .ToArray();
                var groupsBySheet = groups.ToDictionary(
                    group => group.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    group => group.AsEnumerable(),
                    StringComparer.Ordinal);
                var localSheetNames = groupsBySheet.Keys.Order(StringComparer.Ordinal).ToArray();
                var missingSheetNames = localSheetNames
                    .Where(sheetName => !worksheets.Contains(sheetName))
                    .ToArray();
                await _api.AddWorksheetsAsync(
                    tokens.AccessToken,
                    spreadsheet.Id,
                    missingSheetNames,
                    cancellationToken);
                var managedSheetNames = worksheets
                    .Where(IsDailyWorksheetName)
                    .Union(localSheetNames, StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var remoteRowsBySheet = await _api.ReadWorksheetsAsync(
                    tokens.AccessToken,
                    spreadsheet.Id,
                    managedSheetNames,
                    cancellationToken);
                var deletedEntryIds = (await _store.GetGoogleSheetsEntryDeletionIdsAsync(cancellationToken))
                    .Select(id => id.ToString("D"))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var rowsBySheet = new Dictionary<string, IReadOnlyList<IReadOnlyList<object?>>>(StringComparer.Ordinal);
                foreach (var sheetName in managedSheetNames)
                {
                    var remoteRows = remoteRowsBySheet.GetValueOrDefault(sheetName) ?? [];
                    var localRows = groupsBySheet.TryGetValue(sheetName, out var group)
                        ? group.Select(ToRow)
                        : Enumerable.Empty<IReadOnlyList<object?>>();
                    var merged = MergeRows(remoteRows, localRows, deletedEntryIds);
                    rowsBySheet[sheetName] = PadRowsForOverwrite(merged, remoteRows.Count);
                }

                await _api.WriteWorksheetsAsync(
                    tokens.AccessToken,
                    spreadsheet.Id,
                    rowsBySheet,
                    cancellationToken);
                await _store.CompleteGoogleSheetsEntryDeletionsAsync(
                    deletedEntryIds.Select(Guid.Parse).ToArray(),
                    cancellationToken);

                var completed = _clock.UtcNow;
                connection = connection with
                {
                    SpreadsheetId = spreadsheet.Id,
                    SpreadsheetUrl = spreadsheet.Url,
                    LastSuccessfulSyncUtc = completed,
                    LastError = null,
                    RequiresReconnect = false,
                };
                await SaveConnectionAsync(connection, cancellationToken);
                var result = new GoogleSheetsSyncResult(groups.Length, entries.Count, completed);
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
                // SyncNow records a redacted error. Background synchronization must not
                // interfere with local timer persistence.
            }
        }, token);
    }

    public void Start()
    {
        if (_periodicTask is not null)
        {
            return;
        }

        QueueSync();
        _periodicTask = RunPeriodicAsync(_lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
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
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            QueueSync();
        }
    }

    private async Task SaveConnectionAsync(
        GoogleSheetsConnection connection,
        CancellationToken cancellationToken) =>
        await _store.SetSettingAsync(
            GoogleSheetsSettings.ConnectionKey,
            JsonSerializer.Serialize(connection),
            cancellationToken);

    private static IReadOnlyList<IReadOnlyList<object?>> MergeRows(
        IReadOnlyList<IReadOnlyList<string>> remoteRows,
        IEnumerable<IReadOnlyList<object?>> localRows,
        IReadOnlySet<string> deletedEntryIds)
    {
        var byId = new Dictionary<string, IReadOnlyList<object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in remoteRows.Skip(1))
        {
            if (row.Count > 0 &&
                Guid.TryParse(row[0], out _) &&
                !deletedEntryIds.Contains(row[0]))
            {
                byId[row[0]] = row.Cast<object?>().ToArray();
            }
        }

        foreach (var row in localRows)
        {
            byId[(string)row[0]!] = row;
        }

        return new IReadOnlyList<object?>[] { Headers.Cast<object?>().ToArray() }
            .Concat(byId.Values.OrderBy(row => row.Count > 2 ? row[2]?.ToString() : string.Empty, StringComparer.Ordinal))
            .ToArray();
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
        DateOnly.TryParseExact(
            name,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    private IReadOnlyList<object?> ToRow(TimeEntryView entry)
    {
        var start = TimeZoneInfo.ConvertTime(entry.StartUtc, _timeZone);
        var end = entry.EndUtc is null ? (DateTimeOffset?)null : TimeZoneInfo.ConvertTime(entry.EndUtc.Value, _timeZone);
        var durationSeconds = entry.EndUtc is null ? (long?)null : entry.NetDurationSeconds(entry.EndUtc.Value);
        decimal? amount = durationSeconds is null || entry.HourlyRate is null
            ? null
            : entry.HourlyRate.Value * (decimal)TimeSpan.FromSeconds(durationSeconds.Value).TotalHours;
        return new object?[]
        {
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
        };
    }

    private static string FormatDuration(TimeSpan duration) =>
        $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    private static string Required(string value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException("A value is required.", name);

    private static string SanitizeError(string error)
    {
        var singleLine = string.Join(' ', error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 240 ? singleLine : singleLine[..240];
    }
}
