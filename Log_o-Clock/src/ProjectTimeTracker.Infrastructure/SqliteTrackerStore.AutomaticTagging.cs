using System.Globalization;
using Microsoft.Data.Sqlite;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Infrastructure;

public sealed partial class SqliteTrackerStore
{
    public Task<IReadOnlyList<AutomaticTagConcept>> GetAutomaticTagConceptsAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT c.TagId, t.Name, c.MatchText, c.BuiltInKey, c.ModifiedUtc
            FROM AutomaticTagConcepts c
            JOIN Tags t ON t.Id = c.TagId
            ORDER BY t.Name COLLATE NOCASE;
            """,
            reader => new AutomaticTagConcept(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                Parse(reader.GetString(4))),
            cancellationToken);

    public async Task<IReadOnlySet<string>> GetAutomaticTagConceptSuppressionsAsync(
        CancellationToken cancellationToken = default)
    {
        var items = await QueryAsync(
            "SELECT BuiltInKey FROM AutomaticTagConceptSuppressions ORDER BY BuiltInKey COLLATE NOCASE;",
            reader => reader.GetString(0),
            cancellationToken);
        return items.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<TaskAutomaticTagPreference?> GetTaskAutomaticTagPreferenceAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.TaskId, p.TagId, t.Name, p.IsSuppressed, p.ModifiedUtc
            FROM TaskAutomaticTagPreferences p
            LEFT JOIN Tags t ON t.Id = p.TagId
            WHERE p.TaskId = $task
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TaskAutomaticTagPreference(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3),
                Parse(reader.GetString(4)))
            : null;
    }

    public async Task<AutomaticTagHistoryEvidence?> GetAutomaticTagHistoryEvidenceAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var descriptions = await QueryAsync(
            """
            SELECT Description
            FROM TimeEntries
            WHERE TaskId = $task
              AND TRIM(COALESCE(Description, '')) <> '';
            """,
            reader => reader.GetString(0),
            cancellationToken,
            ("$task", taskId.ToString("D")));
        var taggedEntries = descriptions
            .Select(TagParser.Extract)
            .Where(tags => tags.Count > 0)
            .ToArray();
        if (taggedEntries.Length == 0)
        {
            return null;
        }

        var counts = taggedEntries
            .SelectMany(tags => tags)
            .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Name = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        var leader = counts[0];
        await using var connection = await OpenAsync(cancellationToken);
        await using var tagCommand = connection.CreateCommand();
        tagCommand.CommandText = "SELECT Id FROM Tags WHERE Name = $name COLLATE NOCASE LIMIT 1;";
        tagCommand.Parameters.AddWithValue("$name", leader.Name);
        var tagId = (string?)await tagCommand.ExecuteScalarAsync(cancellationToken);
        return tagId is null
            ? null
            : new AutomaticTagHistoryEvidence(
                Guid.Parse(tagId),
                leader.Name,
                leader.Count,
                taggedEntries.Length,
                counts.Length > 1 ? counts[1].Count : 0);
    }

    public Task<IReadOnlyList<AutomaticTaggingQueueItem>> GetAutomaticTaggingQueueAsync(
        AutomaticTagQueueState? state = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return QueryAsync(
            """
            SELECT q.EntryId, e.ProjectId, e.TaskId, task.Name, e.Description,
                   e.StartUtc, e.EndUtc, COALESCE(SUM(x.EndUtc IS NOT NULL) * 0, 0),
                   q.State, q.ProposedTagId, q.ProposedTagName, q.ProposedBuiltInKey,
                   q.Confidence, q.InputHash, q.ClassifierVersion, q.CreatedUtc,
                   COALESCE((
                       SELECT SUM(CAST(strftime('%s', exclusion.EndUtc) AS INTEGER) -
                                  CAST(strftime('%s', exclusion.StartUtc) AS INTEGER))
                       FROM TimeExclusions exclusion
                       WHERE exclusion.TimeEntryId = e.Id
                   ), 0)
            FROM AutomaticTaggingQueue q
            JOIN TimeEntries e ON e.Id = q.EntryId
            JOIN SavedTasks task ON task.Id = e.TaskId
            LEFT JOIN TimeExclusions x ON 1 = 0
            WHERE ($state IS NULL OR q.State = $state)
            GROUP BY q.EntryId
            ORDER BY q.CreatedUtc, q.EntryId
            LIMIT $limit;
            """,
            reader => new AutomaticTaggingQueueItem(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
                reader.GetInt64(16),
                (AutomaticTagQueueState)reader.GetInt32(8),
                reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetDouble(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetString(14),
                Parse(reader.GetString(15))),
            cancellationToken,
            ("$state", state is null ? null : (int)state.Value),
            ("$limit", limit));
    }

    public async Task SetTaskAutomaticTagPreferenceAsync(
        Guid taskId,
        Guid? tagId,
        bool isSuppressed,
        DateTimeOffset modifiedUtc,
        CancellationToken cancellationToken = default)
    {
        if (isSuppressed == (tagId is not null))
        {
            throw new ArgumentException("Choose either one tag or suppression.");
        }

        await ExecuteParameterizedAsync(
            """
            INSERT INTO TaskAutomaticTagPreferences (TaskId, TagId, IsSuppressed, ModifiedUtc)
            VALUES ($task, $tag, $suppressed, $modified)
            ON CONFLICT(TaskId) DO UPDATE SET
                TagId = excluded.TagId,
                IsSuppressed = excluded.IsSuppressed,
                ModifiedUtc = excluded.ModifiedUtc;
            """,
            cancellationToken,
            ("$task", taskId.ToString("D")),
            ("$tag", tagId?.ToString("D")),
            ("$suppressed", isSuppressed ? 1 : 0),
            ("$modified", Format(modifiedUtc)));
    }

    public Task SuppressAutomaticTagConceptAsync(
        string builtInKey,
        DateTimeOffset modifiedUtc,
        CancellationToken cancellationToken = default)
    {
        builtInKey = Required(builtInKey, nameof(builtInKey)).ToLowerInvariant();
        return ExecuteParameterizedAsync(
            """
            INSERT INTO AutomaticTagConceptSuppressions (BuiltInKey, ModifiedUtc)
            VALUES ($key, $modified)
            ON CONFLICT(BuiltInKey) DO UPDATE SET ModifiedUtc = excluded.ModifiedUtc;
            """,
            cancellationToken,
            ("$key", builtInKey),
            ("$modified", Format(modifiedUtc)));
    }

    public Task SaveAutomaticTagSuggestionAsync(
        Guid entryId,
        AutomaticTagPolicyDecision suggestion,
        string inputHash,
        CancellationToken cancellationToken = default)
    {
        if (suggestion.Kind != AutomaticTagDecisionKind.Suggest ||
            string.IsNullOrWhiteSpace(suggestion.TagName))
        {
            throw new ArgumentException("A reviewable automatic-tag suggestion is required.", nameof(suggestion));
        }

        return ExecuteParameterizedAsync(
            """
            UPDATE AutomaticTaggingQueue
            SET State = 1,
                ProposedTagId = $tag,
                ProposedTagName = $name,
                ProposedBuiltInKey = $builtIn,
                Confidence = $confidence,
                InputHash = $hash,
                ClassifierVersion = $version
            WHERE EntryId = $entry;
            """,
            cancellationToken,
            ("$tag", suggestion.TagId?.ToString("D")),
            ("$name", suggestion.TagName),
            ("$builtIn", suggestion.BuiltInKey),
            ("$confidence", suggestion.Confidence),
            ("$hash", inputHash),
            ("$version", AutomaticTaggingSettings.ClassifierVersion),
            ("$entry", entryId.ToString("D")));
    }

    public async Task<AutomaticTagApplyResult> ApplyAutomaticTagAsync(
        Guid entryId,
        AutomaticTagPolicyDecision decision,
        bool rememberForTask = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = TagParser.Normalize(decision.TagName);
        if (decision.Kind is not (AutomaticTagDecisionKind.Apply or AutomaticTagDecisionKind.Suggest) ||
            normalizedName is null)
        {
            throw new ArgumentException("An applicable automatic-tag decision is required.", nameof(decision));
        }

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        Guid projectId;
        Guid taskId;
        string? description;
        await using (var entryCommand = connection.CreateCommand())
        {
            entryCommand.Transaction = transaction;
            entryCommand.CommandText =
                "SELECT ProjectId, TaskId, Description FROM TimeEntries WHERE Id = $entry LIMIT 1;";
            entryCommand.Parameters.AddWithValue("$entry", entryId.ToString("D"));
            await using var reader = await entryCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(1))
            {
                await DeleteAutomaticTagQueueItemAsync(connection, transaction, entryId, cancellationToken);
                transaction.Commit();
                return AutomaticTagApplyResult.MissingEntry;
            }

            projectId = Guid.Parse(reader.GetString(0));
            taskId = Guid.Parse(reader.GetString(1));
            description = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        if (TagParser.Extract(description).Count > 0)
        {
            await DeleteAutomaticTagQueueItemAsync(connection, transaction, entryId, cancellationToken);
            transaction.Commit();
            return AutomaticTagApplyResult.AlreadyTagged;
        }

        var tagId = decision.TagId;
        if (tagId is not null &&
            !await IsTagAvailableForProjectAsync(connection, transaction, tagId.Value, projectId, cancellationToken))
        {
            tagId = null;
        }

        if (tagId is null)
        {
            await using var existingCommand = connection.CreateCommand();
            existingCommand.Transaction = transaction;
            existingCommand.CommandText = "SELECT Id, IsGlobal FROM Tags WHERE Name = $name COLLATE NOCASE LIMIT 1;";
            existingCommand.Parameters.AddWithValue("$name", normalizedName);
            await using var existingReader = await existingCommand.ExecuteReaderAsync(cancellationToken);
            if (await existingReader.ReadAsync(cancellationToken))
            {
                if (!existingReader.GetBoolean(1))
                {
                    transaction.Commit();
                    return AutomaticTagApplyResult.NeedsReview;
                }

                tagId = Guid.Parse(existingReader.GetString(0));
            }
        }

        if (tagId is null)
        {
            if (decision.BuiltInKey is null ||
                await IsAutomaticTagConceptSuppressedAsync(
                    connection,
                    transaction,
                    decision.BuiltInKey,
                    cancellationToken))
            {
                transaction.Commit();
                return AutomaticTagApplyResult.NeedsReview;
            }

            tagId = Guid.NewGuid();
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "INSERT INTO Tags (Id, Name, Color, IsGlobal) VALUES ($id, $name, '#7386C8', 1);",
                cancellationToken,
                ("$id", tagId.Value.ToString("D")),
                ("$name", normalizedName));
        }

        if (decision.BuiltInKey is not null)
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                INSERT INTO AutomaticTagConcepts (TagId, BuiltInKey, MatchText, ModifiedUtc)
                VALUES ($tag, $key, $match, $modified)
                ON CONFLICT(TagId) DO UPDATE SET
                    BuiltInKey = excluded.BuiltInKey,
                    MatchText = excluded.MatchText,
                    ModifiedUtc = excluded.ModifiedUtc;
                """,
                cancellationToken,
                ("$tag", tagId.Value.ToString("D")),
                ("$key", decision.BuiltInKey.ToLowerInvariant()),
                ("$match", decision.MatchText ?? normalizedName),
                ("$modified", Format(DateTimeOffset.UtcNow)));
        }

        var modifiedUtc = DateTimeOffset.UtcNow;
        var taggedDescription = TagParser.AppendBracketedTags(description, [normalizedName]);
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "UPDATE TimeEntries SET Description = $description, ModifiedUtc = $modified WHERE Id = $entry;",
            cancellationToken,
            ("$description", taggedDescription),
            ("$modified", Format(modifiedUtc)),
            ("$entry", entryId.ToString("D")));
        if (rememberForTask)
        {
            await UpsertTaskAutomaticTagPreferenceAsync(
                connection,
                transaction,
                taskId,
                tagId,
                isSuppressed: false,
                modifiedUtc,
                cancellationToken);
        }

        await DeleteAutomaticTagQueueItemAsync(connection, transaction, entryId, cancellationToken);
        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
        return AutomaticTagApplyResult.Applied;
    }

    public async Task DismissAutomaticTagSuggestionAsync(
        Guid entryId,
        bool suppressTask,
        DateTimeOffset modifiedUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        if (suppressTask)
        {
            Guid? taskId = null;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT e.TaskId FROM AutomaticTaggingQueue q JOIN TimeEntries e ON e.Id = q.EntryId WHERE q.EntryId = $entry;";
            command.Parameters.AddWithValue("$entry", entryId.ToString("D"));
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is string taskValue)
            {
                taskId = Guid.Parse(taskValue);
            }

            if (taskId is not null)
            {
                await UpsertTaskAutomaticTagPreferenceAsync(
                    connection,
                    transaction,
                    taskId.Value,
                    tagId: null,
                    isSuppressed: true,
                    modifiedUtc,
                    cancellationToken);
            }
        }

        await DeleteAutomaticTagQueueItemAsync(connection, transaction, entryId, cancellationToken);
        transaction.Commit();
    }

    public Task DeleteExpiredAutomaticTaggingQueueAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteParameterizedAsync(
            "DELETE FROM AutomaticTaggingQueue WHERE CreatedUtc < $cutoff;",
            cancellationToken,
            ("$cutoff", Format(cutoffUtc)));

    private static Task EnqueueAutomaticTaggingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO AutomaticTaggingQueue
                (EntryId, State, ProposedTagId, ProposedTagName, ProposedBuiltInKey,
                 Confidence, InputHash, ClassifierVersion, CreatedUtc)
            VALUES
                ($entry, 0, NULL, NULL, NULL, NULL, NULL, $version, $created);
            """,
            cancellationToken,
            ("$entry", entryId.ToString("D")),
            ("$version", AutomaticTaggingSettings.ClassifierVersion),
            ("$created", Format(DateTimeOffset.UtcNow)));

    private static Task DeleteAutomaticTagQueueItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM AutomaticTaggingQueue WHERE EntryId = $entry;",
            cancellationToken,
            ("$entry", entryId.ToString("D")));

    private static async Task<bool> IsTagAvailableForProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid tagId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM Tags t
            WHERE t.Id = $tag
              AND (t.IsGlobal = 1 OR EXISTS (
                  SELECT 1 FROM ProjectTags pt
                  WHERE pt.TagId = t.Id AND pt.ProjectId = $project));
            """;
        command.Parameters.AddWithValue("$tag", tagId.ToString("D"));
        command.Parameters.AddWithValue("$project", projectId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<bool> IsAutomaticTagConceptSuppressedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string builtInKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM AutomaticTagConceptSuppressions WHERE BuiltInKey = $key COLLATE NOCASE;";
        command.Parameters.AddWithValue("$key", builtInKey);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    private static Task UpsertTaskAutomaticTagPreferenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid taskId,
        Guid? tagId,
        bool isSuppressed,
        DateTimeOffset modifiedUtc,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO TaskAutomaticTagPreferences (TaskId, TagId, IsSuppressed, ModifiedUtc)
            VALUES ($task, $tag, $suppressed, $modified)
            ON CONFLICT(TaskId) DO UPDATE SET
                TagId = excluded.TagId,
                IsSuppressed = excluded.IsSuppressed,
                ModifiedUtc = excluded.ModifiedUtc;
            """,
            cancellationToken,
            ("$task", taskId.ToString("D")),
            ("$tag", tagId?.ToString("D")),
            ("$suppressed", isSuppressed ? 1 : 0),
            ("$modified", Format(modifiedUtc)));
}
