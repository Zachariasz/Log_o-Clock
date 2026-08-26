using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Infrastructure;

public sealed partial class SqliteTrackerStore
{
    private const string CompositeSyncKeySeparator = "\u001f";

    private static readonly SyncTableDescriptor[] SyncTables =
    [
        new("Clients", "Clients", ["Id"], ["Id", "Name", "Color", "IsArchived"], 10),
        new("Projects", "Projects", ["Id"],
            ["Id", "ClientId", "Name", "Color", "IsArchived", "IsFrozen", "DailyTargetHours", "WeeklyTargetHours", "MonthlyTargetHours", "HourlyRate", "Currency", "CarryOverTargetDebtEnabled"], 20),
        new("SavedTasks", "SavedTasks", ["Id"], ["Id", "ProjectId", "Name", "IsArchived", "Origin"], 30),
        new("Tags", "Tags", ["Id"], ["Id", "Name", "Color", "IsGlobal"], 30),
        new("Software", "Software", ["Id"], ["Id", "ProcessName", "Label", "IsExcluded", "IsHidden", "IsGlobal"], 30),
        new("RecognitionRules", "RecognitionRules", ["Id"], ["Id", "ProjectId", "TitlePattern", "ProcessName", "IsEnabled", "IsEnabledBeforeProjectFreeze"], 40),
        new("CustomTargets", "CustomTargets", ["Id"], ["Id", "Name", "ProjectId", "Cadence", "TargetHours", "CreatedUtc", "ModifiedUtc", "CompletedUtc", "DurationMetric"], 40),
        new("ProjectTargetDebtCancellations", "ProjectTargetDebtCancellations", ["Id"], ["Id", "ProjectId", "CanceledSeconds", "CanceledUtc", "RestoredUtc"], 40),
        new("TrelloBoardMappings", "TrelloBoardMappings", ["Id"], ["Id", "ProjectId", "BoardId", "BoardName"], 40),
        new("ProjectTags", "ProjectTags", ["TagId", "ProjectId"], ["TagId", "ProjectId"], 50),
        new("SoftwareTags", "SoftwareTags", ["SoftwareId", "TagId"], ["SoftwareId", "TagId"], 50),
        new("ProjectSoftwareSettings", "ProjectSoftwareSettings", ["ProjectId", "SoftwareId"], ["ProjectId", "SoftwareId", "IsExcluded"], 50),
        new("ProjectSoftwareTags", "ProjectSoftwareTags", ["ProjectId", "SoftwareId", "TagId"], ["ProjectId", "SoftwareId", "TagId"], 60),
        new("TrelloMappingLists", "TrelloMappingLists", ["MappingId", "ListId"], ["MappingId", "ListId", "ListName"], 50),
        new("ExternalTaskLinks", "ExternalTaskLinks", ["Provider", "ExternalId"], ["Provider", "ExternalId", "TaskId", "MappingId", "BoardId", "ListId", "WebUrl", "State", "RemoteModifiedUtc"], 60),
        new("TimeEntries", "TimeEntries", ["Id"], ["Id", "ProjectId", "TaskId", "Description", "StartUtc", "EndUtc", "LastCheckpointUtc", "DetailsPending", "Source", "CreatedUtc", "ModifiedUtc", "IsPaid", "IsCall"], 70),
        new("TimeEntrySoftware", "TimeEntrySoftware", ["TimeEntryId", "SoftwareId"], ["TimeEntryId", "SoftwareId"], 80),
        new("TimeExclusions", "TimeExclusions", ["Id"], ["Id", "TimeEntryId", "StartUtc", "EndUtc", "Reason"], 80),
        new("Settings", "Settings", ["Key"], ["Key", "Value"], 90),
    ];

    private const string ProfileSyncSchemaSql = """
        CREATE TABLE IF NOT EXISTS ProfileSyncRuntime (
            SingletonId INTEGER PRIMARY KEY CHECK (SingletonId = 1),
            IsApplying INTEGER NOT NULL DEFAULT 0 CHECK (IsApplying IN (0, 1))
        );
        INSERT OR IGNORE INTO ProfileSyncRuntime (SingletonId, IsApplying) VALUES (1, 0);

        CREATE TABLE IF NOT EXISTS ProfileSyncMetadata (
            Key TEXT PRIMARY KEY COLLATE NOCASE,
            Value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ProfileSyncDirtyTables (
            TableName TEXT PRIMARY KEY COLLATE NOCASE
        );

        CREATE TABLE IF NOT EXISTS ProfileSyncEntityState (
            EntityType TEXT NOT NULL,
            EntityId TEXT NOT NULL,
            LocalRevisionId TEXT NOT NULL,
            ContentHash TEXT NOT NULL,
            IsDeleted INTEGER NOT NULL CHECK (IsDeleted IN (0, 1)),
            PRIMARY KEY (EntityType, EntityId)
        );

        CREATE TABLE IF NOT EXISTS ProfileSyncOutbox (
            RevisionId TEXT PRIMARY KEY,
            EntityType TEXT NOT NULL,
            EntityId TEXT NOT NULL,
            ParentRevisionIdsJson TEXT NOT NULL,
            Operation INTEGER NOT NULL CHECK (Operation IN (0, 1)),
            DeviceId TEXT NOT NULL,
            DeviceName TEXT NOT NULL,
            ChangedUtc TEXT NOT NULL,
            ContentHash TEXT NOT NULL,
            PayloadJson TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_ProfileSyncOutbox_ChangedUtc
            ON ProfileSyncOutbox (ChangedUtc, RevisionId);

        CREATE TABLE IF NOT EXISTS ProfileSyncConflicts (
            Id TEXT PRIMARY KEY,
            EntityType TEXT NOT NULL,
            EntityId TEXT NOT NULL,
            Kind INTEGER NOT NULL,
            HeadsJson TEXT NOT NULL,
            DetectedUtc TEXT NOT NULL,
            Summary TEXT NULL,
            RelatedEntityIdsJson TEXT NULL,
            UNIQUE (EntityType, EntityId)
        );

        CREATE TABLE IF NOT EXISTS ProfileSyncAliases (
            EntityType TEXT NOT NULL,
            AliasId TEXT NOT NULL,
            CanonicalId TEXT NOT NULL,
            PRIMARY KEY (EntityType, AliasId)
        );
        """;

    public async Task<bool> HasUserProfileDataAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM Clients WHERE Id <> $client) +
                (SELECT COUNT(*) FROM Projects WHERE Id <> $project) +
                (SELECT COUNT(*) FROM SavedTasks) +
                (SELECT COUNT(*) FROM Tags) +
                (SELECT COUNT(*) FROM Software) +
                (SELECT COUNT(*) FROM RecognitionRules) +
                (SELECT COUNT(*) FROM TimeEntries) +
                (SELECT COUNT(*) FROM CustomTargets) +
                (SELECT COUNT(*) FROM TrelloBoardMappings);
            """;
        command.Parameters.AddWithValue("$client", SystemEntityIds.UnassignedClientId.ToString("D"));
        command.Parameters.AddWithValue("$project", SystemEntityIds.UnassignedProjectId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    public async Task<IReadOnlyList<ProfileSyncChange>> CaptureProfileSyncChangesAsync(
        Guid deviceId,
        string deviceName,
        bool seedAll,
        CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("A synchronization device ID is required.", nameof(deviceId));
        }

        deviceName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName.Trim();
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var dirtyTables = seedAll
            ? SyncTables.Select(table => table.TableName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : await ReadDirtySyncTablesAsync(connection, transaction, cancellationToken);
        var runningEntryIds = await ReadRunningEntryIdsAsync(connection, transaction, cancellationToken);

        if (seedAll)
        {
            await SeedLegacyEntryDeletionRevisionsAsync(
                connection,
                transaction,
                deviceId,
                deviceName,
                cancellationToken);
        }

        foreach (var descriptor in SyncTables.Where(table => dirtyTables.Contains(table.TableName)))
        {
            var rows = await ReadSyncRowsAsync(connection, transaction, descriptor, runningEntryIds, cancellationToken);
            if (string.Equals(descriptor.EntityType, "TimeEntries", StringComparison.Ordinal) &&
                rows.Any(row => row.PayloadJson is not null))
            {
                // Associations may have been observed while the timer was still running and therefore
                // intentionally skipped by an earlier capture. Re-scan them when the entry completes.
                dirtyTables.Add("TimeEntrySoftware");
                dirtyTables.Add("TimeExclusions");
            }
            var physicalIds = rows.Select(row => row.EntityId).ToHashSet(StringComparer.Ordinal);
            foreach (var row in rows.Where(row => row.PayloadJson is not null))
            {
                var state = await ReadSyncEntityStateAsync(
                    connection,
                    transaction,
                    descriptor.EntityType,
                    row.EntityId,
                    cancellationToken);
                if (state is { IsDeleted: false } &&
                    string.Equals(state.ContentHash, row.ContentHash, StringComparison.Ordinal))
                {
                    continue;
                }

                await InsertLocalSyncChangeAsync(
                    connection,
                    transaction,
                    descriptor.EntityType,
                    row.EntityId,
                    state is null ? [] : [state.LocalRevisionId],
                    ProfileSyncOperation.Upsert,
                    deviceId,
                    deviceName,
                    DateTimeOffset.UtcNow,
                    row.ContentHash,
                    row.PayloadJson,
                    cancellationToken);
            }

            var activeStates = await ReadSyncEntityStatesAsync(
                connection,
                transaction,
                descriptor.EntityType,
                onlyActive: true,
                cancellationToken);
            foreach (var state in activeStates.Where(state => !physicalIds.Contains(state.EntityId)))
            {
                await InsertLocalSyncChangeAsync(
                    connection,
                    transaction,
                    descriptor.EntityType,
                    state.EntityId,
                    [state.LocalRevisionId],
                    ProfileSyncOperation.Delete,
                    deviceId,
                    deviceName,
                    DateTimeOffset.UtcNow,
                    string.Empty,
                    payloadJson: null,
                    cancellationToken);
            }

            await DeleteDirtySyncTableAsync(connection, transaction, descriptor.TableName, cancellationToken);
        }

        transaction.Commit();
        return await ReadProfileSyncOutboxAsync(connection, cancellationToken);
    }

    private static async Task SeedLegacyEntryDeletionRevisionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid deviceId,
        string deviceName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EntryId, DeletedUtc FROM GoogleSheetsEntryDeletions ORDER BY DeletedUtc, EntryId;";
        var deletions = new List<(string EntryId, DateTimeOffset DeletedUtc)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                deletions.Add((reader.GetString(0), Parse(reader.GetString(1))));
            }
        }
        foreach (var deletion in deletions)
        {
            var state = await ReadSyncEntityStateAsync(
                connection,
                transaction,
                "TimeEntries",
                deletion.EntryId,
                cancellationToken);
            if (state is not null)
            {
                continue;
            }
            await InsertLocalSyncChangeAsync(
                connection,
                transaction,
                "TimeEntries",
                deletion.EntryId,
                [],
                ProfileSyncOperation.Delete,
                deviceId,
                deviceName,
                deletion.DeletedUtc,
                string.Empty,
                payloadJson: null,
                cancellationToken);
        }
    }

    public async Task AcknowledgeProfileSyncChangesAsync(
        IReadOnlyCollection<Guid> revisionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revisionIds);
        if (revisionIds.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var revisionId in revisionIds.Distinct())
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "DELETE FROM ProfileSyncOutbox WHERE RevisionId = $revision;",
                cancellationToken,
                ("$revision", revisionId.ToString("D")));
        }
        transaction.Commit();
    }

    public async Task<long> GetProfileSyncCloudCursorAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM ProfileSyncMetadata WHERE Key = 'CloudChangeRowCursor' LIMIT 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var cursor) && cursor >= 0
            ? cursor
            : 0;
    }

    public async Task SetProfileSyncCloudCursorAsync(
        long cursor,
        CancellationToken cancellationToken = default)
    {
        if (cursor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cursor));
        }
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ProfileSyncMetadata (Key, Value) VALUES ('CloudChangeRowCursor', $value)
            ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("$value", cursor.ToString(CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ProfileSyncReconcileResult> ReconcileProfileSyncChangesAsync(
        IReadOnlyList<ProfileSyncChange> cloudChanges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cloudChanges);
        var validChanges = cloudChanges
            .Where(change => change.RevisionId != Guid.Empty &&
                             !string.IsNullOrWhiteSpace(change.EntityType) &&
                             !string.IsNullOrWhiteSpace(change.EntityId))
            .GroupBy(change => change.RevisionId)
            .Select(group => group.First())
            .ToArray();
        if (validChanges.Length == 0)
        {
            return new ProfileSyncReconcileResult(0, 0, false);
        }

        var changesByRevision = validChanges.ToDictionary(change => change.RevisionId);
        var remoteToApply = new List<ProfileSyncChange>();
        var conflicts = new List<ProfileSyncConflict>();
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var entityGroup in validChanges.GroupBy(change => (change.EntityType, change.EntityId)))
        {
            var revisionsUsedAsParents = entityGroup
                .SelectMany(change => change.ParentRevisionIds)
                .ToHashSet();
            var heads = entityGroup
                .Where(change => !revisionsUsedAsParents.Contains(change.RevisionId))
                .OrderBy(change => change.ChangedUtc)
                .ThenBy(change => change.RevisionId)
                .ToArray();
            var localState = await ReadSyncEntityStateAsync(
                connection,
                transaction,
                entityGroup.Key.EntityType,
                entityGroup.Key.EntityId,
                cancellationToken);

            if (heads.Length == 1)
            {
                var head = heads[0];
                if (localState is null ||
                    localState.LocalRevisionId == head.RevisionId ||
                    IsSyncAncestor(localState.LocalRevisionId, head.RevisionId, changesByRevision))
                {
                    if (localState?.LocalRevisionId != head.RevisionId)
                    {
                        remoteToApply.Add(head);
                    }
                    continue;
                }

                if (IsSyncAncestor(head.RevisionId, localState.LocalRevisionId, changesByRevision))
                {
                    continue;
                }
            }

            var kind = heads.Any(head => head.Operation == ProfileSyncOperation.Delete) &&
                       heads.Any(head => head.Operation == ProfileSyncOperation.Upsert)
                ? ProfileSyncConflictKind.DeleteVersusEdit
                : ProfileSyncConflictKind.ConcurrentEdit;
            var conflict = new ProfileSyncConflict(
                Guid.NewGuid(),
                entityGroup.Key.EntityType,
                entityGroup.Key.EntityId,
                kind,
                heads,
                DateTimeOffset.UtcNow,
                BuildConflictSummary(entityGroup.Key.EntityType, kind));
            await UpsertProfileSyncConflictAsync(connection, transaction, conflict, cancellationToken);
            conflicts.Add(conflict);
        }

        await SetSyncApplyingAsync(connection, transaction, true, cancellationToken);
        var imported = 0;
        var touchedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in remoteToApply
                     .OrderBy(change => change.Operation == ProfileSyncOperation.Delete ? 1 : 0)
                     .ThenBy(change => change.Operation == ProfileSyncOperation.Delete
                         ? -GetSyncApplyOrder(change.EntityType)
                         : GetSyncApplyOrder(change.EntityType)))
        {
            var descriptor = FindSyncTable(change.EntityType);
            if (descriptor is null)
            {
                var invalid = new ProfileSyncConflict(
                    Guid.NewGuid(),
                    change.EntityType,
                    change.EntityId,
                    ProfileSyncConflictKind.InvalidRemoteRecord,
                    [change],
                    DateTimeOffset.UtcNow,
                    "This version of Log O'clock does not recognize the synchronized record type.");
                await UpsertProfileSyncConflictAsync(connection, transaction, invalid, cancellationToken);
                conflicts.Add(invalid);
                continue;
            }

            try
            {
                if (await TryCoalesceSynchronizedIdentityAsync(
                        connection,
                        transaction,
                        descriptor,
                        change,
                        cancellationToken))
                {
                    await WriteSyncEntityStateAsync(
                        connection,
                        transaction,
                        change.EntityType,
                        change.EntityId,
                        change.RevisionId,
                        change.ContentHash,
                        isDeleted: false,
                        cancellationToken);
                    await DeleteProfileSyncConflictAsync(
                        connection,
                        transaction,
                        change.EntityType,
                        change.EntityId,
                        cancellationToken);
                    touchedTables.Add(descriptor.TableName);
                    imported++;
                    continue;
                }
                await ApplyProfileSyncChangeAsync(connection, transaction, descriptor, change, cancellationToken);
                await WriteSyncEntityStateAsync(
                    connection,
                    transaction,
                    change.EntityType,
                    change.EntityId,
                    change.RevisionId,
                    change.ContentHash,
                    change.Operation == ProfileSyncOperation.Delete,
                    cancellationToken);
                await DeleteProfileSyncConflictAsync(
                    connection,
                    transaction,
                    change.EntityType,
                    change.EntityId,
                    cancellationToken);
                touchedTables.Add(descriptor.TableName);
                imported++;
            }
            catch (SqliteException exception)
            {
                var groupedParentDelete = change.Operation == ProfileSyncOperation.Delete &&
                                          change.EntityType is "Clients" or "Projects";
                var related = groupedParentDelete
                    ? await ReadDependentSyncIdentitiesAsync(
                        connection,
                        transaction,
                        change.EntityType,
                        change.EntityId,
                        cancellationToken)
                    : [];
                var relatedKeys = related.Select(item => (item.EntityType, item.EntityId)).ToHashSet();
                var conflictHeads = groupedParentDelete
                    ? new[] { change }.Concat(validChanges.Where(item =>
                        relatedKeys.Contains((item.EntityType, item.EntityId)))).DistinctBy(item => item.RevisionId).ToArray()
                    : [change];
                var invalid = new ProfileSyncConflict(
                    Guid.NewGuid(),
                    change.EntityType,
                    change.EntityId,
                    groupedParentDelete
                        ? ProfileSyncConflictKind.DeleteVersusEdit
                        : IsLikelyIdentityCollision(exception)
                        ? ProfileSyncConflictKind.IdentityCollision
                        : ProfileSyncConflictKind.InvalidRemoteRecord,
                    conflictHeads,
                    DateTimeOffset.UtcNow,
                    groupedParentDelete
                        ? "A client or project was deleted while another computer still has dependent work. Restore the work or confirm deletion of the complete affected set."
                        : SanitizeSyncError(exception.Message),
                    groupedParentDelete
                        ? JsonSerializer.Serialize(related.Select(item => $"{item.EntityType}:{item.EntityId}").ToArray())
                        : null);
                await UpsertProfileSyncConflictAsync(connection, transaction, invalid, cancellationToken);
                conflicts.Add(invalid);
            }
        }

        await SetSyncApplyingAsync(connection, transaction, false, cancellationToken);
        foreach (var table in touchedTables)
        {
            await DeleteDirtySyncTableAsync(connection, transaction, table, cancellationToken);
        }
        transaction.Commit();
        await connection.CloseAsync();
        if (imported > 0)
        {
            await SynchronizeMonthlyLogFilesAsync(cancellationToken);
        }
        return new ProfileSyncReconcileResult(imported, conflicts.Count, imported > 0);
    }

    public async Task<IReadOnlyList<ProfileSyncConflict>> GetProfileSyncConflictsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, EntityType, EntityId, Kind, HeadsJson, DetectedUtc, Summary, RelatedEntityIdsJson
            FROM ProfileSyncConflicts
            ORDER BY DetectedUtc, EntityType, EntityId;
            """;
        var conflicts = new List<ProfileSyncConflict>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            conflicts.Add(new ProfileSyncConflict(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                (ProfileSyncConflictKind)reader.GetInt32(3),
                DeserializeSyncChanges(reader.GetString(4)),
                Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return conflicts;
    }

    public async Task RegisterLegacyProfileSyncCandidatesAsync(
        IReadOnlyList<LegacyProfileSyncCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var candidate in candidates)
        {
            var payload = JsonSerializer.Serialize(candidate);
            var change = new ProfileSyncChange(
                Guid.NewGuid(),
                "LegacyEntry",
                candidate.CandidateId,
                [],
                ProfileSyncOperation.Upsert,
                Guid.Empty,
                "Legacy worksheet",
                DateTimeOffset.UtcNow,
                ComputeSyncHash(payload),
                payload);
            var summary = candidate.ValidationError is null
                ? $"A legacy row from {candidate.SourceWorksheet} has no revision history. Import or ignore it."
                : $"A legacy row from {candidate.SourceWorksheet} cannot be imported automatically: {candidate.ValidationError}";
            await UpsertProfileSyncConflictAsync(
                connection,
                transaction,
                new ProfileSyncConflict(
                    Guid.NewGuid(),
                    "LegacyEntry",
                    candidate.CandidateId,
                    ProfileSyncConflictKind.LegacyEntry,
                    [change],
                    DateTimeOffset.UtcNow,
                    summary),
                cancellationToken);
        }
        transaction.Commit();
    }

    public async Task ResolveProfileSyncConflictAsync(
        Guid conflictId,
        ProfileSyncResolution resolution,
        Guid? cloudRevisionId,
        Guid deviceId,
        string deviceName,
        DateTimeOffset resolvedUtc,
        CancellationToken cancellationToken = default)
    {
        var conflict = (await GetProfileSyncConflictsAsync(cancellationToken))
            .SingleOrDefault(item => item.Id == conflictId)
            ?? throw new InvalidOperationException("The synchronization conflict no longer exists.");
        if (conflict.Kind == ProfileSyncConflictKind.LegacyEntry)
        {
            await ResolveLegacyProfileSyncConflictAsync(
                conflict,
                resolution,
                resolvedUtc,
                cancellationToken);
            return;
        }
        var descriptor = FindSyncTable(conflict.EntityType)
            ?? throw new InvalidOperationException("The synchronized record type is not supported by this app version.");
        var parents = conflict.Heads.Select(head => head.RevisionId).Distinct().ToArray();
        var selectedCloud = cloudRevisionId is { } selectedRevision
            ? conflict.Heads.SingleOrDefault(head => head.RevisionId == selectedRevision)
            : conflict.Heads
                .Where(head => head.Operation == ProfileSyncOperation.Upsert)
                .OrderByDescending(head => head.ChangedUtc)
                .ThenByDescending(head => head.RevisionId)
                .FirstOrDefault();

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await SetSyncApplyingAsync(connection, transaction, true, cancellationToken);
        ProfileSyncOperation operation;
        string contentHash;
        string? payloadJson;
        var duplicatedEntry = false;
        var confirmedDependentDeletes = new List<(DependentSyncIdentity Identity, SyncEntityState State)>();
        switch (resolution)
        {
            case ProfileSyncResolution.KeepCloud:
                if (selectedCloud is not { Operation: ProfileSyncOperation.Upsert, PayloadJson: not null })
                {
                    throw new InvalidOperationException("Select a cloud version that still contains the record.");
                }
                await ApplyProfileSyncChangeAsync(connection, transaction, descriptor, selectedCloud, cancellationToken);
                operation = ProfileSyncOperation.Upsert;
                contentHash = selectedCloud.ContentHash;
                payloadJson = selectedCloud.PayloadJson;
                break;
            case ProfileSyncResolution.Restore:
                if (selectedCloud is { Operation: ProfileSyncOperation.Upsert, PayloadJson: not null })
                {
                    await ApplyProfileSyncChangeAsync(connection, transaction, descriptor, selectedCloud, cancellationToken);
                    operation = ProfileSyncOperation.Upsert;
                    contentHash = selectedCloud.ContentHash;
                    payloadJson = selectedCloud.PayloadJson;
                    break;
                }
                goto case ProfileSyncResolution.KeepLocal;
            case ProfileSyncResolution.Delete:
                if (descriptor.EntityType is "Clients" or "Projects")
                {
                    var affected = await ReadDependentSyncIdentitiesAsync(
                        connection,
                        transaction,
                        descriptor.EntityType,
                        conflict.EntityId,
                        cancellationToken);
                    foreach (var identity in affected)
                    {
                        var state = await ReadSyncEntityStateAsync(
                            connection,
                            transaction,
                            identity.EntityType,
                            identity.EntityId,
                            cancellationToken);
                        if (state is { IsDeleted: false })
                        {
                            confirmedDependentDeletes.Add((identity, state));
                        }
                    }
                    await ApplyDestructiveParentDeleteAsync(
                        connection,
                        transaction,
                        descriptor.EntityType,
                        conflict.EntityId,
                        cancellationToken);
                }
                else
                {
                    await ApplyProfileSyncChangeAsync(
                        connection,
                        transaction,
                        descriptor,
                        conflict.Heads[0] with { Operation = ProfileSyncOperation.Delete, PayloadJson = null },
                        cancellationToken);
                }
                operation = ProfileSyncOperation.Delete;
                contentHash = string.Empty;
                payloadJson = null;
                break;
            case ProfileSyncResolution.KeepBoth:
                if (!string.Equals(conflict.EntityType, "TimeEntries", StringComparison.Ordinal) ||
                    selectedCloud is not { Operation: ProfileSyncOperation.Upsert, PayloadJson: not null })
                {
                    throw new InvalidOperationException("Keep both is available only for entry edit conflicts.");
                }
                await DuplicateConflictingEntryAsync(
                    connection,
                    transaction,
                    conflict.EntityId,
                    selectedCloud.PayloadJson,
                    resolvedUtc,
                    cancellationToken);
                duplicatedEntry = true;
                goto case ProfileSyncResolution.KeepLocal;
            case ProfileSyncResolution.KeepLocal:
                var localRow = await ReadSingleSyncRowAsync(connection, transaction, descriptor, conflict.EntityId, cancellationToken);
                operation = localRow?.PayloadJson is null ? ProfileSyncOperation.Delete : ProfileSyncOperation.Upsert;
                contentHash = localRow?.ContentHash ?? string.Empty;
                payloadJson = localRow?.PayloadJson;
                break;
            default:
                throw new InvalidOperationException("That resolution is not valid for this conflict.");
        }

        await SetSyncApplyingAsync(connection, transaction, false, cancellationToken);
        foreach (var dependent in confirmedDependentDeletes)
        {
            await InsertLocalSyncChangeAsync(
                connection,
                transaction,
                dependent.Identity.EntityType,
                dependent.Identity.EntityId,
                [dependent.State.LocalRevisionId],
                ProfileSyncOperation.Delete,
                deviceId,
                deviceName,
                resolvedUtc,
                string.Empty,
                payloadJson: null,
                cancellationToken);
        }
        await InsertLocalSyncChangeAsync(
            connection,
            transaction,
            conflict.EntityType,
            conflict.EntityId,
            parents,
            operation,
            deviceId,
            deviceName,
            resolvedUtc,
            contentHash,
            payloadJson,
            cancellationToken);
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM ProfileSyncConflicts WHERE Id = $id;",
            cancellationToken,
            ("$id", conflictId.ToString("D")));
        if (duplicatedEntry)
        {
            foreach (var table in new[] { "TimeEntries", "TimeEntrySoftware", "TimeExclusions" })
            {
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    "INSERT INTO ProfileSyncDirtyTables (TableName) VALUES ($table) ON CONFLICT (TableName) DO NOTHING;",
                    cancellationToken,
                    ("$table", table));
            }
        }
        else
        {
            await DeleteDirtySyncTableAsync(connection, transaction, descriptor.TableName, cancellationToken);
        }
        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    private async Task ResolveLegacyProfileSyncConflictAsync(
        ProfileSyncConflict conflict,
        ProfileSyncResolution resolution,
        DateTimeOffset resolvedUtc,
        CancellationToken cancellationToken)
    {
        if (resolution is not (ProfileSyncResolution.ImportLegacy or ProfileSyncResolution.IgnoreLegacy))
        {
            throw new InvalidOperationException("Choose Import or Ignore for a legacy worksheet row.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        if (resolution == ProfileSyncResolution.ImportLegacy)
        {
            var payload = conflict.Heads.FirstOrDefault()?.PayloadJson;
            var candidate = string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonSerializer.Deserialize<LegacyProfileSyncCandidate>(payload);
            if (candidate is null || candidate.ValidationError is not null ||
                candidate.StartUtc is not { } startUtc || candidate.EndUtc is not { } endUtc)
            {
                throw new InvalidOperationException(
                    candidate?.ValidationError ?? "The legacy row does not contain a usable start and end time.");
            }
            startUtc = startUtc.ToUniversalTime();
            endUtc = endUtc.ToUniversalTime();
            if (endUtc - startUtc < MinimumEntryDuration)
            {
                throw new InvalidOperationException("The legacy entry is shorter than the one-minute minimum.");
            }

            var projectId = SystemEntityIds.UnassignedProjectId;
            if (!string.IsNullOrWhiteSpace(candidate.ProjectName))
            {
                var clientName = string.IsNullOrWhiteSpace(candidate.ClientName)
                    ? "Imported"
                    : candidate.ClientName.Trim();
                var clientId = await FindOrInsertLegacyClientAsync(
                    connection,
                    transaction,
                    clientName,
                    cancellationToken);
                projectId = await FindOrInsertLegacyProjectAsync(
                    connection,
                    transaction,
                    clientId,
                    candidate.ProjectName.Trim(),
                    cancellationToken);
            }
            var taskId = string.IsNullOrWhiteSpace(candidate.TaskName)
                ? (Guid?)null
                : await FindOrInsertLegacyTaskAsync(
                    connection,
                    transaction,
                    projectId,
                    candidate.TaskName.Trim(),
                    cancellationToken);
            var entryId = candidate.EntryId ?? Guid.NewGuid();
            await using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = "SELECT COUNT(*) FROM TimeEntries WHERE Id = $id;";
                exists.Parameters.AddWithValue("$id", entryId.ToString("D"));
                if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0)
                {
                    entryId = Guid.NewGuid();
                }
            }
            var description = string.IsNullOrWhiteSpace(candidate.Description) ? null : candidate.Description.Trim();
            var pending = projectId == SystemEntityIds.UnassignedProjectId || taskId is null && description is null;
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                INSERT INTO TimeEntries
                    (Id, ProjectId, TaskId, Description, StartUtc, EndUtc, LastCheckpointUtc,
                     DetailsPending, Source, CreatedUtc, ModifiedUtc, IsPaid, IsCall)
                VALUES
                    ($id, $project, $task, $description, $start, $end, $end,
                     $pending, $source, $resolved, $resolved, $paid, $call);
                """,
                cancellationToken,
                ("$id", entryId.ToString("D")),
                ("$project", projectId.ToString("D")),
                ("$task", taskId?.ToString("D")),
                ("$description", description),
                ("$start", Format(startUtc)),
                ("$end", Format(endUtc)),
                ("$pending", pending ? 1 : 0),
                ("$source", (int)candidate.Source),
                ("$resolved", Format(resolvedUtc)),
                ("$paid", candidate.IsPaid ? 1 : 0),
                ("$call", candidate.IsCall ? 1 : 0));
        }

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM ProfileSyncConflicts WHERE Id = $id;",
            cancellationToken,
            ("$id", conflict.Id.ToString("D")));
        transaction.Commit();
        await connection.CloseAsync();
        if (resolution == ProfileSyncResolution.ImportLegacy)
        {
            await SynchronizeMonthlyLogFilesAsync(cancellationToken);
        }
    }

    private static async Task<Guid> FindOrInsertLegacyClientAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        CancellationToken cancellationToken)
    {
        var existing = await FindGuidAsync(
            connection,
            transaction,
            "SELECT Id FROM Clients WHERE Name = $name COLLATE NOCASE LIMIT 1;",
            cancellationToken,
            ("$name", name));
        if (existing is { } id)
        {
            return id;
        }
        var created = Guid.NewGuid();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "INSERT INTO Clients (Id, Name, Color, IsArchived) VALUES ($id, $name, '#766F80', 0);",
            cancellationToken,
            ("$id", created.ToString("D")),
            ("$name", name));
        return created;
    }

    private static async Task<Guid> FindOrInsertLegacyProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid clientId,
        string name,
        CancellationToken cancellationToken)
    {
        var existing = await FindGuidAsync(
            connection,
            transaction,
            "SELECT Id FROM Projects WHERE ClientId = $client AND Name = $name COLLATE NOCASE LIMIT 1;",
            cancellationToken,
            ("$client", clientId.ToString("D")),
            ("$name", name));
        if (existing is { } id)
        {
            return id;
        }
        var created = Guid.NewGuid();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "INSERT INTO Projects (Id, ClientId, Name, Color) VALUES ($id, $client, $name, '#339CFF');",
            cancellationToken,
            ("$id", created.ToString("D")),
            ("$client", clientId.ToString("D")),
            ("$name", name));
        return created;
    }

    private static async Task<Guid> FindOrInsertLegacyTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        string name,
        CancellationToken cancellationToken)
    {
        var existing = await FindGuidAsync(
            connection,
            transaction,
            "SELECT Id FROM SavedTasks WHERE ProjectId = $project AND Name = $name COLLATE NOCASE LIMIT 1;",
            cancellationToken,
            ("$project", projectId.ToString("D")),
            ("$name", name));
        if (existing is { } id)
        {
            return id;
        }
        var created = Guid.NewGuid();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "INSERT INTO SavedTasks (Id, ProjectId, Name, IsArchived, Origin) VALUES ($id, $project, $name, 0, 0);",
            cancellationToken,
            ("$id", created.ToString("D")),
            ("$project", projectId.ToString("D")),
            ("$name", name));
        return created;
    }

    private static async Task<Guid?> FindGuidAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text && Guid.TryParse(text, out var id) ? id : null;
    }

    private static async Task<IReadOnlyList<DependentSyncIdentity>> ReadDependentSyncIdentitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        var projectIds = new List<string>();
        if (entityType == "Projects")
        {
            projectIds.Add(entityId);
        }
        else
        {
            await using var projects = connection.CreateCommand();
            projects.Transaction = transaction;
            projects.CommandText = "SELECT Id FROM Projects WHERE ClientId = $client ORDER BY Name COLLATE NOCASE;";
            projects.Parameters.AddWithValue("$client", entityId);
            await using var reader = await projects.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                projectIds.Add(reader.GetString(0));
            }
        }

        var result = new List<DependentSyncIdentity>();
        foreach (var projectId in projectIds)
        {
            if (entityType == "Clients")
            {
                result.Add(new DependentSyncIdentity("Projects", projectId));
            }
            foreach (var item in new[]
                     {
                         (EntityType: "TimeEntries", Table: "TimeEntries"),
                         (EntityType: "SavedTasks", Table: "SavedTasks"),
                         (EntityType: "CustomTargets", Table: "CustomTargets"),
                         (EntityType: "RecognitionRules", Table: "RecognitionRules"),
                         (EntityType: "ProjectTargetDebtCancellations", Table: "ProjectTargetDebtCancellations"),
                         (EntityType: "TrelloBoardMappings", Table: "TrelloBoardMappings"),
                     })
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"SELECT Id FROM {QuoteIdentifier(item.Table)} WHERE ProjectId = $project;";
                command.Parameters.AddWithValue("$project", projectId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    result.Add(new DependentSyncIdentity(item.EntityType, reader.GetString(0)));
                }
            }
        }
        return result;
    }

    private static async Task ApplyDestructiveParentDeleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        var projectFilter = entityType == "Clients"
            ? "IN (SELECT Id FROM Projects WHERE ClientId = $id)"
            : "= $id";
        foreach (var table in new[] { "TimeEntries", "CustomTargets", "SavedTasks" })
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                $"DELETE FROM {QuoteIdentifier(table)} WHERE ProjectId {projectFilter};",
                cancellationToken,
                ("$id", entityId));
        }
        if (entityType == "Clients")
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "DELETE FROM Projects WHERE ClientId = $id;",
                cancellationToken,
                ("$id", entityId));
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "DELETE FROM Clients WHERE Id = $id;",
                cancellationToken,
                ("$id", entityId));
        }
        else
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "DELETE FROM Projects WHERE Id = $id;",
                cancellationToken,
                ("$id", entityId));
        }
    }

    private static async Task EnsureProfileSyncTriggersAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var descriptor in SyncTables)
        {
            var safeName = descriptor.TableName.Replace("\"", "\"\"", StringComparison.Ordinal);
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var triggerName = $"TR_ProfileSync_{safeName}_{operation}";
                await ExecuteAsync(connection, $"DROP TRIGGER IF EXISTS \"{triggerName}\";", cancellationToken);
                await ExecuteAsync(
                    connection,
                    $"""
                    CREATE TRIGGER "{triggerName}"
                    AFTER {operation} ON "{safeName}"
                    WHEN (SELECT IsApplying FROM ProfileSyncRuntime WHERE SingletonId = 1) = 0
                    BEGIN
                        INSERT INTO ProfileSyncDirtyTables (TableName) VALUES ('{safeName}')
                        ON CONFLICT (TableName) DO NOTHING;
                    END;
                    """,
                    cancellationToken);
            }

            await ExecuteParameterizedOnConnectionAsync(
                connection,
                "INSERT OR IGNORE INTO ProfileSyncDirtyTables (TableName) VALUES ($table);",
                cancellationToken,
                ("$table", descriptor.TableName));
        }
    }

    private static async Task<HashSet<string>> ReadDirtySyncTablesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT TableName FROM ProfileSyncDirtyTables;";
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private static async Task<HashSet<string>> ReadRunningEntryIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id FROM TimeEntries WHERE EndUtc IS NULL;";
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private static async Task<IReadOnlyList<SyncRow>> ReadSyncRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncTableDescriptor descriptor,
        IReadOnlySet<string> runningEntryIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {string.Join(", ", descriptor.Columns.Select(QuoteIdentifier))} FROM {QuoteIdentifier(descriptor.TableName)};";
        var rows = new List<SyncRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                for (var index = 0; index < descriptor.Columns.Length; index++)
                {
                    values[descriptor.Columns[index]] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                }

                var entityId = BuildSyncEntityId(descriptor, values);
                if (ShouldIgnoreSyncRow(descriptor, values))
                {
                    continue;
                }

                var eligible = IsSyncRowEligible(descriptor, values, runningEntryIds);
                var payloadJson = eligible ? JsonSerializer.Serialize(values) : null;
                rows.Add(new SyncRow(
                    entityId,
                    payloadJson is null ? string.Empty : ComputeSyncHash(payloadJson),
                    payloadJson));
            }
        }
        if (string.Equals(descriptor.EntityType, "TimeEntries", StringComparison.Ordinal))
        {
            for (var index = 0; index < rows.Count; index++)
            {
                if (rows[index].PayloadJson is not { } payload || !Guid.TryParse(rows[index].EntityId, out var entryId))
                {
                    continue;
                }
                var enriched = await EnrichEntrySyncPayloadAsync(
                    connection,
                    transaction,
                    descriptor,
                    entryId,
                    payload,
                    cancellationToken);
                rows[index] = new SyncRow(rows[index].EntityId, ComputeSyncHash(enriched), enriched);
            }
        }
        return rows;
    }

    private static async Task<string> EnrichEntrySyncPayloadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncTableDescriptor descriptor,
        Guid entryId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var values = DeserializeSyncPayload(payloadJson, descriptor).ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        values["__Exclusions"] = await ReadEntryExclusionPayloadsAsync(
            connection,
            transaction,
            entryId,
            cancellationToken);
        values["__SoftwareIds"] = await ReadEntrySoftwarePayloadsAsync(
            connection,
            transaction,
            entryId,
            cancellationToken);
        return JsonSerializer.Serialize(new SortedDictionary<string, object?>(values, StringComparer.Ordinal));
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadEntryExclusionPayloadsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id, StartUtc, EndUtc, Reason FROM TimeExclusions WHERE TimeEntryId = $entry ORDER BY Id;";
        command.Parameters.AddWithValue("$entry", entryId.ToString("D"));
        var result = new List<IReadOnlyDictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["EndUtc"] = reader.GetString(2),
                ["Id"] = reader.GetString(0),
                ["Reason"] = reader.GetString(3),
                ["StartUtc"] = reader.GetString(1),
            });
        }
        return result;
    }

    private static async Task<IReadOnlyList<string>> ReadEntrySoftwarePayloadsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT SoftwareId FROM TimeEntrySoftware WHERE TimeEntryId = $entry ORDER BY SoftwareId;";
        command.Parameters.AddWithValue("$entry", entryId.ToString("D"));
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private static bool ShouldIgnoreSyncRow(
        SyncTableDescriptor descriptor,
        IReadOnlyDictionary<string, object?> values)
    {
        if (string.Equals(descriptor.EntityType, "Clients", StringComparison.Ordinal) &&
            string.Equals(values["Id"]?.ToString(), SystemEntityIds.UnassignedClientId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.Equals(descriptor.EntityType, "Projects", StringComparison.Ordinal) &&
            string.Equals(values["Id"]?.ToString(), SystemEntityIds.UnassignedProjectId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return string.Equals(descriptor.EntityType, "Settings", StringComparison.Ordinal) &&
               !IsSharedProfileSetting(values["Key"]?.ToString());
    }

    private static bool IsSyncRowEligible(
        SyncTableDescriptor descriptor,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlySet<string> runningEntryIds)
    {
        if (string.Equals(descriptor.EntityType, "TimeEntries", StringComparison.Ordinal))
        {
            return values["EndUtc"] is not null;
        }
        if (string.Equals(descriptor.EntityType, "TimeEntrySoftware", StringComparison.Ordinal) ||
            string.Equals(descriptor.EntityType, "TimeExclusions", StringComparison.Ordinal))
        {
            return !runningEntryIds.Contains(values["TimeEntryId"]?.ToString() ?? string.Empty);
        }
        return true;
    }

    private static bool IsSharedProfileSetting(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) ||
            key.StartsWith("googleSheets.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, LogExportDestinationSettings.DestinationKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, SessionTrackingSettings.ResumeMarkerKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, SessionTrackingSettings.ReviewEntryKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, AccumulatedAwayReviewSettings.DailyStateKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, BreakReminderSettings.DailyUsageKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, TargetReviewSettings.LastShownDateKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return key is not (UpdateCheckSettings.LastSuccessfulCheckUtcKey or
            UpdateCheckSettings.LatestVersionKey or
            UpdateCheckSettings.ReleasePageUrlKey or
            UpdateCheckSettings.LastResultKey);
    }

    private static string BuildSyncEntityId(
        SyncTableDescriptor descriptor,
        IReadOnlyDictionary<string, object?> values) =>
        string.Join(
            CompositeSyncKeySeparator,
            descriptor.PrimaryKeyColumns.Select(column => values[column]?.ToString() ?? string.Empty));

    private static string[] ParseSyncEntityId(SyncTableDescriptor descriptor, string entityId)
    {
        var values = entityId.Split(CompositeSyncKeySeparator, StringSplitOptions.None);
        if (values.Length != descriptor.PrimaryKeyColumns.Length)
        {
            throw new InvalidDataException($"The synchronized {descriptor.EntityType} identity is invalid.");
        }
        return values;
    }

    private static async Task<SyncEntityState?> ReadSyncEntityStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT LocalRevisionId, ContentHash, IsDeleted
            FROM ProfileSyncEntityState
            WHERE EntityType = $type AND EntityId = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$type", entityType);
        command.Parameters.AddWithValue("$id", entityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SyncEntityState(entityId, Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetBoolean(2))
            : null;
    }

    private static async Task<IReadOnlyList<SyncEntityState>> ReadSyncEntityStatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entityType,
        bool onlyActive,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EntityId, LocalRevisionId, ContentHash, IsDeleted
            FROM ProfileSyncEntityState
            WHERE EntityType = $type AND ($active = 0 OR IsDeleted = 0);
            """;
        command.Parameters.AddWithValue("$type", entityType);
        command.Parameters.AddWithValue("$active", onlyActive ? 1 : 0);
        var states = new List<SyncEntityState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            states.Add(new SyncEntityState(
                reader.GetString(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetBoolean(3)));
        }
        return states;
    }

    private static async Task InsertLocalSyncChangeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entityType,
        string entityId,
        IReadOnlyCollection<Guid> parents,
        ProfileSyncOperation operation,
        Guid deviceId,
        string deviceName,
        DateTimeOffset changedUtc,
        string contentHash,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        var revisionId = Guid.NewGuid();
        var parentJson = JsonSerializer.Serialize(parents.Select(parent => parent.ToString("D")).ToArray());
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO ProfileSyncOutbox
                (RevisionId, EntityType, EntityId, ParentRevisionIdsJson, Operation, DeviceId, DeviceName, ChangedUtc, ContentHash, PayloadJson)
            VALUES
                ($revision, $type, $id, $parents, $operation, $device, $name, $changed, $hash, $payload);
            """,
            cancellationToken,
            ("$revision", revisionId.ToString("D")),
            ("$type", entityType),
            ("$id", entityId),
            ("$parents", parentJson),
            ("$operation", (int)operation),
            ("$device", deviceId.ToString("D")),
            ("$name", deviceName),
            ("$changed", Format(changedUtc)),
            ("$hash", contentHash),
            ("$payload", payloadJson));
        await WriteSyncEntityStateAsync(
            connection,
            transaction,
            entityType,
            entityId,
            revisionId,
            contentHash,
            operation == ProfileSyncOperation.Delete,
            cancellationToken);
    }

    private static Task WriteSyncEntityStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entityType,
        string entityId,
        Guid revisionId,
        string contentHash,
        bool isDeleted,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO ProfileSyncEntityState
                (EntityType, EntityId, LocalRevisionId, ContentHash, IsDeleted)
            VALUES ($type, $id, $revision, $hash, $deleted)
            ON CONFLICT (EntityType, EntityId) DO UPDATE SET
                LocalRevisionId = excluded.LocalRevisionId,
                ContentHash = excluded.ContentHash,
                IsDeleted = excluded.IsDeleted;
            """,
            cancellationToken,
            ("$type", entityType),
            ("$id", entityId),
            ("$revision", revisionId.ToString("D")),
            ("$hash", contentHash),
            ("$deleted", isDeleted ? 1 : 0));

    private static async Task<IReadOnlyList<ProfileSyncChange>> ReadProfileSyncOutboxAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RevisionId, EntityType, EntityId, ParentRevisionIdsJson, Operation,
                   DeviceId, DeviceName, ChangedUtc, ContentHash, PayloadJson
            FROM ProfileSyncOutbox
            ORDER BY ChangedUtc, RevisionId;
            """;
        var changes = new List<ProfileSyncChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            changes.Add(new ProfileSyncChange(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                DeserializeRevisionIds(reader.GetString(3)),
                (ProfileSyncOperation)reader.GetInt32(4),
                Guid.Parse(reader.GetString(5)),
                reader.GetString(6),
                Parse(reader.GetString(7)),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return changes;
    }

    private static IReadOnlyList<Guid> DeserializeRevisionIds(string json) =>
        (JsonSerializer.Deserialize<string[]>(json) ?? [])
        .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty)
        .ToArray();

    private static IReadOnlyList<ProfileSyncChange> DeserializeSyncChanges(string json) =>
        JsonSerializer.Deserialize<ProfileSyncChange[]>(json) ?? [];

    private static bool IsSyncAncestor(
        Guid ancestor,
        Guid descendant,
        IReadOnlyDictionary<Guid, ProfileSyncChange> changesByRevision)
    {
        if (ancestor == descendant)
        {
            return true;
        }
        var pending = new Stack<Guid>();
        var visited = new HashSet<Guid>();
        pending.Push(descendant);
        while (pending.TryPop(out var current) && visited.Add(current))
        {
            if (!changesByRevision.TryGetValue(current, out var change))
            {
                continue;
            }
            foreach (var parent in change.ParentRevisionIds)
            {
                if (parent == ancestor)
                {
                    return true;
                }
                pending.Push(parent);
            }
        }
        return false;
    }

    private static async Task ApplyProfileSyncChangeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncTableDescriptor descriptor,
        ProfileSyncChange change,
        CancellationToken cancellationToken)
    {
        if (change.Operation == ProfileSyncOperation.Delete)
        {
            var keyValues = ParseSyncEntityId(descriptor, change.EntityId);
            for (var index = 0; index < keyValues.Length; index++)
            {
                keyValues[index] = await ResolveSyncAliasAsync(
                    connection,
                    transaction,
                    AliasEntityType(descriptor, descriptor.PrimaryKeyColumns[index]),
                    keyValues[index],
                    cancellationToken);
            }
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {QuoteIdentifier(descriptor.TableName)} WHERE {string.Join(" AND ", descriptor.PrimaryKeyColumns.Select((column, index) => $"{QuoteIdentifier(column)} = $key{index}"))};";
            for (var index = 0; index < keyValues.Length; index++)
            {
                delete.Parameters.AddWithValue($"$key{index}", keyValues[index]);
            }
            await delete.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(change.PayloadJson))
        {
            throw new InvalidDataException("A synchronized update is missing its data payload.");
        }
        var values = DeserializeSyncPayload(change.PayloadJson, descriptor).ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        await RewriteSyncAliasesAsync(connection, transaction, descriptor, values, cancellationToken);
        if (string.Equals(descriptor.EntityType, "Settings", StringComparison.Ordinal) &&
            !IsSharedProfileSetting(values["Key"]?.ToString()))
        {
            return;
        }
        if (string.Equals(descriptor.EntityType, "TimeEntries", StringComparison.Ordinal) && values["EndUtc"] is null)
        {
            throw new InvalidDataException("A running timer cannot be imported from another computer.");
        }

        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        var parameterNames = descriptor.Columns.Select((_, index) => $"$value{index}").ToArray();
        var nonKeyColumns = descriptor.Columns.Except(descriptor.PrimaryKeyColumns, StringComparer.Ordinal).ToArray();
        var conflictAction = nonKeyColumns.Length == 0
            ? "DO NOTHING"
            : $"DO UPDATE SET {string.Join(", ", nonKeyColumns.Select(column => $"{QuoteIdentifier(column)} = excluded.{QuoteIdentifier(column)}"))}";
        upsert.CommandText = $"""
            INSERT INTO {QuoteIdentifier(descriptor.TableName)}
                ({string.Join(", ", descriptor.Columns.Select(QuoteIdentifier))})
            VALUES ({string.Join(", ", parameterNames)})
            ON CONFLICT ({string.Join(", ", descriptor.PrimaryKeyColumns.Select(QuoteIdentifier))}) {conflictAction};
            """;
        for (var index = 0; index < descriptor.Columns.Length; index++)
        {
            upsert.Parameters.AddWithValue(parameterNames[index], values[descriptor.Columns[index]] ?? DBNull.Value);
        }
        await upsert.ExecuteNonQueryAsync(cancellationToken);
        if (string.Equals(descriptor.EntityType, "TimeEntries", StringComparison.Ordinal))
        {
            await ApplyEntryAssociationsFromPayloadAsync(
                connection,
                transaction,
                values["Id"]?.ToString() ?? change.EntityId,
                change.PayloadJson,
                cancellationToken);
        }
    }

    private static async Task ApplyEntryAssociationsFromPayloadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entryId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.TryGetProperty("__SoftwareIds", out var softwareElement))
        {
            if (softwareElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The synchronized entry software list is invalid.");
            }
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "DELETE FROM TimeEntrySoftware WHERE TimeEntryId = $entry;",
                cancellationToken,
                ("$entry", entryId));
            foreach (var item in softwareElement.EnumerateArray())
            {
                var softwareId = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (string.IsNullOrWhiteSpace(softwareId))
                {
                    throw new InvalidDataException("A synchronized entry contains an invalid software identity.");
                }
                softwareId = await ResolveSyncAliasAsync(
                    connection,
                    transaction,
                    "Software",
                    softwareId,
                    cancellationToken);
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    "INSERT OR IGNORE INTO TimeEntrySoftware (TimeEntryId, SoftwareId) VALUES ($entry, $software);",
                    cancellationToken,
                    ("$entry", entryId),
                    ("$software", softwareId));
            }
        }

        if (!document.RootElement.TryGetProperty("__Exclusions", out var exclusionsElement))
        {
            return;
        }
        if (exclusionsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The synchronized entry exclusion list is invalid.");
        }
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM TimeExclusions WHERE TimeEntryId = $entry;",
            cancellationToken,
            ("$entry", entryId));
        foreach (var item in exclusionsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("Id", out var idElement) ||
                !item.TryGetProperty("StartUtc", out var startElement) ||
                !item.TryGetProperty("EndUtc", out var endElement) ||
                !item.TryGetProperty("Reason", out var reasonElement))
            {
                throw new InvalidDataException("A synchronized entry exclusion is incomplete.");
            }
            var exclusionId = idElement.GetString();
            var startUtc = startElement.GetString();
            var endUtc = endElement.GetString();
            var reason = reasonElement.GetString();
            if (!Guid.TryParse(exclusionId, out _) ||
                !DateTimeOffset.TryParse(startUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) ||
                !DateTimeOffset.TryParse(endUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) ||
                reason is null)
            {
                throw new InvalidDataException("A synchronized entry exclusion is invalid.");
            }
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "INSERT INTO TimeExclusions (Id, TimeEntryId, StartUtc, EndUtc, Reason) VALUES ($id, $entry, $start, $end, $reason);",
                cancellationToken,
                ("$id", exclusionId),
                ("$entry", entryId),
                ("$start", startUtc),
                ("$end", endUtc),
                ("$reason", reason));
        }
    }

    private static async Task<bool> TryCoalesceSynchronizedIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncTableDescriptor descriptor,
        ProfileSyncChange change,
        CancellationToken cancellationToken)
    {
        if (change.Operation != ProfileSyncOperation.Upsert || string.IsNullOrWhiteSpace(change.PayloadJson) ||
            descriptor.EntityType is not ("Clients" or "Projects" or "SavedTasks" or "Tags" or "Software"))
        {
            return false;
        }

        var values = DeserializeSyncPayload(change.PayloadJson, descriptor).ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        var incomingId = values["Id"]?.ToString() ?? change.EntityId;
        var existingAlias = await ResolveSyncAliasAsync(
            connection,
            transaction,
            descriptor.EntityType,
            incomingId,
            cancellationToken);
        if (!string.Equals(existingAlias, incomingId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        await RewriteSyncAliasesAsync(connection, transaction, descriptor, values, cancellationToken);

        string sql;
        (string Name, object? Value)[] parameters;
        switch (descriptor.EntityType)
        {
            case "Clients":
                sql = "SELECT Id FROM Clients WHERE Name = $name COLLATE NOCASE AND Id <> $id LIMIT 1;";
                parameters = [("$name", values["Name"]), ("$id", incomingId)];
                break;
            case "Projects":
                sql = "SELECT Id FROM Projects WHERE ClientId = $parent AND Name = $name COLLATE NOCASE AND Id <> $id LIMIT 1;";
                parameters = [("$parent", values["ClientId"]), ("$name", values["Name"]), ("$id", incomingId)];
                break;
            case "SavedTasks":
                sql = "SELECT Id FROM SavedTasks WHERE ProjectId = $parent AND Name = $name COLLATE NOCASE AND Id <> $id LIMIT 1;";
                parameters = [("$parent", values["ProjectId"]), ("$name", values["Name"]), ("$id", incomingId)];
                break;
            case "Tags":
                sql = "SELECT Id FROM Tags WHERE Name = $name COLLATE NOCASE AND Id <> $id LIMIT 1;";
                parameters = [("$name", values["Name"]), ("$id", incomingId)];
                break;
            default:
                sql = "SELECT Id FROM Software WHERE ProcessName = $process COLLATE NOCASE AND Id <> $id LIMIT 1;";
                parameters = [("$process", values["ProcessName"]), ("$id", incomingId)];
                break;
        }
        var canonical = await FindTextAsync(connection, transaction, sql, cancellationToken, parameters);
        if (string.IsNullOrWhiteSpace(canonical))
        {
            return false;
        }

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO ProfileSyncAliases (EntityType, AliasId, CanonicalId)
            VALUES ($type, $alias, $canonical)
            ON CONFLICT (EntityType, AliasId) DO UPDATE SET CanonicalId = excluded.CanonicalId;
            """,
            cancellationToken,
            ("$type", descriptor.EntityType),
            ("$alias", incomingId),
            ("$canonical", canonical));
        return true;
    }

    private static async Task RewriteSyncAliasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncTableDescriptor descriptor,
        IDictionary<string, object?> values,
        CancellationToken cancellationToken)
    {
        foreach (var column in descriptor.Columns)
        {
            if (values[column] is not string value || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            var aliasType = AliasEntityType(descriptor, column);
            if (aliasType is null)
            {
                continue;
            }
            values[column] = await ResolveSyncAliasAsync(
                connection,
                transaction,
                aliasType,
                value,
                cancellationToken);
        }
    }

    private static string? AliasEntityType(SyncTableDescriptor descriptor, string column) => column switch
    {
        "ClientId" => "Clients",
        "ProjectId" => "Projects",
        "TaskId" => "SavedTasks",
        "TagId" => "Tags",
        "SoftwareId" => "Software",
        "MappingId" => "TrelloBoardMappings",
        "TimeEntryId" => "TimeEntries",
        "Id" when descriptor.EntityType is "Clients" or "Projects" or "SavedTasks" or "Tags" or "Software" => descriptor.EntityType,
        _ => null,
    };

    private static async Task<string> ResolveSyncAliasAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? entityType,
        string value,
        CancellationToken cancellationToken)
    {
        if (entityType is null)
        {
            return value;
        }
        var current = value;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current))
        {
            var next = await FindTextAsync(
                connection,
                transaction,
                "SELECT CanonicalId FROM ProfileSyncAliases WHERE EntityType = $type AND AliasId = $alias LIMIT 1;",
                cancellationToken,
                ("$type", entityType),
                ("$alias", current));
            if (string.IsNullOrWhiteSpace(next) || string.Equals(next, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = next;
        }
        return current;
    }

    private static async Task<string?> FindTextAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static IReadOnlyDictionary<string, object?> DeserializeSyncPayload(
        string payloadJson,
        SyncTableDescriptor descriptor)
    {
        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The synchronized record payload is invalid.");
        }
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in descriptor.Columns)
        {
            if (!document.RootElement.TryGetProperty(column, out var element))
            {
                throw new InvalidDataException($"The synchronized {descriptor.EntityType} record is missing {column}.");
            }
            values[column] = JsonElementToSqlValue(element);
        }
        return values;
    }

    private static object? JsonElementToSqlValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        _ => throw new InvalidDataException("A synchronized cell value has an unsupported type."),
    };

    private static async Task UpsertProfileSyncConflictAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProfileSyncConflict conflict,
        CancellationToken cancellationToken) =>
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO ProfileSyncConflicts
                (Id, EntityType, EntityId, Kind, HeadsJson, DetectedUtc, Summary, RelatedEntityIdsJson)
            VALUES ($id, $type, $entity, $kind, $heads, $detected, $summary, $related)
            ON CONFLICT (EntityType, EntityId) DO UPDATE SET
                Kind = excluded.Kind,
                HeadsJson = excluded.HeadsJson,
                DetectedUtc = excluded.DetectedUtc,
                Summary = excluded.Summary,
                RelatedEntityIdsJson = excluded.RelatedEntityIdsJson;
            """,
            cancellationToken,
            ("$id", conflict.Id.ToString("D")),
            ("$type", conflict.EntityType),
            ("$entity", conflict.EntityId),
            ("$kind", (int)conflict.Kind),
            ("$heads", JsonSerializer.Serialize(conflict.Heads)),
            ("$detected", Format(conflict.DetectedUtc)),
            ("$summary", conflict.Summary),
            ("$related", conflict.RelatedEntityIdsJson));

    private static Task DeleteProfileSyncConflictAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string entityType,
        string entityId,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM ProfileSyncConflicts WHERE EntityType = $type AND EntityId = $id;",
            cancellationToken,
            ("$type", entityType),
            ("$id", entityId));

    private static Task SetSyncApplyingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool applying,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            connection,
            transaction,
            "UPDATE ProfileSyncRuntime SET IsApplying = $applying WHERE SingletonId = 1;",
            cancellationToken,
            ("$applying", applying ? 1 : 0));

    private static Task DeleteDirtySyncTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM ProfileSyncDirtyTables WHERE TableName = $table;",
            cancellationToken,
            ("$table", table));

    private static async Task<SyncRow?> ReadSingleSyncRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncTableDescriptor descriptor,
        string entityId,
        CancellationToken cancellationToken)
    {
        var keyValues = ParseSyncEntityId(descriptor, entityId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {string.Join(", ", descriptor.Columns.Select(QuoteIdentifier))} FROM {QuoteIdentifier(descriptor.TableName)} WHERE {string.Join(" AND ", descriptor.PrimaryKeyColumns.Select((column, index) => $"{QuoteIdentifier(column)} = $key{index}"))} LIMIT 1;";
        for (var index = 0; index < keyValues.Length; index++)
        {
            command.Parameters.AddWithValue($"$key{index}", keyValues[index]);
        }
        string payload;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < descriptor.Columns.Length; index++)
            {
                values[descriptor.Columns[index]] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }
            payload = JsonSerializer.Serialize(values);
        }
        if (string.Equals(descriptor.EntityType, "TimeEntries", StringComparison.Ordinal) &&
            Guid.TryParse(entityId, out var entryId))
        {
            payload = await EnrichEntrySyncPayloadAsync(
                connection,
                transaction,
                descriptor,
                entryId,
                payload,
                cancellationToken);
        }
        return new SyncRow(entityId, ComputeSyncHash(payload), payload);
    }

    private static async Task DuplicateConflictingEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string originalEntityId,
        string payloadJson,
        DateTimeOffset resolvedUtc,
        CancellationToken cancellationToken)
    {
        var descriptor = FindSyncTable("TimeEntries")!;
        var values = DeserializeSyncPayload(payloadJson, descriptor).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var duplicateId = Guid.NewGuid();
        values["Id"] = duplicateId.ToString("D");
        values["CreatedUtc"] = Format(resolvedUtc);
        values["ModifiedUtc"] = Format(resolvedUtc);
        values["LastCheckpointUtc"] = values["EndUtc"];
        var hasSoftwarePayload = false;
        var hasExclusionPayload = false;
        using (var document = JsonDocument.Parse(payloadJson))
        {
            if (document.RootElement.TryGetProperty("__SoftwareIds", out var softwareElement) &&
                softwareElement.ValueKind == JsonValueKind.Array)
            {
                hasSoftwarePayload = true;
                values["__SoftwareIds"] = softwareElement
                    .EnumerateArray()
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray();
            }
            if (document.RootElement.TryGetProperty("__Exclusions", out var exclusionsElement) &&
                exclusionsElement.ValueKind == JsonValueKind.Array)
            {
                hasExclusionPayload = true;
                var duplicateExclusions = new List<IReadOnlyDictionary<string, object?>>();
                foreach (var item in exclusionsElement.EnumerateArray())
                {
                    duplicateExclusions.Add(new SortedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["EndUtc"] = item.GetProperty("EndUtc").GetString(),
                        ["Id"] = Guid.NewGuid().ToString("D"),
                        ["Reason"] = item.GetProperty("Reason").GetString(),
                        ["StartUtc"] = item.GetProperty("StartUtc").GetString(),
                    });
                }
                values["__Exclusions"] = duplicateExclusions;
            }
        }
        var duplicatePayload = JsonSerializer.Serialize(new SortedDictionary<string, object?>(values, StringComparer.Ordinal));
        await ApplyProfileSyncChangeAsync(
            connection,
            transaction,
            descriptor,
            new ProfileSyncChange(
                Guid.NewGuid(),
                descriptor.EntityType,
                values["Id"]!.ToString()!,
                [],
                ProfileSyncOperation.Upsert,
                Guid.Empty,
                string.Empty,
                resolvedUtc,
                ComputeSyncHash(duplicatePayload),
                duplicatePayload),
            cancellationToken);
        if (Guid.TryParse(originalEntityId, out var originalId) && !hasSoftwarePayload)
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                INSERT OR IGNORE INTO TimeEntrySoftware (TimeEntryId, SoftwareId)
                SELECT $duplicate, SoftwareId FROM TimeEntrySoftware WHERE TimeEntryId = $original;
                """,
                cancellationToken,
                ("$duplicate", duplicateId.ToString("D")),
                ("$original", originalId.ToString("D")));
        }
        if (Guid.TryParse(originalEntityId, out originalId) && !hasExclusionPayload)
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                INSERT INTO TimeExclusions (Id, TimeEntryId, StartUtc, EndUtc, Reason)
                SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
                       substr(lower(hex(randomblob(2))), 2) || '-' ||
                       substr('89ab', abs(random()) % 4 + 1, 1) ||
                       substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
                       $duplicate, StartUtc, EndUtc, Reason
                FROM TimeExclusions WHERE TimeEntryId = $original;
                """,
                cancellationToken,
                ("$duplicate", duplicateId.ToString("D")),
                ("$original", originalId.ToString("D")));
        }
    }

    private static string ComputeSyncHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static SyncTableDescriptor? FindSyncTable(string entityType) =>
        SyncTables.FirstOrDefault(table => string.Equals(table.EntityType, entityType, StringComparison.Ordinal));

    private static int GetSyncApplyOrder(string entityType) => FindSyncTable(entityType)?.ApplyOrder ?? int.MaxValue;

    private static string BuildConflictSummary(string entityType, ProfileSyncConflictKind kind) => kind switch
    {
        ProfileSyncConflictKind.DeleteVersusEdit => $"{entityType}: one computer deleted this record while another changed it.",
        _ => $"{entityType}: multiple computers changed this record from the same earlier version.",
    };

    private static bool IsLikelyIdentityCollision(SqliteException exception) =>
        exception.SqliteErrorCode == 19 && exception.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeSyncError(string value)
    {
        var singleLine = string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 240 ? singleLine : singleLine[..240];
    }

    private sealed record SyncTableDescriptor(
        string EntityType,
        string TableName,
        string[] PrimaryKeyColumns,
        string[] Columns,
        int ApplyOrder);

    private sealed record SyncRow(string EntityId, string ContentHash, string? PayloadJson);

    private sealed record SyncEntityState(
        string EntityId,
        Guid LocalRevisionId,
        string ContentHash,
        bool IsDeleted);

    private sealed record DependentSyncIdentity(string EntityType, string EntityId);
}
