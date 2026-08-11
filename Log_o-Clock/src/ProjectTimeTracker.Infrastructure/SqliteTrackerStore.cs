using System.Globalization;
using Microsoft.Data.Sqlite;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Infrastructure;

public sealed class SqliteTrackerStore : ITrackerStore
{
    private const int SchemaVersion = 23;
    private static readonly TimeSpan MinimumEntryDuration = TimeSpan.FromMinutes(1);
    private readonly string _connectionString;
    private readonly TimeZoneInfo _monthlyLogTimeZone;
    private readonly SemaphoreSlim _monthlyLogSync = new(1, 1);
    private readonly HashSet<string> _managedMonthlyLogFiles = new(StringComparer.OrdinalIgnoreCase);

    public SqliteTrackerStore(
        string databasePath,
        string? monthlyLogDirectory = null,
        TimeZoneInfo? monthlyLogTimeZone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        MonthlyLogDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(monthlyLogDirectory)
                ? Path.GetDirectoryName(DatabasePath)!
                : monthlyLogDirectory);
        _monthlyLogTimeZone = monthlyLogTimeZone ?? TimeZoneInfo.Local;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
    }

    public string DatabasePath { get; }
    public string MonthlyLogDirectory { get; }
    public string DailyLogDirectory => Path.Combine(MonthlyLogDirectory, DailySafetyArchive.DailyLogsDirectoryName);
    public string DailyBackupDirectory => Path.Combine(MonthlyLogDirectory, DailySafetyArchive.DailyBackupsDirectoryName);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        Directory.CreateDirectory(MonthlyLogDirectory);

        var fileExisted = File.Exists(DatabasePath);
        await using (var connection = await OpenAsync(cancellationToken))
        {
            var version = await ScalarLongAsync(connection, "PRAGMA user_version;", cancellationToken);

            if (version > SchemaVersion)
            {
                throw new InvalidOperationException($"Database schema {version} is newer than this app supports ({SchemaVersion}).");
            }

            if (fileExisted && IsUpgradeRequired(version))
            {
                await connection.CloseAsync();
                File.Copy(DatabasePath, DatabasePath + $".backup-v{version}-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false);
                await connection.OpenAsync(cancellationToken);
            }

            await ExecuteAsync(connection, SchemaSql, cancellationToken);
            if (version == 1)
            {
                await ExecuteAsync(connection, MigrationV2Sql, cancellationToken);
            }

            if (version < 4 &&
                await ScalarLongAsync(
                    connection,
                    "SELECT COUNT(*) FROM pragma_table_info('Projects') WHERE name = 'DailyTargetHours';",
                    cancellationToken) == 0)
            {
                await ExecuteAsync(connection, MigrationV4Sql, cancellationToken);
            }

            if (version < 8 &&
                await ScalarLongAsync(
                    connection,
                    "SELECT COUNT(*) FROM pragma_table_info('Software') WHERE name = 'IsExcluded';",
                    cancellationToken) == 0)
            {
                await ExecuteAsync(connection, MigrationV8Sql, cancellationToken);
            }

            if (version < 10)
            {
                await ExecuteAsync(connection, MigrationV10Sql, cancellationToken);
            }

            if (version < 11 &&
                await ScalarLongAsync(
                    connection,
                    "SELECT COUNT(*) FROM pragma_table_info('Software') WHERE name = 'IsHidden';",
                    cancellationToken) == 0)
            {
                await ExecuteAsync(connection, MigrationV11Sql, cancellationToken);
            }

            if (version < 12 &&
                await ScalarLongAsync(
                    connection,
                    "SELECT COUNT(*) FROM pragma_table_info('Software') WHERE name = 'IsGlobal';",
                    cancellationToken) == 0)
            {
                await ExecuteAsync(connection, MigrationV12Sql, cancellationToken);
            }

            if (version < 13 &&
                await ScalarLongAsync(
                    connection,
                    "SELECT COUNT(*) FROM pragma_table_info('Tags') WHERE name = 'IsGlobal';",
                    cancellationToken) == 0)
            {
                await ExecuteAsync(connection, MigrationV13Sql, cancellationToken);
            }

            if (version < 14)
            {
                await ExecuteAsync(connection, MigrationV14Sql, cancellationToken);
            }

            if (version < 15 &&
                await ScalarLongAsync(
                    connection,
                    "SELECT COUNT(*) FROM pragma_table_info('Projects') WHERE name = 'CarryOverTargetDebtEnabled';",
                    cancellationToken) == 0)
            {
                await ExecuteAsync(connection, MigrationV15Sql, cancellationToken);
            }

            if (version < 16 &&
                await ScalarLongAsync(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'CustomTargets';",
                    cancellationToken) == 0)
            {
                await ExecuteAsync(connection, MigrationV16Sql, cancellationToken);
            }

            if (version < 20)
            {
                await ExecuteAsync(connection, MigrationV20Sql, cancellationToken);
            }

            if (version < 23 &&
                await ScalarLongAsync(
                    connection,
                    "SELECT COUNT(*) FROM pragma_table_info('CustomTargets') WHERE name = 'DurationMetric';",
                    cancellationToken) == 0)
            {
                await ExecuteAsync(connection, MigrationV23Sql, cancellationToken);
            }

            if (version < 17 &&
                await ScalarLongAsync(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ProjectTargetDebtCancellations';",
                    cancellationToken) == 0)
            {
                await ExecuteAsync(connection, MigrationV17Sql, cancellationToken);
            }

            if (version < 18)
            {
                await MigrateProjectTargetsToRecordsAsync(connection, cancellationToken);
            }

            if (version < 19)
            {
                await ExecuteAsync(connection, MigrationV19Sql, cancellationToken);
            }

            if (version < 21 &&
                await ScalarLongAsync(
                    connection,
                    "SELECT COUNT(*) FROM pragma_table_info('SavedTasks') WHERE name = 'Origin';",
                    cancellationToken) == 0)
            {
                await ExecuteAsync(connection, MigrationV21Sql, cancellationToken);
            }

            await ExecuteAsync(connection, SchemaV21IndexesSql, cancellationToken);

            await EnsureSystemEntitiesAsync(connection, cancellationToken);
            await DeleteSubMinuteCompletedEntriesAsync(connection, transaction: null, entryId: null, cancellationToken);
            await SynchronizeTagsFromDescriptionsAsync(connection, cancellationToken);
            await ExecuteAsync(connection, $"PRAGMA user_version = {SchemaVersion};", cancellationToken);
        }

        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task RecoverInterruptedTimerAsync(DateTimeOffset recoveredAtUtc, CancellationToken cancellationToken = default)
    {
        recoveredAtUtc = recoveredAtUtc.ToUniversalTime();
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        string? behaviorValue;
        string? markerValue;
        await using (var settingsCommand = connection.CreateCommand())
        {
            settingsCommand.Transaction = transaction;
            settingsCommand.CommandText =
                "SELECT Value FROM Settings WHERE Key = $key LIMIT 1;";
            settingsCommand.Parameters.AddWithValue("$key", SessionTrackingSettings.BehaviorKey);
            behaviorValue = (string?)await settingsCommand.ExecuteScalarAsync(cancellationToken);
            settingsCommand.Parameters["$key"].Value = SessionTrackingSettings.ResumeMarkerKey;
            markerValue = (string?)await settingsCommand.ExecuteScalarAsync(cancellationToken);
        }

        TimeEntry? running = null;
        await using (var runningCommand = connection.CreateCommand())
        {
            runningCommand.Transaction = transaction;
            runningCommand.CommandText =
                """
                SELECT Id, ProjectId, TaskId, Description, StartUtc, EndUtc, LastCheckpointUtc,
                       DetailsPending, Source, CreatedUtc, ModifiedUtc, IsPaid
                FROM TimeEntries
                WHERE EndUtc IS NULL
                LIMIT 1;
                """;
            await using var reader = await runningCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                running = MapTimeEntry(reader);
            }
        }

        var markedEntryId = Guid.Empty;
        var unavailableSinceUtc = default(DateTimeOffset);
        var resumeAfterSignIn =
            Enum.TryParse<SessionTrackingBehavior>(behaviorValue, ignoreCase: true, out var behavior) &&
            behavior == SessionTrackingBehavior.KeepRunningAndExclude &&
            SessionTrackingSettings.TryParseResumeMarker(
                markerValue,
                out markedEntryId,
                out unavailableSinceUtc) &&
            running?.Id == markedEntryId;
        if (resumeAfterSignIn && running is not null)
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "UPDATE TimeEntries SET LastCheckpointUtc = $recovered, ModifiedUtc = $recovered WHERE Id = $id AND EndUtc IS NULL;",
                cancellationToken,
                ("$recovered", Format(recoveredAtUtc)),
                ("$id", running.Id.ToString("D")));
            transaction.Commit();
            await connection.CloseAsync();
            await SynchronizeMonthlyLogFilesAsync(cancellationToken);
            return;
        }

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "UPDATE Settings SET Value = '' WHERE Key = $key;",
            cancellationToken,
            ("$key", SessionTrackingSettings.ResumeMarkerKey));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE TimeEntries
            SET EndUtc = CASE WHEN LastCheckpointUtc < $recovered THEN LastCheckpointUtc ELSE $recovered END,
                DetailsPending = CASE
                    WHEN ProjectId = $unassigned
                      OR (TaskId IS NULL AND TRIM(COALESCE(Description, '')) = '')
                    THEN 1
                    ELSE DetailsPending
                END,
                ModifiedUtc = $recovered
            WHERE EndUtc IS NULL;
            """,
            cancellationToken,
            ("$recovered", Format(recoveredAtUtc)),
            ("$unassigned", SystemEntityIds.UnassignedProjectId.ToString("D")));
        await DeleteSubMinuteCompletedEntriesAsync(connection, transaction, entryId: null, cancellationToken);
        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public Task<IReadOnlyList<Client>> GetClientsAsync(bool includeArchived = false, CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT Id, Name, Color, IsArchived
            FROM Clients
            WHERE Id <> $system
              AND ($include = 1 OR IsArchived = 0)
            ORDER BY Name COLLATE NOCASE;
            """,
            reader => new Client(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)),
            cancellationToken,
            ("$system", SystemEntityIds.UnassignedClientId.ToString("D")),
            ("$include", includeArchived ? 1 : 0));

    public Task<IReadOnlyList<Project>> GetProjectsAsync(bool includeArchived = false, CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT Id, ClientId, Name, Color, IsArchived, DailyTargetHours, WeeklyTargetHours,
                   MonthlyTargetHours, HourlyRate, Currency, CarryOverTargetDebtEnabled
            FROM Projects
            WHERE Id <> $system
              AND ($include = 1 OR IsArchived = 0)
            ORDER BY Name COLLATE NOCASE;
            """,
            MapProject,
            cancellationToken,
            ("$system", SystemEntityIds.UnassignedProjectId.ToString("D")),
            ("$include", includeArchived ? 1 : 0));

    public async Task<IReadOnlyList<ProjectTargetDebt>> GetProjectTargetDebtsAsync(
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var cancellationsByProject = (await GetProjectTargetDebtCancellationsAsync(
                includeRestored: false,
                cancellationToken: cancellationToken))
            .GroupBy(item => item.ProjectId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ProjectTargetDebtCancellation>)group.ToArray());
        var projects = (await GetProjectsAsync(cancellationToken: cancellationToken))
            .Where(project => project.CarryOverTargetDebtEnabled && project.MonthlyTargetHours is > 0)
            .ToArray();
        if (projects.Length == 0)
        {
            return [];
        }

        var firstStarts = (await QueryAsync(
                """
                SELECT ProjectId, MIN(StartUtc)
                FROM TimeEntries
                WHERE ProjectId <> $system
                GROUP BY ProjectId;
                """,
                reader => new KeyValuePair<Guid, DateTimeOffset>(
                    Guid.Parse(reader.GetString(0)),
                    Parse(reader.GetString(1))),
                cancellationToken,
                ("$system", SystemEntityIds.UnassignedProjectId.ToString("D"))))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        var debts = new List<ProjectTargetDebt>(projects.Length);
        foreach (var project in projects)
        {
            if (!firstStarts.TryGetValue(project.Id, out var firstStartUtc))
            {
                debts.Add(ProjectTargetDebt.None(project.Id) with
                {
                    Cancellations = cancellationsByProject.GetValueOrDefault(project.Id, []),
                });
                continue;
            }

            debts.Add(await CalculateProjectTargetDebtAsync(
                project,
                firstStartUtc,
                nowUtc,
                timeZone,
                cancellationsByProject.GetValueOrDefault(project.Id, []),
                cancellationToken));
        }

        return debts;
    }

    public Task<IReadOnlyList<ProjectTargetDebtCancellation>> GetProjectTargetDebtCancellationsAsync(
        Guid? projectId = null,
        bool includeRestored = false,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT Id, ProjectId, CanceledSeconds, CanceledUtc, RestoredUtc
            FROM ProjectTargetDebtCancellations
            WHERE ($project IS NULL OR ProjectId = $project)
              AND ($includeRestored = 1 OR RestoredUtc IS NULL)
            ORDER BY CanceledUtc, Id;
            """,
            reader => new ProjectTargetDebtCancellation(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt64(2),
                Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : Parse(reader.GetString(4))),
            cancellationToken,
            ("$project", projectId?.ToString("D")),
            ("$includeRestored", includeRestored ? 1 : 0));

    public async Task<ProjectTargetDebtCancellation> CancelProjectTargetDebtAsync(
        Guid projectId,
        long canceledSeconds,
        DateTimeOffset canceledUtc,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty || projectId == SystemEntityIds.UnassignedProjectId)
        {
            throw new ArgumentOutOfRangeException(nameof(projectId));
        }

        if (canceledSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canceledSeconds), "Canceled debt must be greater than zero.");
        }

        var cancellation = new ProjectTargetDebtCancellation(
            Guid.NewGuid(),
            projectId,
            canceledSeconds,
            canceledUtc.ToUniversalTime());
        await ExecuteParameterizedAsync(
            """
            INSERT INTO ProjectTargetDebtCancellations (Id, ProjectId, CanceledSeconds, CanceledUtc, RestoredUtc)
            VALUES ($id, $project, $seconds, $canceled, NULL);
            """,
            cancellationToken,
            ("$id", cancellation.Id.ToString("D")),
            ("$project", cancellation.ProjectId.ToString("D")),
            ("$seconds", cancellation.CanceledSeconds),
            ("$canceled", Format(cancellation.CanceledUtc)));
        return cancellation;
    }

    public Task RestoreProjectTargetDebtAsync(
        Guid projectId,
        DateTimeOffset restoredUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteParameterizedAsync(
            """
            UPDATE ProjectTargetDebtCancellations
            SET RestoredUtc = $restored
            WHERE ProjectId = $project AND RestoredUtc IS NULL;
            """,
            cancellationToken,
            ("$restored", Format(restoredUtc)),
            ("$project", projectId.ToString("D")));

    private async Task<ProjectTargetDebt> CalculateProjectTargetDebtAsync(
        Project project,
        DateTimeOffset firstStartUtc,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        IReadOnlyList<ProjectTargetDebtCancellation> cancellations,
        CancellationToken cancellationToken)
    {
        var firstLocalDate = TimeZoneInfo.ConvertTime(firstStartUtc, timeZone).Date;
        var nowLocalDate = TimeZoneInfo.ConvertTime(nowUtc, timeZone).Date;
        var adjustments = new List<TargetDebtAdjustment>();
        var basis = ProjectTargetDebtCalculator.GetRepaymentBasis(project);
        var projectTargets = (await GetCustomTargetsAsync(cancellationToken))
            .Where(target => target.ProjectId == project.Id)
            .ToArray();
        var monthlyUsesShortIdle = CadenceUsesShortIdle(
            projectTargets,
            CustomTargetCadence.Monthly);
        var repaymentUsesShortIdle = CadenceUsesShortIdle(
            projectTargets,
            basis switch
            {
                TargetDebtRepaymentBasis.Daily => CustomTargetCadence.Daily,
                TargetDebtRepaymentBasis.Weekly => CustomTargetCadence.Weekly,
                _ => CustomTargetCadence.Monthly,
            });

        for (var monthDate = new DateTime(firstLocalDate.Year, firstLocalDate.Month, 1);
             monthDate <= nowLocalDate;
             monthDate = monthDate.AddMonths(1))
        {
            var month = TrackingPeriodCalculator.MonthContaining(monthDate, timeZone);
            var effectiveEndUtc = month.EndUtc < nowUtc ? month.EndUtc : nowUtc;
            if (effectiveEndUtc <= month.StartUtc)
            {
                continue;
            }

            var monthlySeconds = await GetProjectTargetSecondsAsync(
                project.Id,
                new TrackingPeriod(month.StartUtc, effectiveEndUtc),
                monthlyUsesShortIdle,
                cancellationToken);
            if (basis == TargetDebtRepaymentBasis.Monthly)
            {
                var repayment = ProjectTargetDebtCalculator.GetRepaymentCapacitySeconds(
                    project,
                    [],
                    [],
                    monthlySeconds);
                adjustments.Add(new TargetDebtAdjustment(effectiveEndUtc, 0, repayment));
            }

            if (month.EndUtc <= nowUtc)
            {
                adjustments.Add(new TargetDebtAdjustment(
                    month.EndUtc,
                    ProjectTargetDebtCalculator.MonthlyShortfallSeconds(project, monthlySeconds),
                    0));
            }
        }

        if (basis == TargetDebtRepaymentBasis.Daily)
        {
            for (var date = firstLocalDate; date <= nowLocalDate; date = date.AddDays(1))
            {
                var day = TrackingPeriodCalculator.DayContaining(date, timeZone);
                var effectiveEndUtc = day.EndUtc < nowUtc ? day.EndUtc : nowUtc;
                if (effectiveEndUtc <= day.StartUtc)
                {
                    continue;
                }

                var seconds = await GetProjectTargetSecondsAsync(
                    project.Id,
                    new TrackingPeriod(day.StartUtc, effectiveEndUtc),
                    repaymentUsesShortIdle,
                    cancellationToken);
                var repayment = ProjectTargetDebtCalculator.GetRepaymentCapacitySeconds(
                    project,
                    [seconds],
                    [],
                    0);
                adjustments.Add(new TargetDebtAdjustment(effectiveEndUtc, 0, repayment));
            }
        }
        else if (basis == TargetDebtRepaymentBasis.Weekly)
        {
            var firstWeekStart = firstLocalDate.AddDays(
                -((7 + (int)firstLocalDate.DayOfWeek - (int)DayOfWeek.Monday) % 7));
            var currentWeekStart = nowLocalDate.AddDays(
                -((7 + (int)nowLocalDate.DayOfWeek - (int)DayOfWeek.Monday) % 7));
            for (var weekDate = firstWeekStart; weekDate <= currentWeekStart; weekDate = weekDate.AddDays(7))
            {
                var week = TrackingPeriodCalculator.WeekContaining(weekDate, timeZone);
                var effectiveEndUtc = week.EndUtc < nowUtc ? week.EndUtc : nowUtc;
                if (effectiveEndUtc <= week.StartUtc)
                {
                    continue;
                }

                var seconds = await GetProjectTargetSecondsAsync(
                    project.Id,
                    new TrackingPeriod(week.StartUtc, effectiveEndUtc),
                    repaymentUsesShortIdle,
                    cancellationToken);
                var repayment = ProjectTargetDebtCalculator.GetRepaymentCapacitySeconds(
                    project,
                    [],
                    [seconds],
                    0);
                adjustments.Add(new TargetDebtAdjustment(effectiveEndUtc, 0, repayment));
            }
        }

        adjustments.AddRange(cancellations.Select(item => new TargetDebtAdjustment(
            item.CanceledUtc,
            DebtAddedSeconds: 0,
            RepaymentCapacitySeconds: 0,
            DebtCanceledSeconds: item.CanceledSeconds)));
        return ProjectTargetDebtCalculator.Calculate(project, adjustments) with
        {
            Cancellations = cancellations,
        };
    }

    private async Task<long> GetProjectTargetSecondsAsync(
        Guid projectId,
        TrackingPeriod period,
        bool useShortIdle,
        CancellationToken cancellationToken) =>
        (await GetReportAsync(
                period.StartUtc,
                period.EndUtc,
                new ReportFilter(ProjectId: projectId),
                cancellationToken))
            .Sum(row => useShortIdle
                ? row.DurationWithShortIdleSeconds
                : row.DurationSeconds);

    private static bool CadenceUsesShortIdle(
        IReadOnlyCollection<CustomTarget> targets,
        CustomTargetCadence cadence)
    {
        var matching = targets.Where(target => target.Cadence == cadence).ToArray();
        return matching.Length > 0 && matching.All(target =>
            target.DurationMetric == TargetDurationMetric.IncludingShortIdle);
    }

    public Task<IReadOnlyList<ProjectWorkSummary>> GetProjectWorkSummariesAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT p.Id,
                   CAST(COALESCE(SUM(
                       MAX(0,
                           CAST(strftime('%s', COALESCE(e.EndUtc, $now)) AS INTEGER)
                           - CAST(strftime('%s', e.StartUtc) AS INTEGER)
                           - COALESCE((
                               SELECT SUM(
                                   CAST(strftime('%s', x.EndUtc) AS INTEGER)
                                   - CAST(strftime('%s', x.StartUtc) AS INTEGER))
                               FROM TimeExclusions x
                               WHERE x.TimeEntryId = e.Id
                           ), 0)
                       )
                   ), 0) AS INTEGER),
                   MIN(e.StartUtc),
                   MAX(COALESCE(e.EndUtc, $now))
            FROM Projects p
            LEFT JOIN TimeEntries e ON e.ProjectId = p.Id
            WHERE p.IsArchived = 0
              AND p.Id <> $system
            GROUP BY p.Id;
            """,
            reader => new ProjectWorkSummary(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : Parse(reader.GetString(3))),
            cancellationToken,
            ("$now", Format(nowUtc)),
            ("$system", SystemEntityIds.UnassignedProjectId.ToString("D")));

    public Task<IReadOnlyList<SavedTask>> GetTasksAsync(Guid? projectId = null, bool includeArchived = false, CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT t.Id, t.ProjectId, t.Name, t.IsArchived, t.Origin, x.WebUrl
            FROM SavedTasks t
            LEFT JOIN ExternalTaskLinks x ON x.TaskId = t.Id AND x.Provider = 'Trello'
            WHERE ($project IS NULL OR t.ProjectId = $project)
              AND ($include = 1 OR t.IsArchived = 0)
              AND ($project = $system OR t.ProjectId <> $system)
            ORDER BY t.Name COLLATE NOCASE;
            """,
            MapSavedTask,
            cancellationToken,
            ("$project", projectId?.ToString("D")),
            ("$system", SystemEntityIds.UnassignedProjectId.ToString("D")),
            ("$include", includeArchived ? 1 : 0));

    public Task<IReadOnlyList<TaskWorkSummary>> GetTaskWorkSummariesAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT t.Id,
                   CAST(COALESCE(SUM(
                       MAX(0,
                           CAST(strftime('%s', COALESCE(e.EndUtc, $now)) AS INTEGER)
                           - CAST(strftime('%s', e.StartUtc) AS INTEGER)
                           - COALESCE((
                               SELECT SUM(
                                   CAST(strftime('%s', x.EndUtc) AS INTEGER)
                                   - CAST(strftime('%s', x.StartUtc) AS INTEGER))
                               FROM TimeExclusions x
                               WHERE x.TimeEntryId = e.Id
                           ), 0)
                       )
                   ), 0) AS INTEGER)
            FROM SavedTasks t
            LEFT JOIN TimeEntries e ON e.TaskId = t.Id
            WHERE t.IsArchived = 0
              AND t.ProjectId <> $system
            GROUP BY t.Id;
            """,
            reader => new TaskWorkSummary(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1)),
            cancellationToken,
            ("$now", Format(nowUtc)),
            ("$system", SystemEntityIds.UnassignedProjectId.ToString("D")));

    public Task<IReadOnlyList<TagDefinition>> GetTagsAsync(
        Guid? projectId = null,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT t.Id, t.Name, t.Color, t.IsGlobal,
                   GROUP_CONCAT(pt.ProjectId)
            FROM Tags t
            LEFT JOIN ProjectTags pt ON pt.TagId = t.Id
            WHERE $project IS NULL
               OR t.IsGlobal = 1
               OR EXISTS (
                   SELECT 1 FROM ProjectTags available
                   WHERE available.TagId = t.Id
                     AND available.ProjectId = $project
               )
            GROUP BY t.Id, t.Name, t.Color, t.IsGlobal
            ORDER BY t.Name COLLATE NOCASE;
            """,
            MapTagDefinition,
            cancellationToken,
            ("$project", NormalizeTagProjectId(projectId)?.ToString("D")));

    public Task<IReadOnlyList<TagSummary>> GetTagSummariesAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT t.Id, t.Name, t.Color, t.IsGlobal,
                   GROUP_CONCAT(pt.ProjectId),
                   (SELECT COUNT(*) FROM TimeEntries e WHERE has_tag(e.Description, t.Name))
            FROM Tags t
            LEFT JOIN ProjectTags pt ON pt.TagId = t.Id
            GROUP BY t.Id, t.Name, t.Color, t.IsGlobal
            ORDER BY t.Name COLLATE NOCASE;
            """,
            reader => new TagSummary(
                MapTagDefinition(reader),
                reader.GetInt32(5)),
            cancellationToken);

    public async Task<IReadOnlyList<SoftwareDefinition>> GetSoftwareAsync(CancellationToken cancellationToken = default)
        => await QueryAsync(
            """
            SELECT s.Id, s.ProcessName, s.Label, COUNT(es.TimeEntryId)
            FROM Software s
            LEFT JOIN TimeEntrySoftware es ON es.SoftwareId = s.Id
            WHERE s.IsHidden = 0
            GROUP BY s.Id, s.ProcessName, s.Label
            ORDER BY s.Label COLLATE NOCASE, s.ProcessName COLLATE NOCASE;
            """,
            reader => new SoftwareDefinition(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)),
            cancellationToken);

    public async Task<IReadOnlyList<ProjectSoftwareDefinition>> GetProjectSoftwareAsync(
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync(
            """
            SELECT $global AS ScopeId, 'All projects' AS ScopeName, 'Global' AS ClientName,
                   s.Id, s.ProcessName, s.Label, COUNT(es.TimeEntryId),
                   s.IsExcluded, 1 AS IsGlobal
            FROM Software s
            LEFT JOIN TimeEntrySoftware es ON es.SoftwareId = s.Id
            WHERE s.IsHidden = 0
              AND s.IsGlobal = 1
            GROUP BY s.Id, s.ProcessName, s.Label, s.IsExcluded

            UNION ALL

            SELECT p.Id, p.Name, c.Name,
                   s.Id, s.ProcessName, s.Label, COUNT(es.TimeEntryId),
                   ps.IsExcluded, 0
            FROM ProjectSoftwareSettings ps
            JOIN Projects p ON p.Id = ps.ProjectId
            JOIN Clients c ON c.Id = p.ClientId
            JOIN Software s ON s.Id = ps.SoftwareId
            LEFT JOIN TimeEntries e ON e.ProjectId = p.Id
            LEFT JOIN TimeEntrySoftware es
                   ON es.TimeEntryId = e.Id AND es.SoftwareId = s.Id
            WHERE p.IsArchived = 0
              AND c.IsArchived = 0
              AND s.IsHidden = 0
              AND s.IsGlobal = 0
              AND p.Id <> $unassigned
              AND ($project IS NULL OR p.Id = $project)
            GROUP BY p.Id, p.Name, c.Name,
                     s.Id, s.ProcessName, s.Label, ps.IsExcluded
            ORDER BY 2 COLLATE NOCASE, 3 COLLATE NOCASE,
                     6 COLLATE NOCASE, 5 COLLATE NOCASE;
            """,
            reader => new ProjectSoftwareDefinition(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                new SoftwareDefinition(
                    Guid.Parse(reader.GetString(3)),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6)),
                [],
                reader.GetBoolean(7),
                reader.GetBoolean(8)),
            cancellationToken,
            ("$project", projectId?.ToString("D")),
            ("$global", SystemEntityIds.GlobalSoftwareScopeId.ToString("D")),
            ("$unassigned", SystemEntityIds.UnassignedProjectId.ToString("D")));
        var assignments = await QueryAsync(
            """
            SELECT $global AS ScopeId, st.SoftwareId, t.Id, t.Name, t.Color
            FROM SoftwareTags st
            JOIN Software s ON s.Id = st.SoftwareId
            JOIN Tags t ON t.Id = st.TagId
            WHERE s.IsGlobal = 1

            UNION ALL

            SELECT st.ProjectId, st.SoftwareId, t.Id, t.Name, t.Color
            FROM ProjectSoftwareTags st
            JOIN Software s ON s.Id = st.SoftwareId
            JOIN Tags t ON t.Id = st.TagId
            WHERE s.IsGlobal = 0
              AND ($project IS NULL OR st.ProjectId = $project)
            ORDER BY 4 COLLATE NOCASE;
            """,
            reader => (
                ProjectId: Guid.Parse(reader.GetString(0)),
                SoftwareId: Guid.Parse(reader.GetString(1)),
                Tag: new TagDefinition(
                    Guid.Parse(reader.GetString(2)),
                    reader.GetString(3),
                    reader.GetString(4))),
            cancellationToken,
            ("$project", projectId?.ToString("D")),
            ("$global", SystemEntityIds.GlobalSoftwareScopeId.ToString("D")));
        var tagsBySetting = assignments
            .GroupBy(assignment => (assignment.ProjectId, assignment.SoftwareId))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TagDefinition>)group.Select(assignment => assignment.Tag).ToArray());
        return rows
            .Select(item => item with
            {
                Tags = tagsBySetting.GetValueOrDefault((item.ProjectId, item.Software.Id), []),
            })
            .ToArray();
    }

    public Task<IReadOnlyList<TagDefinition>> GetSoftwareTagsByProcessAsync(
        Guid projectId,
        string processName,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT t.Id, t.Name, t.Color
            FROM Software s
            JOIN SoftwareTags st ON st.SoftwareId = s.Id
            JOIN Tags t ON t.Id = st.TagId
            WHERE s.IsGlobal = 1
              AND s.IsHidden = 0
              AND t.IsGlobal = 1
              AND s.ProcessName = $process

            UNION

            SELECT t.Id, t.Name, t.Color
            FROM Software s
            JOIN ProjectSoftwareTags st ON st.SoftwareId = s.Id
            JOIN Tags t ON t.Id = st.TagId
            WHERE s.IsGlobal = 0
              AND st.ProjectId = $project
              AND s.IsHidden = 0
              AND (
                  t.IsGlobal = 1 OR EXISTS (
                      SELECT 1 FROM ProjectTags pt
                      WHERE pt.TagId = t.Id
                        AND pt.ProjectId = $project
                  )
              )
              AND s.ProcessName = $process
            ORDER BY 2 COLLATE NOCASE;
            """,
            reader => new TagDefinition(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
            reader.GetString(2)),
            cancellationToken,
            ("$project", projectId.ToString("D")),
            ("$process", NormalizeProcessName(processName)));

    public Task<IReadOnlyList<RecognitionRule>> GetRulesAsync(Guid? projectId = null, CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT r.Id, r.ProjectId, r.TitlePattern, r.ProcessName, r.IsEnabled
            FROM RecognitionRules r
            JOIN Projects p ON p.Id = r.ProjectId
            JOIN Clients c ON c.Id = p.ClientId
            WHERE ($project IS NULL OR r.ProjectId = $project)
              AND p.IsArchived = 0
              AND c.IsArchived = 0
            ORDER BY r.TitlePattern COLLATE NOCASE;
            """,
            reader => new RecognitionRule(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4)),
            cancellationToken,
            ("$project", projectId?.ToString("D")));

    public Task<IReadOnlyList<RecognitionCandidate>> GetRecognitionCandidatesAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT p.Id, p.ClientId, p.Name, p.Color, p.IsArchived,
                   c.Id, c.Name, c.Color, c.IsArchived,
                   r.Id, r.ProjectId, r.TitlePattern, r.ProcessName, r.IsEnabled
            FROM RecognitionRules r
            JOIN Projects p ON p.Id = r.ProjectId
            JOIN Clients c ON c.Id = p.ClientId
            WHERE r.IsEnabled = 1 AND p.IsArchived = 0 AND c.IsArchived = 0;
            """,
            reader => new RecognitionCandidate(
                new Project(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4)),
                new Client(Guid.Parse(reader.GetString(5)), reader.GetString(6), reader.GetString(7), reader.GetBoolean(8)),
                new RecognitionRule(Guid.Parse(reader.GetString(9)), Guid.Parse(reader.GetString(10)), reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetBoolean(13))),
            cancellationToken);

    public Task<IReadOnlyList<ProjectOption>> GetProjectOptionsAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT p.Id, c.Id, c.Name, p.Name, p.Color
            FROM Projects p JOIN Clients c ON c.Id = p.ClientId
            WHERE p.IsArchived = 0
              AND c.IsArchived = 0
              AND p.Id <> $system
            ORDER BY c.Name COLLATE NOCASE, p.Name COLLATE NOCASE;
            """,
            reader => new ProjectOption(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4)),
            cancellationToken,
            ("$system", SystemEntityIds.UnassignedProjectId.ToString("D")));

    public Task<IReadOnlyList<CustomTarget>> GetCustomTargetsAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT t.Id, t.Name, t.ProjectId, t.Cadence, t.TargetHours,
                   t.CreatedUtc, t.ModifiedUtc, t.CompletedUtc, t.DurationMetric
            FROM CustomTargets t
            LEFT JOIN Projects p ON p.Id = t.ProjectId
            LEFT JOIN Clients c ON c.Id = p.ClientId
            WHERE t.ProjectId IS NULL
               OR (p.IsArchived = 0 AND c.IsArchived = 0)
            ORDER BY t.Cadence, t.Name COLLATE NOCASE;
            """,
            reader => new CustomTarget(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                (CustomTargetCadence)reader.GetInt32(3),
                reader.GetDouble(4),
                Parse(reader.GetString(5)),
                Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : Parse(reader.GetString(7)),
                (TargetDurationMetric)reader.GetInt32(8)),
            cancellationToken);

    public async Task<TrelloConnection?> GetTrelloConnectionAsync(CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync(
            """
            SELECT MemberId, Username, DisplayName, LastSuccessfulSyncUtc, LastError, RequiresReconnect
            FROM TrelloConnections WHERE SingletonId = 1 LIMIT 1;
            """,
            reader => new TrelloConnection(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetBoolean(5)),
            cancellationToken);
        return rows.FirstOrDefault();
    }

    public async Task<IReadOnlyList<TrelloBoardMapping>> GetTrelloBoardMappingsAsync(
        CancellationToken cancellationToken = default)
    {
        var mappings = await QueryAsync(
            """
            SELECT Id, ProjectId, BoardId, BoardName
            FROM TrelloBoardMappings
            ORDER BY BoardName COLLATE NOCASE;
            """,
            reader => (
                Id: Guid.Parse(reader.GetString(0)),
                ProjectId: Guid.Parse(reader.GetString(1)),
                BoardId: reader.GetString(2),
                BoardName: reader.GetString(3)),
            cancellationToken);
        var lists = await QueryAsync(
            """
            SELECT MappingId, ListId, ListName
            FROM TrelloMappingLists
            ORDER BY ListName COLLATE NOCASE;
            """,
            reader => (
                MappingId: Guid.Parse(reader.GetString(0)),
                List: new TrelloListMapping(reader.GetString(1), reader.GetString(2))),
            cancellationToken);
        var listsByMapping = lists
            .GroupBy(item => item.MappingId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TrelloListMapping>)group.Select(item => item.List).ToArray());
        return mappings
            .Select(mapping => new TrelloBoardMapping(
                mapping.Id,
                mapping.ProjectId,
                mapping.BoardId,
                mapping.BoardName,
                listsByMapping.GetValueOrDefault(mapping.Id, [])))
            .ToArray();
    }

    public Task<IReadOnlyList<ExternalTaskLink>> GetExternalTaskLinksAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT TaskId, Provider, ExternalId, BoardId, ListId, WebUrl, State, RemoteModifiedUtc
            FROM ExternalTaskLinks
            ORDER BY BoardId, ExternalId;
            """,
            reader => new ExternalTaskLink(
                reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                (ExternalTaskLinkState)reader.GetInt32(6),
                reader.IsDBNull(7) ? null : Parse(reader.GetString(7))),
            cancellationToken);

    public Task SaveTrelloConnectionAsync(
        TrelloConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return ExecuteParameterizedAsync(
            """
            INSERT INTO TrelloConnections
                (SingletonId, MemberId, Username, DisplayName, LastSuccessfulSyncUtc, LastError, RequiresReconnect)
            VALUES (1, $member, $username, $display, $lastSync, $error, $reconnect)
            ON CONFLICT(SingletonId) DO UPDATE SET
                MemberId = excluded.MemberId,
                Username = excluded.Username,
                DisplayName = excluded.DisplayName,
                LastSuccessfulSyncUtc = excluded.LastSuccessfulSyncUtc,
                LastError = excluded.LastError,
                RequiresReconnect = excluded.RequiresReconnect;
            """,
            cancellationToken,
            ("$member", Required(connection.MemberId, nameof(connection.MemberId))),
            ("$username", Required(connection.Username, nameof(connection.Username))),
            ("$display", Required(connection.DisplayName, nameof(connection.DisplayName))),
            ("$lastSync", connection.LastSuccessfulSyncUtc is { } lastSync ? Format(lastSync) : null),
            ("$error", connection.LastError),
            ("$reconnect", connection.RequiresReconnect ? 1 : 0));
    }

    public Task UpdateTrelloSyncStatusAsync(
        DateTimeOffset? successfulUtc,
        string? error,
        bool requiresReconnect,
        CancellationToken cancellationToken = default) =>
        ExecuteParameterizedAsync(
            """
            UPDATE TrelloConnections
            SET LastSuccessfulSyncUtc = COALESCE($success, LastSuccessfulSyncUtc),
                LastError = $error,
                RequiresReconnect = $reconnect
            WHERE SingletonId = 1;
            """,
            cancellationToken,
            ("$success", successfulUtc is { } value ? Format(value) : null),
            ("$error", string.IsNullOrWhiteSpace(error) ? null : error.Trim()),
            ("$reconnect", requiresReconnect ? 1 : 0));

    public async Task ClearTrelloConnectionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await DetachLinkedTasksAsync(connection, transaction, mappingId: null, cancellationToken);
        await ExecuteInTransactionAsync(connection, transaction, "DELETE FROM TrelloBoardMappings;", cancellationToken);
        await ExecuteInTransactionAsync(connection, transaction, "DELETE FROM TrelloConnections;", cancellationToken);
        transaction.Commit();
    }

    public async Task UpsertTrelloBoardMappingAsync(
        TrelloBoardMapping mapping,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.Id == Guid.Empty || mapping.ProjectId == Guid.Empty || mapping.Lists.Count == 0)
        {
            throw new ArgumentException("Choose a project, board, and at least one list.", nameof(mapping));
        }

        var distinctLists = mapping.Lists
            .Where(list => !string.IsNullOrWhiteSpace(list.ListId) && !string.IsNullOrWhiteSpace(list.ListName))
            .DistinctBy(list => list.ListId, StringComparer.Ordinal)
            .ToArray();
        if (distinctLists.Length == 0)
        {
            throw new ArgumentException("Choose at least one Trello list.", nameof(mapping));
        }

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        string? previousBoardId;
        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction;
            existingCommand.CommandText = "SELECT BoardId FROM TrelloBoardMappings WHERE Id = $id LIMIT 1;";
            existingCommand.Parameters.AddWithValue("$id", mapping.Id.ToString("D"));
            previousBoardId = (string?)await existingCommand.ExecuteScalarAsync(cancellationToken);
        }
        if (previousBoardId is not null &&
            !string.Equals(previousBoardId, mapping.BoardId, StringComparison.Ordinal))
        {
            await DetachLinkedTasksAsync(connection, transaction, mapping.Id, cancellationToken);
        }

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO TrelloBoardMappings (Id, ProjectId, BoardId, BoardName)
            VALUES ($id, $project, $board, $name)
            ON CONFLICT(Id) DO UPDATE SET
                ProjectId = excluded.ProjectId,
                BoardId = excluded.BoardId,
                BoardName = excluded.BoardName;
            """,
            cancellationToken,
            ("$id", mapping.Id.ToString("D")),
            ("$project", mapping.ProjectId.ToString("D")),
            ("$board", Required(mapping.BoardId, nameof(mapping.BoardId))),
            ("$name", Required(mapping.BoardName, nameof(mapping.BoardName))));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM TrelloMappingLists WHERE MappingId = $id;",
            cancellationToken,
            ("$id", mapping.Id.ToString("D")));
        foreach (var list in distinctLists)
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "INSERT INTO TrelloMappingLists (MappingId, ListId, ListName) VALUES ($mapping, $list, $name);",
                cancellationToken,
                ("$mapping", mapping.Id.ToString("D")),
                ("$list", list.ListId.Trim()),
                ("$name", list.ListName.Trim()));
        }

        transaction.Commit();
    }

    public async Task RemoveTrelloBoardMappingAsync(Guid mappingId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await DetachLinkedTasksAsync(connection, transaction, mappingId, cancellationToken);
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM TrelloBoardMappings WHERE Id = $id;",
            cancellationToken,
            ("$id", mappingId.ToString("D")));
        transaction.Commit();
    }

    public async Task<TrelloSyncResult> ReconcileTrelloBoardAsync(
        Guid mappingId,
        IReadOnlyList<TrelloCard> cards,
        DateTimeOffset completedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cards);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        Guid projectId;
        string boardId;
        await using (var mappingCommand = connection.CreateCommand())
        {
            mappingCommand.Transaction = transaction;
            mappingCommand.CommandText = "SELECT ProjectId, BoardId FROM TrelloBoardMappings WHERE Id = $id LIMIT 1;";
            mappingCommand.Parameters.AddWithValue("$id", mappingId.ToString("D"));
            await using var reader = await mappingCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new KeyNotFoundException("The Trello mapping no longer exists.");
            }

            projectId = Guid.Parse(reader.GetString(0));
            boardId = reader.GetString(1);
        }

        var storedLinks = new List<StoredExternalLink>();
        await using (var linksCommand = connection.CreateCommand())
        {
            linksCommand.Transaction = transaction;
            linksCommand.CommandText =
                """
                SELECT x.TaskId, x.ExternalId, x.BoardId, x.ListId, x.MappingId, x.State,
                       CASE WHEN x.TaskId IS NULL THEN 0 ELSE
                           (SELECT COUNT(*) FROM TimeEntries e WHERE e.TaskId = x.TaskId) END
                FROM ExternalTaskLinks x
                WHERE x.Provider = 'Trello';
                """;
            await using var reader = await linksCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                storedLinks.Add(new StoredExternalLink(
                    reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                    (ExternalTaskLinkState)reader.GetInt32(5),
                    reader.GetInt64(6)));
            }
        }

        var byCard = storedLinks.ToDictionary(link => link.CardId, StringComparer.Ordinal);
        var receivedCardIds = new HashSet<string>(StringComparer.Ordinal);
        var imported = 0;
        var updated = 0;
        var detached = 0;
        var deleted = 0;
        foreach (var card in cards
                     .Where(card => string.Equals(card.BoardId, boardId, StringComparison.Ordinal))
                     .DistinctBy(card => card.Id, StringComparer.Ordinal))
        {
            receivedCardIds.Add(card.Id);
            if (byCard.TryGetValue(card.Id, out var stored))
            {
                if (stored.State == ExternalTaskLinkState.Suppressed)
                {
                    continue;
                }

                var taskId = stored.TaskId ?? Guid.NewGuid();
                if (stored.TaskId is null)
                {
                    await InsertTrelloTaskAsync(connection, transaction, taskId, projectId, card.Name, cancellationToken);
                    imported++;
                }
                else
                {
                    await ExecuteInTransactionAsync(
                        connection,
                        transaction,
                        "UPDATE SavedTasks SET ProjectId = $project, Name = $name, IsArchived = 0, Origin = 1 WHERE Id = $id;",
                        cancellationToken,
                        ("$project", projectId.ToString("D")),
                        ("$name", Required(card.Name, nameof(card.Name))),
                        ("$id", taskId.ToString("D")));
                    updated++;
                }

                await UpdateExternalLinkAsync(connection, transaction, mappingId, taskId, card, ExternalTaskLinkState.Linked, cancellationToken);
                continue;
            }

            var createdTaskId = Guid.NewGuid();
            await InsertTrelloTaskAsync(connection, transaction, createdTaskId, projectId, card.Name, cancellationToken);
            await InsertExternalLinkAsync(connection, transaction, mappingId, createdTaskId, card, cancellationToken);
            imported++;
        }

        foreach (var stored in storedLinks.Where(link =>
                     link.State == ExternalTaskLinkState.Linked &&
                     link.MappingId == mappingId &&
                     !receivedCardIds.Contains(link.CardId)))
        {
            if (stored.TaskId is not { } taskId)
            {
                continue;
            }

            if (stored.EntryCount == 0)
            {
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    "DELETE FROM SavedTasks WHERE Id = $id;",
                    cancellationToken,
                    ("$id", taskId.ToString("D")));
                deleted++;
            }
            else
            {
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    "UPDATE SavedTasks SET Origin = 2, IsArchived = 0 WHERE Id = $id;",
                    cancellationToken,
                    ("$id", taskId.ToString("D")));
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    "UPDATE ExternalTaskLinks SET State = 1, MappingId = NULL WHERE Provider = 'Trello' AND ExternalId = $card;",
                    cancellationToken,
                    ("$card", stored.CardId));
                detached++;
            }
        }

        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
        return new TrelloSyncResult(1, imported, updated, detached, deleted, completedUtc.ToUniversalTime());
    }

    public async Task SuppressExternalTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "UPDATE ExternalTaskLinks SET State = 2 WHERE TaskId = $id AND Provider = 'Trello';",
            cancellationToken,
            ("$id", taskId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "UPDATE SavedTasks SET IsArchived = 1, Origin = CASE WHEN Origin = 1 THEN 2 ELSE Origin END WHERE Id = $id;",
            cancellationToken,
            ("$id", taskId.ToString("D")));
        transaction.Commit();
    }

    public async Task<Client> AddClientAsync(string name, string color, CancellationToken cancellationToken = default)
    {
        name = Required(name, nameof(name));
        color = NormalizeColor(color);
        var client = new Client(Guid.NewGuid(), name, color);
        await ExecuteParameterizedAsync(
            "INSERT INTO Clients (Id, Name, Color, IsArchived) VALUES ($id, $name, $color, 0);",
            cancellationToken,
            ("$id", client.Id.ToString("D")), ("$name", client.Name), ("$color", client.Color));
        return client;
    }

    public async Task<Project> AddProjectAsync(Guid clientId, string name, string color, CancellationToken cancellationToken = default)
    {
        name = Required(name, nameof(name));
        color = NormalizeColor(color);
        var project = new Project(Guid.NewGuid(), clientId, name, color);
        var rule = new RecognitionRule(Guid.NewGuid(), project.Id, name, null);

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO Projects (Id, ClientId, Name, Color, IsArchived) VALUES ($id, $client, $name, $color, 0);";
            AddParameters(command, ("$id", project.Id.ToString("D")), ("$client", clientId.ToString("D")), ("$name", name), ("$color", color));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO RecognitionRules (Id, ProjectId, TitlePattern, ProcessName, IsEnabled) VALUES ($id, $project, $pattern, NULL, 1);";
            AddParameters(command, ("$id", rule.Id.ToString("D")), ("$project", project.Id.ToString("D")), ("$pattern", rule.TitlePattern));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
        return project;
    }

    public async Task<SavedTask> AddTaskAsync(Guid projectId, string name, CancellationToken cancellationToken = default)
    {
        name = Required(name, nameof(name));
        var task = new SavedTask(Guid.NewGuid(), projectId, name);
        await ExecuteParameterizedAsync(
            "INSERT INTO SavedTasks (Id, ProjectId, Name, IsArchived, Origin) VALUES ($id, $project, $name, 0, 0);",
            cancellationToken,
            ("$id", task.Id.ToString("D")), ("$project", projectId.ToString("D")), ("$name", name));
        return task;
    }

    public async Task<SavedTask> GetOrAddTaskAsync(
        Guid projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        name = Required(name, nameof(name));
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        SavedTask? existing = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT Id, ProjectId, Name, IsArchived FROM SavedTasks WHERE ProjectId = $project AND Origin = 0 AND Name = $name COLLATE NOCASE LIMIT 1;";
            AddParameters(command, ("$project", projectId.ToString("D")), ("$name", name));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existing = new SavedTask(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetBoolean(3));
            }
        }

        if (existing is not null)
        {
            if (existing.IsArchived)
            {
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    "UPDATE SavedTasks SET IsArchived = 0 WHERE Id = $id;",
                    cancellationToken,
                    ("$id", existing.Id.ToString("D")));
                existing = existing with { IsArchived = false };
            }

            transaction.Commit();
            return existing;
        }

        var task = new SavedTask(Guid.NewGuid(), projectId, name);
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "INSERT INTO SavedTasks (Id, ProjectId, Name, IsArchived, Origin) VALUES ($id, $project, $name, 0, 0);",
            cancellationToken,
            ("$id", task.Id.ToString("D")),
            ("$project", projectId.ToString("D")),
            ("$name", task.Name));
        transaction.Commit();
        return task;
    }

    public async Task<TagDefinition> GetOrAddTagAsync(
        string name,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        name = TagParser.Normalize(name)
            ?? throw new ArgumentException(
                "A tag name can contain letters, numbers, underscores, and hyphens.",
                nameof(name));
        projectId = NormalizeTagProjectId(projectId);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var colors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var colorsCommand = connection.CreateCommand())
        {
            colorsCommand.Transaction = transaction;
            colorsCommand.CommandText = "SELECT Color FROM Tags;";
            await using var reader = await colorsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                colors.Add(reader.GetString(0));
            }
        }

        var proposedId = Guid.NewGuid();
        var proposedColor = GenerateUniqueTagColor(colors);
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "INSERT OR IGNORE INTO Tags (Id, Name, Color, IsGlobal) VALUES ($id, $name, $color, $global);",
            cancellationToken,
            ("$id", proposedId.ToString("D")),
            ("$name", name),
            ("$color", proposedColor),
            ("$global", projectId is null ? 1 : 0));

        Guid tagId;
        bool isGlobal;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Id, IsGlobal FROM Tags WHERE Name = $name LIMIT 1;";
            command.Parameters.AddWithValue("$name", name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The tag could not be created or loaded.");
            }

            tagId = Guid.Parse(reader.GetString(0));
            isGlobal = reader.GetBoolean(1);
        }

        if (!isGlobal && projectId is null)
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "UPDATE Tags SET IsGlobal = 1 WHERE Id = $id; DELETE FROM ProjectTags WHERE TagId = $id;",
                cancellationToken,
                ("$id", tagId.ToString("D")));
        }
        else if (!isGlobal && projectId is { } assignedProjectId)
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "INSERT OR IGNORE INTO ProjectTags (TagId, ProjectId) VALUES ($tag, $project);",
                cancellationToken,
                ("$tag", tagId.ToString("D")),
                ("$project", assignedProjectId.ToString("D")));
        }

        transaction.Commit();
        return (await GetTagsAsync(projectId, cancellationToken))
            .Single(tag => tag.Id == tagId);
    }

    public async Task<TagDefinition> AddTagAsync(
        string name,
        string color,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        name = TagParser.Normalize(name)
            ?? throw new ArgumentException(
                "A tag name can contain letters, numbers, underscores, and hyphens.",
                nameof(name));
        color = NormalizeColor(color);
        projectId = NormalizeTagProjectId(projectId);
        var tagId = Guid.NewGuid();
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "INSERT INTO Tags (Id, Name, Color, IsGlobal) VALUES ($id, $name, $color, $global);",
            cancellationToken,
            ("$id", tagId.ToString("D")),
            ("$name", name),
            ("$color", color),
            ("$global", projectId is null ? 1 : 0));
        if (projectId is { } assignedProjectId)
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "INSERT INTO ProjectTags (TagId, ProjectId) VALUES ($tag, $project);",
                cancellationToken,
                ("$tag", tagId.ToString("D")),
                ("$project", assignedProjectId.ToString("D")));
        }

        transaction.Commit();
        return (await GetTagsAsync(projectId, cancellationToken))
            .Single(tag => tag.Id == tagId);
    }

    public async Task<RecognitionRule> AddRuleAsync(Guid projectId, string titlePattern, string? processName, CancellationToken cancellationToken = default)
    {
        titlePattern = Required(titlePattern, nameof(titlePattern));
        processName = string.IsNullOrWhiteSpace(processName) ? null : Path.GetFileNameWithoutExtension(processName.Trim());
        var rule = new RecognitionRule(Guid.NewGuid(), projectId, titlePattern, processName);
        await ExecuteParameterizedAsync(
            "INSERT INTO RecognitionRules (Id, ProjectId, TitlePattern, ProcessName, IsEnabled) VALUES ($id, $project, $pattern, $process, 1);",
            cancellationToken,
            ("$id", rule.Id.ToString("D")), ("$project", projectId.ToString("D")), ("$pattern", titlePattern), ("$process", processName));
        return rule;
    }

    public async Task<CustomTarget> AddCustomTargetAsync(
        string name,
        Guid? projectId,
        CustomTargetCadence cadence,
        double targetHours,
        TargetDurationMetric durationMetric = TargetDurationMetric.ActiveTime,
        CancellationToken cancellationToken = default)
    {
        ValidateTargetDurationMetric(durationMetric);
        var (normalizedName, normalizedProjectId, normalizedHours) =
            NormalizeCustomTarget(name, projectId, cadence, targetHours);
        var nowUtc = DateTimeOffset.UtcNow;
        var target = new CustomTarget(
            Guid.NewGuid(),
            normalizedName,
            normalizedProjectId,
            cadence,
            normalizedHours,
            nowUtc,
            nowUtc,
            DurationMetric: durationMetric);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO CustomTargets (Id, Name, ProjectId, Cadence, TargetHours, CreatedUtc, ModifiedUtc, CompletedUtc, DurationMetric)
            VALUES ($id, $name, $project, $cadence, $hours, $created, $modified, NULL, $durationMetric);
            """,
            cancellationToken,
            ("$id", target.Id.ToString("D")),
            ("$name", target.Name),
            ("$project", target.ProjectId?.ToString("D")),
            ("$cadence", (int)target.Cadence),
            ("$hours", target.TargetHours),
            ("$created", Format(target.CreatedUtc)),
            ("$modified", Format(target.ModifiedUtc)),
            ("$durationMetric", (int)target.DurationMetric));
        if (target.ProjectId is { } scopedProjectId)
        {
            await RecalculateProjectTargetSummariesAsync(
                connection,
                transaction,
                scopedProjectId,
                cancellationToken);
        }

        transaction.Commit();
        return target;
    }

    public Task SetCustomTargetCompletionAsync(
        Guid targetId,
        DateTimeOffset? completedUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteParameterizedAsync(
            """
            UPDATE CustomTargets
            SET CompletedUtc = $completed
            WHERE Id = $id AND Cadence = 3;
            """,
            cancellationToken,
            ("$completed", completedUtc is { } value ? Format(value) : null),
            ("$id", targetId.ToString("D")));

    public async Task RenameClientAsync(Guid clientId, string name, CancellationToken cancellationToken = default)
    {
        await ExecuteParameterizedAsync(
            "UPDATE Clients SET Name = $name WHERE Id = $id;",
            cancellationToken,
            ("$name", Required(name, nameof(name))), ("$id", clientId.ToString("D")));
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task RenameProjectAsync(Guid projectId, string name, CancellationToken cancellationToken = default)
    {
        name = Required(name, nameof(name));
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "UPDATE Projects SET Name = $name WHERE Id = $id;",
            cancellationToken,
            ("$name", name), ("$id", projectId.ToString("D")));

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO RecognitionRules (Id, ProjectId, TitlePattern, ProcessName, IsEnabled)
            SELECT $ruleId, $project, $name, NULL, 1
            WHERE NOT EXISTS (
                SELECT 1 FROM RecognitionRules
                WHERE ProjectId = $project
                  AND TitlePattern = $name COLLATE NOCASE
                  AND ProcessName IS NULL
            );
            """,
            cancellationToken,
            ("$ruleId", Guid.NewGuid().ToString("D")),
            ("$project", projectId.ToString("D")),
            ("$name", name));

        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public Task UpdateProjectColorAsync(Guid projectId, string color, CancellationToken cancellationToken = default) =>
        ExecuteParameterizedAsync(
            "UPDATE Projects SET Color = $color WHERE Id = $id;",
            cancellationToken,
            ("$color", NormalizeColor(color)), ("$id", projectId.ToString("D")));

    public async Task UpdateProjectSettingsAsync(
        Guid projectId,
        double? dailyTargetHours,
        double? weeklyTargetHours,
        double? monthlyTargetHours,
        decimal? hourlyRate,
        string currency,
        bool? carryOverTargetDebtEnabled = null,
        CancellationToken cancellationToken = default) =>
        await UpdateProjectSettingsCoreAsync(
            projectId,
            null,
            dailyTargetHours,
            weeklyTargetHours,
            monthlyTargetHours,
            hourlyRate,
            currency,
            carryOverTargetDebtEnabled,
            cancellationToken);

    public async Task UpdateProjectSettingsAsync(
        Guid projectId,
        Guid clientId,
        double? dailyTargetHours,
        double? weeklyTargetHours,
        double? monthlyTargetHours,
        decimal? hourlyRate,
        string currency,
        bool? carryOverTargetDebtEnabled = null,
        CancellationToken cancellationToken = default) =>
        await UpdateProjectSettingsCoreAsync(
            projectId,
            clientId,
            dailyTargetHours,
            weeklyTargetHours,
            monthlyTargetHours,
            hourlyRate,
            currency,
            carryOverTargetDebtEnabled,
            cancellationToken);

    private async Task UpdateProjectSettingsCoreAsync(
        Guid projectId,
        Guid? clientId,
        double? dailyTargetHours,
        double? weeklyTargetHours,
        double? monthlyTargetHours,
        decimal? hourlyRate,
        string currency,
        bool? carryOverTargetDebtEnabled,
        CancellationToken cancellationToken)
    {
        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("Choose a valid client.", nameof(clientId));
        }

        dailyTargetHours = NormalizeTarget(dailyTargetHours, nameof(dailyTargetHours));
        weeklyTargetHours = NormalizeTarget(weeklyTargetHours, nameof(weeklyTargetHours));
        monthlyTargetHours = NormalizeTarget(monthlyTargetHours, nameof(monthlyTargetHours));
        hourlyRate = NormalizeRate(hourlyRate);
        currency = NormalizeCurrency(currency);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        double? existingDaily;
        double? existingWeekly;
        double? existingMonthly;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT DailyTargetHours, WeeklyTargetHours, MonthlyTargetHours FROM Projects WHERE Id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", projectId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new ArgumentException("Choose an existing project.", nameof(projectId));
            }

            existingDaily = reader.IsDBNull(0) ? null : reader.GetDouble(0);
            existingWeekly = reader.IsDBNull(1) ? null : reader.GetDouble(1);
            existingMonthly = reader.IsDBNull(2) ? null : reader.GetDouble(2);
        }

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE Projects
            SET ClientId = COALESCE($client, ClientId),
                HourlyRate = $rate,
                Currency = $currency,
                CarryOverTargetDebtEnabled = COALESCE($carryDebt, CarryOverTargetDebtEnabled)
            WHERE Id = $id;
            """,
            cancellationToken,
            ("$client", clientId?.ToString("D")),
            ("$rate", hourlyRate is null ? null : (double)hourlyRate.Value),
            ("$currency", currency),
            ("$carryDebt", carryOverTargetDebtEnabled is null ? null : (carryOverTargetDebtEnabled.Value ? 1 : 0)),
            ("$id", projectId.ToString("D")));

        if (!TargetValuesEqual(existingDaily, dailyTargetHours))
        {
            await ReplaceCadenceSummaryTargetAsync(
                connection,
                transaction,
                projectId,
                CustomTargetCadence.Daily,
                dailyTargetHours,
                cancellationToken);
        }

        if (!TargetValuesEqual(existingWeekly, weeklyTargetHours))
        {
            await ReplaceCadenceSummaryTargetAsync(
                connection,
                transaction,
                projectId,
                CustomTargetCadence.Weekly,
                weeklyTargetHours,
                cancellationToken);
        }

        if (!TargetValuesEqual(existingMonthly, monthlyTargetHours))
        {
            await ReplaceCadenceSummaryTargetAsync(
                connection,
                transaction,
                projectId,
                CustomTargetCadence.Monthly,
                monthlyTargetHours,
                cancellationToken);
        }

        await RecalculateProjectTargetSummariesAsync(
            connection,
            transaction,
            projectId,
            cancellationToken);
        await DeleteDebtAdjustmentsWithoutMonthlyTargetsAsync(
            connection,
            transaction,
            projectId,
            cancellationToken);
        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task UpdateProjectDetailsAsync(
        Guid projectId,
        Guid clientId,
        decimal? hourlyRate,
        string currency,
        bool carryOverTargetDebtEnabled,
        CancellationToken cancellationToken = default)
    {
        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("Choose a valid client.", nameof(clientId));
        }

        hourlyRate = NormalizeRate(hourlyRate);
        currency = NormalizeCurrency(currency);
        await ExecuteParameterizedAsync(
            """
            UPDATE Projects
            SET ClientId = $client,
                HourlyRate = $rate,
                Currency = $currency,
                CarryOverTargetDebtEnabled = $carryDebt
            WHERE Id = $id;
            """,
            cancellationToken,
            ("$client", clientId.ToString("D")),
            ("$rate", hourlyRate is null ? null : (double)hourlyRate.Value),
            ("$currency", currency),
            ("$carryDebt", carryOverTargetDebtEnabled ? 1 : 0),
            ("$id", projectId.ToString("D")));
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task RenameTaskAsync(Guid taskId, string name, CancellationToken cancellationToken = default)
    {
        if (await IsLinkedTrelloTaskAsync(taskId, cancellationToken))
        {
            throw new InvalidOperationException("Trello controls the name of this linked task. Open the card in Trello to rename it.");
        }

        await ExecuteParameterizedAsync(
            "UPDATE SavedTasks SET Name = $name WHERE Id = $id;",
            cancellationToken,
            ("$name", Required(name, nameof(name))), ("$id", taskId.ToString("D")));
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task RenameTagAsync(Guid tagId, string name, CancellationToken cancellationToken = default)
    {
        name = TagParser.Normalize(name)
            ?? throw new ArgumentException("A tag can contain letters, numbers, hyphens, and underscores.", nameof(name));

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        string? oldName;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Name FROM Tags WHERE Id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", tagId.ToString("D"));
            oldName = (string?)await command.ExecuteScalarAsync(cancellationToken);
        }

        if (oldName is null || string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase))
        {
            transaction.Commit();
            return;
        }

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "UPDATE Tags SET Name = $name WHERE Id = $id;",
            cancellationToken,
            ("$name", name),
            ("$id", tagId.ToString("D")));

        var entries = new List<(Guid Id, string Description)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Id, Description FROM TimeEntries WHERE has_tag(Description, $tag);";
            command.Parameters.AddWithValue("$tag", oldName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                entries.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1)));
            }
        }

        foreach (var entry in entries)
        {
            var description = TagParser.Rename(entry.Description, oldName, name);
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "UPDATE TimeEntries SET Description = $description, ModifiedUtc = $now WHERE Id = $id;",
                cancellationToken,
                ("$description", description),
                ("$now", Format(DateTimeOffset.UtcNow)),
                ("$id", entry.Id.ToString("D")));
        }

        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public Task UpdateTagColorAsync(Guid tagId, string color, CancellationToken cancellationToken = default) =>
        ExecuteParameterizedAsync(
            "UPDATE Tags SET Color = $color WHERE Id = $id;",
            cancellationToken,
            ("$color", NormalizeColor(color)),
            ("$id", tagId.ToString("D")));

    public async Task UpdateTagAsync(
        Guid tagId,
        string name,
        string color,
        Guid? projectId,
        CancellationToken cancellationToken = default)
    {
        await RenameTagAsync(tagId, name, cancellationToken);
        projectId = NormalizeTagProjectId(projectId);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "UPDATE Tags SET Color = $color, IsGlobal = $global WHERE Id = $id;",
            cancellationToken,
            ("$color", NormalizeColor(color)),
            ("$global", projectId is null ? 1 : 0),
            ("$id", tagId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM ProjectTags WHERE TagId = $id;",
            cancellationToken,
            ("$id", tagId.ToString("D")));
        if (projectId is { } assignedProjectId)
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "INSERT INTO ProjectTags (TagId, ProjectId) VALUES ($tag, $project);",
                cancellationToken,
                ("$tag", tagId.ToString("D")),
                ("$project", assignedProjectId.ToString("D")));
        }

        transaction.Commit();
    }

    public async Task DeleteTagAsync(Guid tagId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        string? tagName;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Name FROM Tags WHERE Id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", tagId.ToString("D"));
            tagName = (string?)await command.ExecuteScalarAsync(cancellationToken);
        }

        if (tagName is null)
        {
            transaction.Commit();
            return;
        }

        var entries = new List<(Guid Id, string Description)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Id, Description FROM TimeEntries WHERE has_tag(Description, $tag);";
            command.Parameters.AddWithValue("$tag", tagName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                entries.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1)));
            }
        }

        var modifiedUtc = Format(DateTimeOffset.UtcNow);
        foreach (var entry in entries)
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "UPDATE TimeEntries SET Description = $description, ModifiedUtc = $now WHERE Id = $id;",
                cancellationToken,
                ("$description", TagParser.ConvertToText(entry.Description, tagName)),
                ("$now", modifiedUtc),
                ("$id", entry.Id.ToString("D")));
        }

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM Tags WHERE Id = $id;",
            cancellationToken,
            ("$id", tagId.ToString("D")));

        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task RenameSoftwareAsync(Guid softwareId, string label, CancellationToken cancellationToken = default)
    {
        await ExecuteParameterizedAsync(
            "UPDATE Software SET Label = $label WHERE Id = $id;",
            cancellationToken,
            ("$label", Required(label, nameof(label))),
            ("$id", softwareId.ToString("D")));
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task<SoftwareDefinition> AddSoftwareAsync(
        string processName,
        string label,
        Guid projectId,
        bool isExcluded,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        processName = NormalizeProcessName(processName);
        label = Required(label, nameof(label));
        var proposedSoftwareId = Guid.NewGuid();
        var normalizedTagIds = NormalizeIds(tagIds);
        var isGlobal = projectId == SystemEntityIds.GlobalSoftwareScopeId;
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO Software (Id, ProcessName, Label, IsExcluded, IsHidden, IsGlobal)
            VALUES ($id, $process, $label, $excluded, 0, $global)
            ON CONFLICT(ProcessName) DO UPDATE SET
                Label = excluded.Label,
                IsExcluded = excluded.IsExcluded,
                IsHidden = 0,
                IsGlobal = excluded.IsGlobal;
            """,
            cancellationToken,
            ("$id", proposedSoftwareId.ToString("D")),
            ("$process", processName),
            ("$label", label),
            ("$excluded", isGlobal && isExcluded ? 1 : 0),
            ("$global", isGlobal ? 1 : 0));
        SoftwareDefinition software;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT s.Id, s.ProcessName, s.Label, COUNT(es.TimeEntryId)
                FROM Software s
                LEFT JOIN TimeEntrySoftware es ON es.SoftwareId = s.Id
                WHERE s.ProcessName = $process
                GROUP BY s.Id, s.ProcessName, s.Label;
                """;
            command.Parameters.AddWithValue("$process", processName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The software record could not be created or restored.");
            }

            software = new SoftwareDefinition(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3));
        }
        if (isGlobal)
        {
            await ReplaceGlobalSoftwareSettingsAsync(
                connection,
                transaction,
                software.Id,
                isExcluded,
                normalizedTagIds,
                cancellationToken);
        }
        else
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "DELETE FROM SoftwareTags WHERE SoftwareId = $software;",
                cancellationToken,
                ("$software", software.Id.ToString("D")));
            await ReplaceProjectSoftwareSettingsAsync(
                connection,
                transaction,
                projectId,
                software.Id,
                isExcluded,
                normalizedTagIds,
                cancellationToken);
        }
        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
        return software;
    }

    public async Task UpdateSoftwareAsync(
        Guid softwareId,
        Guid projectId,
        string label,
        bool isExcluded,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken = default)
    {
        label = Required(label, nameof(label));
        var normalizedTagIds = NormalizeIds(tagIds);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "UPDATE Software SET Label = $label WHERE Id = $id;",
            cancellationToken,
            ("$label", label),
            ("$id", softwareId.ToString("D")));
        if (projectId == SystemEntityIds.GlobalSoftwareScopeId)
        {
            await ReplaceGlobalSoftwareSettingsAsync(
                connection,
                transaction,
                softwareId,
                isExcluded,
                normalizedTagIds,
                cancellationToken);
        }
        else
        {
            await ReplaceProjectSoftwareSettingsAsync(
                connection,
                transaction,
                projectId,
                softwareId,
                isExcluded,
                normalizedTagIds,
                cancellationToken);
        }

        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task RemoveSoftwareFromListAsync(
        Guid softwareId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "UPDATE Software SET IsHidden = 1 WHERE Id = $software;",
            cancellationToken,
            ("$software", softwareId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM ProjectSoftwareSettings WHERE SoftwareId = $software;",
            cancellationToken,
            ("$software", softwareId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM SoftwareTags WHERE SoftwareId = $software;",
            cancellationToken,
            ("$software", softwareId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM TimeEntrySoftware WHERE SoftwareId = $software;",
            cancellationToken,
            ("$software", softwareId.ToString("D")));
        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task BulkUpdateProjectsAsync(
        IReadOnlyCollection<Guid> projectIds,
        ProjectBulkEdit edit,
        CancellationToken cancellationToken = default)
    {
        var ids = NormalizeIds(projectIds);
        ArgumentNullException.ThrowIfNull(edit);
        if (ids.Length == 0 ||
            !edit.UpdateClient &&
            !edit.UpdateColor &&
            !edit.UpdateDailyTarget &&
            !edit.UpdateWeeklyTarget &&
            !edit.UpdateMonthlyTarget &&
            !edit.UpdateHourlyRate &&
            !edit.UpdateCurrency &&
            !edit.UpdateCarryOverTargetDebt)
        {
            return;
        }

        if (edit.UpdateClient && (edit.ClientId is not Guid clientId || clientId == Guid.Empty))
        {
            throw new ArgumentException("Choose a valid client.", nameof(edit));
        }

        var assignments = new List<string>();
        var parameters = new List<(string Name, object? Value)>();
        if (edit.UpdateClient)
        {
            assignments.Add("ClientId = $client");
            parameters.Add(("$client", edit.ClientId!.Value.ToString("D")));
        }

        if (edit.UpdateColor)
        {
            assignments.Add("Color = $color");
            parameters.Add(("$color", NormalizeColor(edit.Color ?? string.Empty)));
        }

        var dailyTarget = edit.UpdateDailyTarget
            ? NormalizeTarget(edit.DailyTargetHours, nameof(edit.DailyTargetHours))
            : null;
        var weeklyTarget = edit.UpdateWeeklyTarget
            ? NormalizeTarget(edit.WeeklyTargetHours, nameof(edit.WeeklyTargetHours))
            : null;
        var monthlyTarget = edit.UpdateMonthlyTarget
            ? NormalizeTarget(edit.MonthlyTargetHours, nameof(edit.MonthlyTargetHours))
            : null;

        if (edit.UpdateHourlyRate)
        {
            assignments.Add("HourlyRate = $rate");
            var rate = NormalizeRate(edit.HourlyRate);
            parameters.Add(("$rate", rate is null ? null : (double)rate.Value));
        }

        if (edit.UpdateCurrency)
        {
            assignments.Add("Currency = $currency");
            parameters.Add(("$currency", NormalizeCurrency(edit.Currency ?? string.Empty)));
        }

        if (edit.UpdateCarryOverTargetDebt)
        {
            assignments.Add("CarryOverTargetDebtEnabled = $carryDebt");
            parameters.Add(("$carryDebt", edit.CarryOverTargetDebtEnabled == true ? 1 : 0));
        }

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var projectId in ids)
        {
            if (assignments.Count > 0)
            {
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    $"UPDATE Projects SET {string.Join(", ", assignments)} WHERE Id = $id;",
                    cancellationToken,
                    parameters.Append(("$id", projectId.ToString("D"))).ToArray());
            }

            if (edit.UpdateDailyTarget)
            {
                await ReplaceCadenceSummaryTargetAsync(
                    connection,
                    transaction,
                    projectId,
                    CustomTargetCadence.Daily,
                    dailyTarget,
                    cancellationToken);
            }

            if (edit.UpdateWeeklyTarget)
            {
                await ReplaceCadenceSummaryTargetAsync(
                    connection,
                    transaction,
                    projectId,
                    CustomTargetCadence.Weekly,
                    weeklyTarget,
                    cancellationToken);
            }

            if (edit.UpdateMonthlyTarget)
            {
                await ReplaceCadenceSummaryTargetAsync(
                    connection,
                    transaction,
                    projectId,
                    CustomTargetCadence.Monthly,
                    monthlyTarget,
                    cancellationToken);
            }

            await RecalculateProjectTargetSummariesAsync(
                connection,
                transaction,
                projectId,
                cancellationToken);
        }

        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task BulkUpdateTasksAsync(
        IReadOnlyCollection<Guid> taskIds,
        TaskBulkEdit edit,
        CancellationToken cancellationToken = default)
    {
        var ids = NormalizeIds(taskIds);
        ArgumentNullException.ThrowIfNull(edit);
        if (ids.Length == 0 || !edit.UpdateProject)
        {
            return;
        }

        if (edit.ProjectId is not Guid projectId || projectId == Guid.Empty)
        {
            throw new ArgumentException("Choose a valid project.", nameof(edit));
        }

        if (await CountLinkedTrelloTasksAsync(ids, cancellationToken) > 0)
        {
            throw new InvalidOperationException("Trello-linked tasks cannot be moved to another project.");
        }

        await ExecuteBulkUpdateAsync(
            "SavedTasks",
            ids,
            ["ProjectId = $project"],
            [("$project", projectId.ToString("D"))],
            cancellationToken);
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public Task BulkUpdateTagsAsync(
        IReadOnlyCollection<Guid> tagIds,
        TagBulkEdit edit,
        CancellationToken cancellationToken = default)
    {
        var ids = NormalizeIds(tagIds);
        ArgumentNullException.ThrowIfNull(edit);
        return ids.Length == 0 || !edit.UpdateColor
            ? Task.CompletedTask
            : ExecuteBulkUpdateAsync(
                "Tags",
                ids,
                ["Color = $color"],
                [("$color", NormalizeColor(edit.Color ?? string.Empty))],
                cancellationToken);
    }

    public async Task BulkUpdateRulesAsync(
        IReadOnlyCollection<Guid> ruleIds,
        RecognitionRuleBulkEdit edit,
        CancellationToken cancellationToken = default)
    {
        var ids = NormalizeIds(ruleIds);
        ArgumentNullException.ThrowIfNull(edit);
        if (ids.Length == 0 ||
            !edit.UpdateProject &&
            !edit.UpdateTitlePattern &&
            !edit.UpdateProcessName)
        {
            return;
        }

        var assignments = new List<string>();
        var parameters = new List<(string Name, object? Value)>();
        if (edit.UpdateProject)
        {
            if (edit.ProjectId is not Guid projectId || projectId == Guid.Empty)
            {
                throw new ArgumentException("Choose a valid project.", nameof(edit));
            }

            assignments.Add("ProjectId = $project");
            parameters.Add(("$project", projectId.ToString("D")));
        }

        if (edit.UpdateTitlePattern)
        {
            assignments.Add("TitlePattern = $pattern");
            parameters.Add(("$pattern", Required(edit.TitlePattern ?? string.Empty, nameof(edit.TitlePattern))));
        }

        if (edit.UpdateProcessName)
        {
            assignments.Add("ProcessName = $process");
            parameters.Add(("$process", string.IsNullOrWhiteSpace(edit.ProcessName)
                ? null
                : Path.GetFileNameWithoutExtension(edit.ProcessName.Trim())));
        }

        await ExecuteBulkUpdateAsync(
            "RecognitionRules",
            ids,
            assignments,
            parameters,
            cancellationToken);
    }

    public Task UpdateRuleAsync(
        Guid ruleId,
        Guid projectId,
        string titlePattern,
        string? processName,
        CancellationToken cancellationToken = default)
    {
        titlePattern = Required(titlePattern, nameof(titlePattern));
        processName = string.IsNullOrWhiteSpace(processName) ? null : Path.GetFileNameWithoutExtension(processName.Trim());
        return ExecuteParameterizedAsync(
            "UPDATE RecognitionRules SET ProjectId = $project, TitlePattern = $pattern, ProcessName = $process WHERE Id = $id;",
            cancellationToken,
            ("$project", projectId.ToString("D")),
            ("$pattern", titlePattern),
            ("$process", processName),
            ("$id", ruleId.ToString("D")));
    }

    public async Task UpdateCustomTargetAsync(
        Guid targetId,
        string name,
        Guid? projectId,
        CustomTargetCadence cadence,
        double targetHours,
        TargetDurationMetric durationMetric = TargetDurationMetric.ActiveTime,
        CancellationToken cancellationToken = default)
    {
        ValidateTargetDurationMetric(durationMetric);
        var (normalizedName, normalizedProjectId, normalizedHours) =
            NormalizeCustomTarget(name, projectId, cadence, targetHours);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        Guid? previousProjectId;
        CustomTargetCadence? previousCadence;
        double? previousHours;
        DateTimeOffset? previousCompletedUtc;
        TargetDurationMetric? previousDurationMetric;
        await using (var previousCommand = connection.CreateCommand())
        {
            previousCommand.Transaction = transaction;
            previousCommand.CommandText =
                "SELECT ProjectId, Cadence, TargetHours, CompletedUtc, DurationMetric FROM CustomTargets WHERE Id = $id LIMIT 1;";
            previousCommand.Parameters.AddWithValue("$id", targetId.ToString("D"));
            await using var reader = await previousCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                previousProjectId = reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0));
                previousCadence = (CustomTargetCadence)reader.GetInt32(1);
                previousHours = reader.GetDouble(2);
                previousCompletedUtc = reader.IsDBNull(3) ? null : Parse(reader.GetString(3));
                previousDurationMetric = (TargetDurationMetric)reader.GetInt32(4);
            }
            else
            {
                previousProjectId = null;
                previousCadence = null;
                previousHours = null;
                previousCompletedUtc = null;
                previousDurationMetric = null;
            }
        }

        var preserveCompletion = previousCadence == cadence &&
            TargetValuesEqual(previousHours, normalizedHours) &&
            previousProjectId == normalizedProjectId &&
            previousDurationMetric == durationMetric;

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE CustomTargets
            SET Name = $name,
                ProjectId = $project,
                Cadence = $cadence,
                TargetHours = $hours,
                DurationMetric = $durationMetric,
                CompletedUtc = $completed,
                ModifiedUtc = $modified
            WHERE Id = $id;
            """,
            cancellationToken,
            ("$name", normalizedName),
            ("$project", normalizedProjectId?.ToString("D")),
            ("$cadence", (int)cadence),
            ("$hours", normalizedHours),
            ("$durationMetric", (int)durationMetric),
            ("$completed", preserveCompletion && previousCompletedUtc is { } completed
                ? Format(completed)
                : null),
            ("$modified", Format(DateTimeOffset.UtcNow)),
            ("$id", targetId.ToString("D")));
        if (previousProjectId is { } oldProjectId)
        {
            await RecalculateProjectTargetSummariesAsync(
                connection,
                transaction,
                oldProjectId,
                cancellationToken);
            await DeleteDebtAdjustmentsWithoutMonthlyTargetsAsync(
                connection,
                transaction,
                oldProjectId,
                cancellationToken);
        }

        if (normalizedProjectId is { } newProjectId && newProjectId != previousProjectId)
        {
            await RecalculateProjectTargetSummariesAsync(
                connection,
                transaction,
                newProjectId,
                cancellationToken);
            await DeleteDebtAdjustmentsWithoutMonthlyTargetsAsync(
                connection,
                transaction,
                newProjectId,
                cancellationToken);
        }

        transaction.Commit();
    }

    public async Task ReplaceProjectTargetsAsync(
        Guid projectId,
        IReadOnlyList<ProjectTargetInput> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Choose a valid project.", nameof(projectId));
        }

        var normalized = targets.Select(target =>
        {
            ArgumentNullException.ThrowIfNull(target);
            var (name, _, hours) = NormalizeCustomTarget(
                target.Name,
                projectId,
                target.Cadence,
                target.TargetHours);
            ValidateTargetDurationMetric(target.DurationMetric);
            return new ProjectTargetInput(target.Id, name, target.Cadence, hours, target.DurationMetric);
        }).ToArray();
        var duplicateId = normalized
            .Where(target => target.Id is not null)
            .GroupBy(target => target.Id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw new ArgumentException("A target cannot appear more than once.", nameof(targets));
        }

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var existingTargets = new Dictionary<Guid, ExistingTargetState>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT Id, CreatedUtc, Cadence, TargetHours, CompletedUtc, DurationMetric FROM CustomTargets WHERE ProjectId = $project;";
            command.Parameters.AddWithValue("$project", projectId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingTargets[Guid.Parse(reader.GetString(0))] = new ExistingTargetState(
                    Parse(reader.GetString(1)),
                    (CustomTargetCadence)reader.GetInt32(2),
                    reader.GetDouble(3),
                    reader.IsDBNull(4) ? null : Parse(reader.GetString(4)),
                    (TargetDurationMetric)reader.GetInt32(5));
            }
        }

        var invalidExistingId = normalized
            .Where(target => target.Id is { } id && !existingTargets.ContainsKey(id))
            .Select(target => target.Id)
            .FirstOrDefault();
        if (invalidExistingId is not null)
        {
            throw new ArgumentException("A target does not belong to this project.", nameof(targets));
        }

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM CustomTargets WHERE ProjectId = $project;",
            cancellationToken,
            ("$project", projectId.ToString("D")));

        var modifiedUtc = DateTimeOffset.UtcNow;
        foreach (var target in normalized)
        {
            var id = target.Id ?? Guid.NewGuid();
            var existing = target.Id is { } existingId
                ? existingTargets[existingId]
                : null;
            var createdUtc = existing is not null
                ? existing.CreatedUtc
                : modifiedUtc;
            var completedUtc = existing is not null &&
                existing.Cadence == target.Cadence &&
                TargetValuesEqual(existing.TargetHours, target.TargetHours) &&
                existing.DurationMetric == target.DurationMetric
                    ? existing.CompletedUtc
                    : null;
            await InsertCustomTargetAsync(
                connection,
                transaction,
                new CustomTarget(
                    id,
                    target.Name,
                    projectId,
                    target.Cadence,
                    target.TargetHours,
                    createdUtc,
                    modifiedUtc,
                    completedUtc,
                    target.DurationMetric),
                cancellationToken);
        }

        await RecalculateProjectTargetSummariesAsync(
            connection,
            transaction,
            projectId,
            cancellationToken);
        await DeleteDebtAdjustmentsWithoutMonthlyTargetsAsync(
            connection,
            transaction,
            projectId,
            cancellationToken);
        transaction.Commit();
    }

    public async Task ArchiveClientAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM TimeEntries WHERE ProjectId IN (SELECT Id FROM Projects WHERE ClientId = $id);",
            cancellationToken,
            ("$id", clientId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM CustomTargets WHERE ProjectId IN (SELECT Id FROM Projects WHERE ClientId = $id);",
            cancellationToken,
            ("$id", clientId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM SavedTasks WHERE ProjectId IN (SELECT Id FROM Projects WHERE ClientId = $id);",
            cancellationToken,
            ("$id", clientId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM Projects WHERE ClientId = $id;",
            cancellationToken,
            ("$id", clientId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM Clients WHERE Id = $id;",
            cancellationToken,
            ("$id", clientId.ToString("D")));
        await DeleteOrphanedProjectTagsAsync(connection, transaction, cancellationToken);
        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task ArchiveProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM TimeEntries WHERE ProjectId = $id;",
            cancellationToken,
            ("$id", projectId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM CustomTargets WHERE ProjectId = $id;",
            cancellationToken,
            ("$id", projectId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM SavedTasks WHERE ProjectId = $id;",
            cancellationToken,
            ("$id", projectId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM Projects WHERE Id = $id;",
            cancellationToken,
            ("$id", projectId.ToString("D")));
        await DeleteOrphanedProjectTagsAsync(connection, transaction, cancellationToken);
        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task ArchiveTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (await IsLinkedTrelloTaskAsync(taskId, cancellationToken))
        {
            await SuppressExternalTaskAsync(taskId, cancellationToken);
            return;
        }

        await ExecuteParameterizedAsync(
            "UPDATE SavedTasks SET IsArchived = 1 WHERE Id = $id;",
            cancellationToken,
            ("$id", taskId.ToString("D")));
    }

    public Task DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default) =>
        ExecuteParameterizedAsync("DELETE FROM RecognitionRules WHERE Id = $id;", cancellationToken, ("$id", ruleId.ToString("D")));

    public async Task DeleteCustomTargetAsync(Guid targetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        Guid? projectId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT ProjectId FROM CustomTargets WHERE Id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", targetId.ToString("D"));
            var value = await command.ExecuteScalarAsync(cancellationToken);
            projectId = value is null or DBNull ? null : Guid.Parse((string)value);
        }

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM CustomTargets WHERE Id = $id;",
            cancellationToken,
            ("$id", targetId.ToString("D")));
        if (projectId is { } scopedProjectId)
        {
            await RecalculateProjectTargetSummariesAsync(
                connection,
                transaction,
                scopedProjectId,
                cancellationToken);
            await DeleteDebtAdjustmentsWithoutMonthlyTargetsAsync(
                connection,
                transaction,
                scopedProjectId,
                cancellationToken);
        }

        transaction.Commit();
    }

    public async Task<TimeEntry?> GetRunningEntryAsync(CancellationToken cancellationToken = default)
    {
        var items = await QueryAsync(
            """
            SELECT Id, ProjectId, TaskId, Description, StartUtc, EndUtc, LastCheckpointUtc,
                   DetailsPending, Source, CreatedUtc, ModifiedUtc, IsPaid
            FROM TimeEntries WHERE EndUtc IS NULL LIMIT 1;
            """,
            MapTimeEntry,
            cancellationToken);
        return items.FirstOrDefault();
    }

    public async Task<TimeEntry?> GetTimeEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        var items = await QueryAsync(
            """
            SELECT Id, ProjectId, TaskId, Description, StartUtc, EndUtc, LastCheckpointUtc,
                   DetailsPending, Source, CreatedUtc, ModifiedUtc, IsPaid
            FROM TimeEntries
            WHERE Id = $id
            LIMIT 1;
            """,
            MapTimeEntry,
            cancellationToken,
            ("$id", entryId.ToString("D")));
        return items.FirstOrDefault();
    }

    public async Task<long> GetEntryExcludedSecondsAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(SUM(
                CAST(strftime('%s', EndUtc) AS INTEGER) -
                CAST(strftime('%s', StartUtc) AS INTEGER)), 0)
            FROM TimeExclusions
            WHERE TimeEntryId = $entry;
            """;
        command.Parameters.AddWithValue("$entry", entryId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task<TimeEntry> StartTimerAsync(Guid projectId, TrackingSource source, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        nowUtc = nowUtc.ToUniversalTime();

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE TimeEntries
            SET EndUtc = $now,
                LastCheckpointUtc = $now,
                DetailsPending = CASE
                    WHEN ProjectId = $unassigned
                      OR (TaskId IS NULL AND TRIM(COALESCE(Description, '')) = '')
                    THEN 1
                    ELSE DetailsPending
                END,
                ModifiedUtc = $now
            WHERE EndUtc IS NULL;
            """,
            cancellationToken,
            ("$now", Format(nowUtc)),
            ("$unassigned", SystemEntityIds.UnassignedProjectId.ToString("D")));

        await DeleteSubMinuteCompletedEntriesAsync(connection, transaction, entryId: null, cancellationToken);

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO TimeEntries
                (Id, ProjectId, TaskId, Description, StartUtc, EndUtc, LastCheckpointUtc, DetailsPending, Source, CreatedUtc, ModifiedUtc, IsPaid)
            VALUES
                ($id, $project, NULL, NULL, $now, NULL, $now, 1, $source, $now, $now, 0);
            """,
            cancellationToken,
            ("$id", id.ToString("D")),
            ("$project", projectId.ToString("D")),
            ("$now", Format(nowUtc)),
            ("$source", (int)source));

        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
        return new TimeEntry(id, projectId, null, null, nowUtc, null, nowUtc, true, source, nowUtc, nowUtc);
    }

    public async Task<TimerStartResult> StartOrResumeTimerAsync(
        Guid projectId,
        Guid? taskId,
        string? description,
        TrackingSource source,
        DateTimeOffset nowUtc,
        TimeSpan maximumGap,
        CancellationToken cancellationToken = default)
    {
        if (maximumGap < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGap));
        }

        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        nowUtc = nowUtc.ToUniversalTime();
        var pending =
            projectId == SystemEntityIds.UnassignedProjectId ||
            taskId is null && description is null;

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE TimeEntries
            SET EndUtc = $now,
                LastCheckpointUtc = $now,
                DetailsPending = CASE
                    WHEN ProjectId = $unassigned
                      OR (TaskId IS NULL AND TRIM(COALESCE(Description, '')) = '')
                    THEN 1
                    ELSE DetailsPending
                END,
                ModifiedUtc = $now
            WHERE EndUtc IS NULL;
            """,
            cancellationToken,
            ("$now", Format(nowUtc)),
            ("$unassigned", SystemEntityIds.UnassignedProjectId.ToString("D")));

        await DeleteSubMinuteCompletedEntriesAsync(
            connection,
            transaction,
            entryId: null,
            cancellationToken);

        TimeEntry? previous = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT Id, ProjectId, TaskId, Description, StartUtc, EndUtc, LastCheckpointUtc,
                       DetailsPending, Source, CreatedUtc, ModifiedUtc, IsPaid
                FROM TimeEntries
                WHERE EndUtc IS NOT NULL AND EndUtc <= $now
                ORDER BY EndUtc DESC, ModifiedUtc DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$now", Format(nowUtc));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                previous = MapTimeEntry(reader);
            }
        }

        if (CanResumePreviousEntry(
                previous,
                projectId,
                taskId,
                description,
                nowUtc,
                maximumGap))
        {
            var previousEntry = previous!;
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                UPDATE TimeEntries
                SET EndUtc = NULL,
                    LastCheckpointUtc = $now,
                    DetailsPending = $pending,
                    ModifiedUtc = $now
                WHERE Id = $id AND EndUtc = $previousEnd;
                """,
                cancellationToken,
                ("$now", Format(nowUtc)),
                ("$pending", pending ? 1 : 0),
                ("$id", previousEntry.Id.ToString("D")),
                ("$previousEnd", Format(previousEntry.EndUtc!.Value)));
            transaction.Commit();
            await connection.CloseAsync();
            await SynchronizeMonthlyLogFilesAsync(cancellationToken);
            return new TimerStartResult(
                previousEntry with
                {
                    EndUtc = null,
                    LastCheckpointUtc = nowUtc,
                    DetailsPending = pending,
                    ModifiedUtc = nowUtc,
                },
                ResumedPreviousEntry: true);
        }

        var id = Guid.NewGuid();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO TimeEntries
                (Id, ProjectId, TaskId, Description, StartUtc, EndUtc, LastCheckpointUtc, DetailsPending, Source, CreatedUtc, ModifiedUtc, IsPaid)
            VALUES
                ($id, $project, $task, $description, $now, NULL, $now, $pending, $source, $now, $now, 0);
            """,
            cancellationToken,
            ("$id", id.ToString("D")),
            ("$project", projectId.ToString("D")),
            ("$task", taskId?.ToString("D")),
            ("$description", description),
            ("$now", Format(nowUtc)),
            ("$pending", pending ? 1 : 0),
            ("$source", (int)source));
        await EnsureTagsForDescriptionAsync(
            connection,
            transaction,
            description,
            projectId,
            cancellationToken);

        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
        return new TimerStartResult(
            new TimeEntry(
                id,
                projectId,
                taskId,
                description,
                nowUtc,
                null,
                nowUtc,
                pending,
                source,
                nowUtc,
                nowUtc),
            ResumedPreviousEntry: false);
    }

    public async Task<TimeEntry> SplitRunningTimerAsync(
        Guid entryId,
        Guid? taskId,
        string? description,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        var newEntryId = Guid.NewGuid();
        nowUtc = nowUtc.ToUniversalTime();

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        TimeEntry? running = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT Id, ProjectId, TaskId, Description, StartUtc, EndUtc, LastCheckpointUtc,
                       DetailsPending, Source, CreatedUtc, ModifiedUtc, IsPaid
                FROM TimeEntries
                WHERE Id = $id AND EndUtc IS NULL
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", entryId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                running = MapTimeEntry(reader);
            }
        }

        if (running is null)
        {
            throw new InvalidOperationException("The selected entry is no longer the running timer.");
        }

        var pending =
            running.ProjectId == SystemEntityIds.UnassignedProjectId ||
            taskId is null && description is null;
        if (nowUtc < running.StartUtc)
        {
            nowUtc = running.StartUtc;
        }

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE TimeEntries
            SET TaskId = $task,
                Description = $description,
                EndUtc = $now,
                LastCheckpointUtc = $now,
                DetailsPending = $pending,
                ModifiedUtc = $now
            WHERE Id = $id AND EndUtc IS NULL;
            """,
            cancellationToken,
            ("$task", taskId?.ToString("D")),
            ("$description", description),
            ("$now", Format(nowUtc)),
            ("$pending", pending ? 1 : 0),
            ("$id", running.Id.ToString("D")));
        await EnsureTagsForDescriptionAsync(connection, transaction, description, running.ProjectId, cancellationToken);
        await DeleteSubMinuteCompletedEntriesAsync(connection, transaction, running.Id, cancellationToken);

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO TimeEntries
                (Id, ProjectId, TaskId, Description, StartUtc, EndUtc, LastCheckpointUtc, DetailsPending, Source, CreatedUtc, ModifiedUtc, IsPaid)
            VALUES
                ($id, $project, $task, $description, $now, NULL, $now, $pending, $source, $now, $now, $paid);
            """,
            cancellationToken,
            ("$id", newEntryId.ToString("D")),
            ("$project", running.ProjectId.ToString("D")),
            ("$task", taskId?.ToString("D")),
            ("$description", description),
            ("$now", Format(nowUtc)),
            ("$pending", pending ? 1 : 0),
            ("$source", (int)running.Source),
            ("$paid", running.IsPaid ? 1 : 0));

        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
        return new TimeEntry(
            newEntryId,
            running.ProjectId,
            taskId,
            description,
            nowUtc,
            null,
            nowUtc,
            pending,
            running.Source,
            nowUtc,
            nowUtc,
            running.IsPaid);
    }

    public async Task<TimeEntry> SwitchRunningTimerAsync(
        Guid entryId,
        Guid projectId,
        Guid? taskId,
        string? description,
        TrackingSource source,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        var newEntryId = Guid.NewGuid();
        nowUtc = nowUtc.ToUniversalTime();

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        TimeEntry? running = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT Id, ProjectId, TaskId, Description, StartUtc, EndUtc, LastCheckpointUtc,
                       DetailsPending, Source, CreatedUtc, ModifiedUtc, IsPaid
                FROM TimeEntries
                WHERE Id = $id AND EndUtc IS NULL
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", entryId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                running = MapTimeEntry(reader);
            }
        }

        if (running is null)
        {
            throw new InvalidOperationException("The selected entry is no longer the running timer.");
        }

        if (nowUtc < running.StartUtc)
        {
            nowUtc = running.StartUtc;
        }

        var stoppedPending =
            running.ProjectId == SystemEntityIds.UnassignedProjectId ||
            running.TaskId is null && string.IsNullOrWhiteSpace(running.Description);
        var startedPending =
            projectId == SystemEntityIds.UnassignedProjectId ||
            taskId is null && description is null;

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE TimeEntries
            SET EndUtc = $now,
                LastCheckpointUtc = $now,
                DetailsPending = $pending,
                ModifiedUtc = $now
            WHERE Id = $id AND EndUtc IS NULL;
            """,
            cancellationToken,
            ("$now", Format(nowUtc)),
            ("$pending", stoppedPending ? 1 : 0),
            ("$id", running.Id.ToString("D")));
        await DeleteSubMinuteCompletedEntriesAsync(connection, transaction, running.Id, cancellationToken);
        await EnsureTagsForDescriptionAsync(connection, transaction, description, projectId, cancellationToken);

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO TimeEntries
                (Id, ProjectId, TaskId, Description, StartUtc, EndUtc, LastCheckpointUtc, DetailsPending, Source, CreatedUtc, ModifiedUtc, IsPaid)
            VALUES
                ($id, $project, $task, $description, $now, NULL, $now, $pending, $source, $now, $now, 0);
            """,
            cancellationToken,
            ("$id", newEntryId.ToString("D")),
            ("$project", projectId.ToString("D")),
            ("$task", taskId?.ToString("D")),
            ("$description", description),
            ("$now", Format(nowUtc)),
            ("$pending", startedPending ? 1 : 0),
            ("$source", (int)source));

        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
        return new TimeEntry(
            newEntryId,
            projectId,
            taskId,
            description,
            nowUtc,
            null,
            nowUtc,
            startedPending,
            source,
            nowUtc,
            nowUtc);
    }

    public async Task<TimeEntry?> StopRunningTimerAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var running = await GetRunningEntryAsync(cancellationToken);
        if (running is null)
        {
            return null;
        }

        nowUtc = nowUtc.ToUniversalTime();
        if (nowUtc < running.StartUtc)
        {
            nowUtc = running.StartUtc;
        }

        var pending =
            running.ProjectId == SystemEntityIds.UnassignedProjectId ||
            running.TaskId is null && string.IsNullOrWhiteSpace(running.Description);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "UPDATE TimeEntries SET EndUtc = $now, LastCheckpointUtc = $now, DetailsPending = $pending, ModifiedUtc = $now WHERE Id = $id AND EndUtc IS NULL;",
            cancellationToken,
            ("$now", Format(nowUtc)), ("$pending", pending ? 1 : 0), ("$id", running.Id.ToString("D")));
        var deleted = await DeleteSubMinuteCompletedEntriesAsync(connection, transaction, running.Id, cancellationToken);
        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
        if (deleted > 0)
        {
            return null;
        }

        return running with { EndUtc = nowUtc, LastCheckpointUtc = nowUtc, DetailsPending = pending, ModifiedUtc = nowUtc };
    }

    public Task CheckpointRunningTimerAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
        ExecuteParameterizedAsync(
            "UPDATE TimeEntries SET LastCheckpointUtc = $now, ModifiedUtc = $now WHERE EndUtc IS NULL;",
            cancellationToken,
            ("$now", Format(nowUtc)));

    public async Task<TimeEntry> UpdateRunningEntryStartAsync(
        Guid entryId,
        DateTimeOffset startUtc,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        startUtc = startUtc.ToUniversalTime();
        nowUtc = nowUtc.ToUniversalTime();
        if (startUtc > nowUtc)
        {
            throw new InvalidOperationException("Start time cannot be in the future.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        TimeEntry? running = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT Id, ProjectId, TaskId, Description, StartUtc, EndUtc, LastCheckpointUtc,
                       DetailsPending, Source, CreatedUtc, ModifiedUtc, IsPaid
                FROM TimeEntries
                WHERE Id = $id AND EndUtc IS NULL
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", entryId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                running = MapTimeEntry(reader);
            }
        }

        if (running is null)
        {
            throw new InvalidOperationException("The selected entry is no longer the running timer.");
        }

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            DELETE FROM TimeExclusions
            WHERE TimeEntryId = $id AND EndUtc <= $start;
            """,
            cancellationToken,
            ("$id", entryId.ToString("D")),
            ("$start", Format(startUtc)));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE TimeExclusions
            SET StartUtc = $start
            WHERE TimeEntryId = $id AND StartUtc < $start AND EndUtc > $start;
            """,
            cancellationToken,
            ("$id", entryId.ToString("D")),
            ("$start", Format(startUtc)));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE TimeEntries
            SET StartUtc = $start,
                LastCheckpointUtc = CASE
                    WHEN LastCheckpointUtc < $start THEN $start
                    ELSE LastCheckpointUtc
                END,
                ModifiedUtc = $now
            WHERE Id = $id AND EndUtc IS NULL;
            """,
            cancellationToken,
            ("$start", Format(startUtc)),
            ("$now", Format(nowUtc)),
            ("$id", entryId.ToString("D")));

        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
        return running with
        {
            StartUtc = startUtc,
            LastCheckpointUtc = running.LastCheckpointUtc < startUtc
                ? startUtc
                : running.LastCheckpointUtc,
            ModifiedUtc = nowUtc,
        };
    }

    public async Task UpdateEntryDetailsAsync(Guid entryId, Guid? taskId, string? description, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        Guid projectId;
        await using (var projectCommand = connection.CreateCommand())
        {
            projectCommand.Transaction = transaction;
            projectCommand.CommandText = "SELECT ProjectId FROM TimeEntries WHERE Id = $id LIMIT 1;";
            projectCommand.Parameters.AddWithValue("$id", entryId.ToString("D"));
            var value = (string?)await projectCommand.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("The time entry no longer exists.");
            projectId = Guid.Parse(value);
        }

        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE TimeEntries
            SET TaskId = $task,
                Description = $description,
                DetailsPending = CASE
                    WHEN ProjectId = $unassigned OR ($task IS NULL AND $description IS NULL)
                    THEN 1
                    ELSE 0
                END,
                ModifiedUtc = $now
            WHERE Id = $id;
            """,
            cancellationToken,
            ("$task", taskId?.ToString("D")), ("$description", description),
            ("$unassigned", SystemEntityIds.UnassignedProjectId.ToString("D")),
            ("$now", Format(nowUtc)), ("$id", entryId.ToString("D")));
        await EnsureTagsForDescriptionAsync(connection, transaction, description, projectId, cancellationToken);
        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task UpdateEntryAssignmentAsync(
        Guid entryId,
        Guid projectId,
        Guid? taskId,
        string? description,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        var pending =
            projectId == SystemEntityIds.UnassignedProjectId ||
            taskId is null && description is null;
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE TimeEntries
            SET ProjectId = $project,
                TaskId = $task,
                Description = $description,
                DetailsPending = $pending,
                ModifiedUtc = $now
            WHERE Id = $id;
            """,
            cancellationToken,
            ("$project", projectId.ToString("D")),
            ("$task", taskId?.ToString("D")),
            ("$description", description),
            ("$pending", pending ? 1 : 0),
            ("$now", Format(nowUtc)),
            ("$id", entryId.ToString("D")));
        await EnsureTagsForDescriptionAsync(connection, transaction, description, projectId, cancellationToken);
        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task AddManualEntryAsync(Guid projectId, Guid? taskId, string? description, DateTimeOffset startUtc, DateTimeOffset endUtc, bool isPaid = false, CancellationToken cancellationToken = default)
    {
        ValidateRange(startUtc, endUtc);
        if (endUtc - startUtc < MinimumEntryDuration)
        {
            return;
        }

        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        var pending = taskId is null && description is null;
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO TimeEntries
                (Id, ProjectId, TaskId, Description, StartUtc, EndUtc, LastCheckpointUtc, DetailsPending, Source, CreatedUtc, ModifiedUtc, IsPaid)
            VALUES
                ($id, $project, $task, $description, $start, $end, $end, $pending, $source, $now, $now, $paid);
            """,
            cancellationToken,
            ("$id", Guid.NewGuid().ToString("D")), ("$project", projectId.ToString("D")), ("$task", taskId?.ToString("D")),
            ("$description", description), ("$start", Format(startUtc)), ("$end", Format(endUtc)),
            ("$pending", pending ? 1 : 0), ("$source", (int)TrackingSource.Manual), ("$now", Format(now)), ("$paid", isPaid ? 1 : 0));
        await EnsureTagsForDescriptionAsync(connection, transaction, description, projectId, cancellationToken);
        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task UpdateTimeEntryAsync(
        Guid entryId,
        Guid projectId,
        Guid? taskId,
        string? description,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        bool isPaid = false,
        long excludedSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(startUtc, endUtc);
        if (excludedSeconds < 0 || excludedSeconds > (long)(endUtc - startUtc).TotalSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(excludedSeconds),
                "Subtracted idle time must fit within the entry duration.");
        }

        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        var pending = taskId is null && description is null;
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE TimeEntries SET ProjectId = $project, TaskId = $task, Description = $description,
                StartUtc = $start, EndUtc = $end, LastCheckpointUtc = $end,
                DetailsPending = $pending, ModifiedUtc = $now, IsPaid = $paid
            WHERE Id = $id AND EndUtc IS NOT NULL;
            """,
            cancellationToken,
            ("$project", projectId.ToString("D")), ("$task", taskId?.ToString("D")), ("$description", description),
            ("$start", Format(startUtc)), ("$end", Format(endUtc)), ("$pending", pending ? 1 : 0),
            ("$now", Format(DateTimeOffset.UtcNow)), ("$paid", isPaid ? 1 : 0), ("$id", entryId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM TimeExclusions WHERE TimeEntryId = $id;",
            cancellationToken,
            ("$id", entryId.ToString("D")));
        if (excludedSeconds > 0)
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                INSERT INTO TimeExclusions (Id, TimeEntryId, StartUtc, EndUtc, Reason)
                VALUES ($exclusionId, $entry, $start, $end, 'Idle adjustment');
                """,
                cancellationToken,
                ("$exclusionId", Guid.NewGuid().ToString("D")),
                ("$entry", entryId.ToString("D")),
                ("$start", Format(endUtc.AddSeconds(-excludedSeconds))),
                ("$end", Format(endUtc)));
        }

        var deleted = await DeleteSubMinuteCompletedEntriesAsync(connection, transaction, entryId, cancellationToken);
        if (deleted == 0)
        {
            await EnsureTagsForDescriptionAsync(connection, transaction, description, projectId, cancellationToken);
        }

        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task SetEntriesPaidAsync(IReadOnlyCollection<Guid> entryIds, bool isPaid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        if (entryIds.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var entryId in entryIds.Distinct())
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "UPDATE TimeEntries SET IsPaid = $paid, ModifiedUtc = $now WHERE Id = $id;",
                cancellationToken,
                ("$paid", isPaid ? 1 : 0),
                ("$now", Format(DateTimeOffset.UtcNow)),
                ("$id", entryId.ToString("D")));
        }

        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task DeleteTimeEntryAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO GoogleSheetsEntryDeletions (EntryId, DeletedUtc)
            SELECT Id, $deleted FROM TimeEntries WHERE Id = $id AND EndUtc IS NOT NULL;
            """,
            cancellationToken,
            ("$deleted", Format(DateTimeOffset.UtcNow)),
            ("$id", entryId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM TimeEntries WHERE Id = $id AND EndUtc IS NOT NULL;",
            cancellationToken,
            ("$id", entryId.ToString("D")));
        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public Task<IReadOnlyList<Guid>> GetGoogleSheetsEntryDeletionIdsAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            "SELECT EntryId FROM GoogleSheetsEntryDeletions ORDER BY DeletedUtc, EntryId;",
            reader => Guid.Parse(reader.GetString(0)),
            cancellationToken);

    public async Task CompleteGoogleSheetsEntryDeletionsAsync(
        IReadOnlyCollection<Guid> entryIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        if (entryIds.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var entryId in entryIds.Distinct())
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "DELETE FROM GoogleSheetsEntryDeletions WHERE EntryId = $id;",
                cancellationToken,
                ("$id", entryId.ToString("D")));
        }

        transaction.Commit();
        await connection.CloseAsync();
    }

    public Task AddExclusionAsync(
        Guid entryId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string reason,
        CancellationToken cancellationToken = default) =>
        AddExclusionsAsync(
            entryId,
            [new TimeExclusionPeriod(startUtc, endUtc, reason)],
            cancellationToken);

    public async Task AddExclusionsAsync(
        Guid entryId,
        IReadOnlyCollection<TimeExclusionPeriod> exclusions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exclusions);
        if (exclusions.Count == 0)
        {
            return;
        }

        var periods = exclusions.ToArray();
        foreach (var period in periods)
        {
            ValidateRange(period.StartUtc, period.EndUtc);
            _ = Required(period.Reason, nameof(period.Reason));
        }

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var period in periods)
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "INSERT INTO TimeExclusions (Id, TimeEntryId, StartUtc, EndUtc, Reason) VALUES ($id, $entry, $start, $end, $reason);",
                cancellationToken,
                ("$id", Guid.NewGuid().ToString("D")),
                ("$entry", entryId.ToString("D")),
                ("$start", Format(period.StartUtc)),
                ("$end", Format(period.EndUtc)),
                ("$reason", Required(period.Reason, nameof(period.Reason))));
        }

        await DeleteSubMinuteCompletedEntriesAsync(connection, transaction, entryId, cancellationToken);
        transaction.Commit();
        await connection.CloseAsync();
        await SynchronizeMonthlyLogFilesAsync(cancellationToken);
    }

    public async Task<bool> RecordSoftwareUsageAsync(
        Guid entryId,
        string processName,
        CancellationToken cancellationToken = default)
    {
        processName = NormalizeProcessName(processName);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        int inserted;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO TimeEntrySoftware (TimeEntryId, SoftwareId)
                SELECT e.Id, s.Id
                FROM Software s
                JOIN TimeEntries e ON e.Id = $entry
                WHERE s.ProcessName = $process
                  AND s.IsHidden = 0
                  AND (
                      (s.IsGlobal = 1 AND s.IsExcluded = 0)
                      OR
                      (s.IsGlobal = 0 AND EXISTS (
                          SELECT 1
                          FROM ProjectSoftwareSettings ps
                          WHERE ps.ProjectId = e.ProjectId
                            AND ps.SoftwareId = s.Id
                            AND ps.IsExcluded = 0
                      ))
                  )
                ;
                """;
            command.Parameters.AddWithValue("$entry", entryId.ToString("D"));
            command.Parameters.AddWithValue("$process", processName);
            inserted = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
        await connection.CloseAsync();
        if (inserted > 0)
        {
            await SynchronizeMonthlyLogFilesAsync(cancellationToken);
        }

        return inserted > 0;
    }

    public Task<IReadOnlyList<TimeEntryView>> GetEntriesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT e.Id, e.ProjectId, e.TaskId,
                   CASE WHEN p.Id = $unassigned THEN 'Unassigned' ELSE c.Name END,
                   CASE WHEN p.Id = $unassigned THEN 'Unassigned' ELSE p.Name END,
                   t.Name, e.Description,
                   e.StartUtc, e.EndUtc,
                   COALESCE((SELECT SUM(CAST(strftime('%s', x.EndUtc) AS INTEGER) - CAST(strftime('%s', x.StartUtc) AS INTEGER))
                             FROM TimeExclusions x WHERE x.TimeEntryId = e.Id), 0),
                   e.DetailsPending, e.Source, e.IsPaid, p.HourlyRate, p.Currency,
                   COALESCE((
                       SELECT group_concat(SoftwareLabel, ' · ')
                       FROM (
                           SELECT DISTINCT s.Label AS SoftwareLabel
                           FROM TimeEntrySoftware es
                           JOIN Software s ON s.Id = es.SoftwareId AND s.IsHidden = 0
                           WHERE es.TimeEntryId = e.Id
                           ORDER BY s.Label COLLATE NOCASE
                       )
                   ), '')
            FROM TimeEntries e
            JOIN Projects p ON p.Id = e.ProjectId
            JOIN Clients c ON c.Id = p.ClientId
            LEFT JOIN SavedTasks t ON t.Id = e.TaskId
            WHERE e.StartUtc < $to AND (e.EndUtc IS NULL OR e.EndUtc >= $from)
            ORDER BY e.StartUtc DESC;
            """,
            MapTimeEntryView,
            cancellationToken,
            ("$from", Format(fromUtc)),
            ("$to", Format(toUtc)),
            ("$unassigned", SystemEntityIds.UnassignedProjectId.ToString("D")));

    public async Task<DateTimeOffset?> GetLatestEntryStartUtcAsync(CancellationToken cancellationToken = default)
    {
        var values = await QueryAsync(
            "SELECT StartUtc FROM TimeEntries ORDER BY StartUtc DESC LIMIT 1;",
            reader => (DateTimeOffset?)Parse(reader.GetString(0)),
            cancellationToken);
        return values.SingleOrDefault();
    }

    public Task<IReadOnlyList<TimeEntryView>> GetPendingEntriesAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT e.Id, e.ProjectId, e.TaskId,
                   CASE WHEN p.Id = $unassigned THEN 'Unassigned' ELSE c.Name END,
                   CASE WHEN p.Id = $unassigned THEN 'Unassigned' ELSE p.Name END,
                   t.Name, e.Description,
                   e.StartUtc, e.EndUtc,
                   COALESCE((SELECT SUM(CAST(strftime('%s', x.EndUtc) AS INTEGER) - CAST(strftime('%s', x.StartUtc) AS INTEGER))
                             FROM TimeExclusions x WHERE x.TimeEntryId = e.Id), 0),
                   e.DetailsPending, e.Source, e.IsPaid, p.HourlyRate, p.Currency,
                   COALESCE((
                       SELECT group_concat(SoftwareLabel, ' · ')
                       FROM (
                           SELECT DISTINCT s.Label AS SoftwareLabel
                           FROM TimeEntrySoftware es
                           JOIN Software s ON s.Id = es.SoftwareId AND s.IsHidden = 0
                           WHERE es.TimeEntryId = e.Id
                           ORDER BY s.Label COLLATE NOCASE
                       )
                   ), '')
            FROM TimeEntries e
            JOIN Projects p ON p.Id = e.ProjectId
            JOIN Clients c ON c.Id = p.ClientId
            LEFT JOIN SavedTasks t ON t.Id = e.TaskId
            WHERE e.DetailsPending = 1
            ORDER BY e.StartUtc ASC;
            """,
            MapTimeEntryView,
            cancellationToken,
            ("$unassigned", SystemEntityIds.UnassignedProjectId.ToString("D")));

    public Task<IReadOnlyList<ReportRow>> GetReportAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? tag = null,
        CancellationToken cancellationToken = default) =>
        GetReportAsync(fromUtc, toUtc, new ReportFilter(Tag: tag), cancellationToken);

    public async Task<IReadOnlyList<ReportRow>> GetReportAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        ReportFilter filter,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var shortIdleMaximumMinutes = ShortIdleReportingSettings.ParseMaximumMinutes(
            await GetSettingAsync(ShortIdleReportingSettings.MaximumMinutesKey, cancellationToken));
        var shortIdleMaximumSeconds = checked(shortIdleMaximumMinutes * 60);
        var tag = TagParser.Normalize(filter.Tag);
        var taskMode = filter.UnassignedTaskOnly ? 2 : filter.TaskId is null ? 0 : 1;
        var paid = filter.PaidStatus switch
        {
            PaidStatusFilter.Paid => 1,
            PaidStatusFilter.Unpaid => 0,
            _ => -1,
        };
        return await QueryAsync(
            """
            WITH EntryDurations AS (
                SELECT p.Id AS ProjectId,
                       e.TaskId,
                       CASE WHEN p.Id = $unassigned THEN 'Unassigned' ELSE c.Name END AS ClientName,
                       CASE WHEN p.Id = $unassigned THEN 'Unassigned' ELSE p.Name END AS ProjectName,
                       COALESCE(t.Name, 'Unassigned') AS TaskName,
                       p.HourlyRate,
                       p.Currency,
                       e.IsPaid,
                       MIN(COALESCE(e.EndUtc, $now), $to) AS LatestActivityUtc,
                       MAX(0,
                           CAST(strftime('%s', MIN(COALESCE(e.EndUtc, $now), $to)) AS INTEGER)
                           - CAST(strftime('%s', MAX(e.StartUtc, $from)) AS INTEGER)
                           - COALESCE((
                               SELECT SUM(MAX(0,
                                   CAST(strftime('%s', MIN(x.EndUtc, COALESCE(e.EndUtc, $now), $to)) AS INTEGER)
                                   - CAST(strftime('%s', MAX(x.StartUtc, e.StartUtc, $from)) AS INTEGER)
                               ))
                               FROM TimeExclusions x
                               WHERE x.TimeEntryId = e.Id
                                 AND x.StartUtc < MIN(COALESCE(e.EndUtc, $now), $to)
                                 AND x.EndUtc > MAX(e.StartUtc, $from)
                           ), 0)
                       ) AS DurationSeconds,
                       MAX(0,
                           CAST(strftime('%s', MIN(COALESCE(e.EndUtc, $now), $to)) AS INTEGER)
                           - CAST(strftime('%s', MAX(e.StartUtc, $from)) AS INTEGER)
                           - COALESCE((
                               SELECT SUM(MAX(0,
                                   CAST(strftime('%s', MIN(x.EndUtc, COALESCE(e.EndUtc, $now), $to)) AS INTEGER)
                                   - CAST(strftime('%s', MAX(x.StartUtc, e.StartUtc, $from)) AS INTEGER)
                               ))
                               FROM TimeExclusions x
                               WHERE x.TimeEntryId = e.Id
                                 AND x.StartUtc < MIN(COALESCE(e.EndUtc, $now), $to)
                                 AND x.EndUtc > MAX(e.StartUtc, $from)
                                 AND CAST(strftime('%s', x.EndUtc) AS INTEGER)
                                     - CAST(strftime('%s', x.StartUtc) AS INTEGER) > $shortIdleMaximumSeconds
                           ), 0)
                       ) AS DurationWithShortIdleSeconds
                FROM TimeEntries e
                JOIN Projects p ON p.Id = e.ProjectId
                JOIN Clients c ON c.Id = p.ClientId
                LEFT JOIN SavedTasks t ON t.Id = e.TaskId
                WHERE e.StartUtc < $to
                  AND COALESCE(e.EndUtc, $now) > $from
                  AND ($client IS NULL OR c.Id = $client)
                  AND ($project IS NULL OR p.Id = $project)
                  AND ($taskMode = 0
                       OR ($taskMode = 1 AND e.TaskId = $task)
                       OR ($taskMode = 2 AND e.TaskId IS NULL))
                  AND ($tag IS NULL OR has_tag(e.Description, $tag))
                  AND ($paid = -1 OR e.IsPaid = $paid)
            )
            SELECT ProjectId, TaskId, ClientName, ProjectName, TaskName,
                   CAST(SUM(DurationSeconds) AS INTEGER),
                   CAST(SUM(DurationWithShortIdleSeconds) AS INTEGER),
                   COUNT(*),
                   HourlyRate,
                   Currency,
                   CAST(SUM(CASE WHEN IsPaid = 1 THEN DurationSeconds ELSE 0 END) AS INTEGER),
                   CAST(SUM(CASE WHEN IsPaid = 0 THEN DurationSeconds ELSE 0 END) AS INTEGER),
                   MAX(LatestActivityUtc)
            FROM EntryDurations
            GROUP BY ProjectId, TaskId, ClientName, ProjectName, TaskName, HourlyRate, Currency
            ORDER BY ClientName COLLATE NOCASE, ProjectName COLLATE NOCASE, TaskName COLLATE NOCASE;
            """,
            reader => new ReportRow(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : Convert.ToDecimal(reader.GetDouble(8), CultureInfo.InvariantCulture),
                reader.GetString(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.IsDBNull(12) ? null : Parse(reader.GetString(12)),
                reader.GetInt64(6)),
            cancellationToken,
            ("$from", Format(fromUtc)),
            ("$to", Format(toUtc)),
            ("$now", Format(nowUtc)),
            ("$shortIdleMaximumSeconds", shortIdleMaximumSeconds),
            ("$client", filter.ClientId?.ToString("D")),
            ("$project", filter.ProjectId?.ToString("D")),
            ("$taskMode", taskMode),
            ("$task", filter.TaskId?.ToString("D")),
            ("$tag", tag),
            ("$paid", paid),
            ("$unassigned", SystemEntityIds.UnassignedProjectId.ToString("D")));
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        var values = await QueryAsync(
            "SELECT Value FROM Settings WHERE Key = $key LIMIT 1;",
            reader => reader.GetString(0),
            cancellationToken,
            ("$key", key));
        return values.FirstOrDefault();
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await ExecuteParameterizedAsync(
            "INSERT INTO Settings (Key, Value) VALUES ($key, $value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;",
            cancellationToken,
            ("$key", Required(key, nameof(key))), ("$value", value));
        if (string.Equals(key, LogExportDestinationSettings.DestinationKey, StringComparison.Ordinal))
        {
            await SynchronizeMonthlyLogFilesAsync(cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        _monthlyLogSync.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task SynchronizeMonthlyLogFilesAsync(CancellationToken cancellationToken)
    {
        await _monthlyLogSync.WaitAsync(cancellationToken);
        try
        {
            var entries = await QueryAsync(
                """
                SELECT e.Id, e.ProjectId, e.TaskId, c.Name, p.Name, t.Name, e.Description,
                       e.StartUtc, e.EndUtc,
                       COALESCE((SELECT SUM(CAST(strftime('%s', x.EndUtc) AS INTEGER) - CAST(strftime('%s', x.StartUtc) AS INTEGER))
                                 FROM TimeExclusions x WHERE x.TimeEntryId = e.Id), 0),
                       e.DetailsPending, e.Source, e.IsPaid, p.HourlyRate, p.Currency,
                       COALESCE((
                           SELECT group_concat(SoftwareLabel, ' · ')
                           FROM (
                               SELECT DISTINCT s.Label AS SoftwareLabel
                               FROM TimeEntrySoftware es
                               JOIN Software s ON s.Id = es.SoftwareId AND s.IsHidden = 0
                               WHERE es.TimeEntryId = e.Id
                               ORDER BY s.Label COLLATE NOCASE
                           )
                       ), '')
                FROM TimeEntries e
                JOIN Projects p ON p.Id = e.ProjectId
                JOIN Clients c ON c.Id = p.ClientId
                LEFT JOIN SavedTasks t ON t.Id = e.TaskId
                ORDER BY e.StartUtc ASC;
                """,
                MapTimeEntryView,
                cancellationToken);

            var exportDestination = await GetSettingAsync(
                LogExportDestinationSettings.DestinationKey,
                cancellationToken);
            var writeLocalExports = !string.Equals(
                exportDestination,
                LogExportDestinationSettings.GoogleSheets,
                StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(MonthlyLogDirectory);
            var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (writeLocalExports)
            {
                var groups = entries.GroupBy(entry =>
                {
                    var localStart = TimeZoneInfo.ConvertTime(entry.StartUtc, _monthlyLogTimeZone);
                    return (localStart.Year, localStart.Month);
                });
                foreach (var group in groups)
                {
                    var fileName = MonthlyLogWriter.GetFileName(group.Key.Year, group.Key.Month);
                    var path = Path.Combine(MonthlyLogDirectory, fileName);
                    await MonthlyLogWriter.WriteAsync(path, group.ToArray(), _monthlyLogTimeZone, cancellationToken);
                    expectedFiles.Add(fileName);
                }
            }

            var today = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _monthlyLogTimeZone).DateTime);
            if (writeLocalExports)
            {
                foreach (var group in entries.GroupBy(entry =>
                         DateOnly.FromDateTime(
                             TimeZoneInfo.ConvertTime(entry.StartUtc, _monthlyLogTimeZone).DateTime)))
                {
                    await DailySafetyArchive.WriteLogAsync(
                        MonthlyLogDirectory,
                        group.Key,
                        group.ToArray(),
                        _monthlyLogTimeZone,
                        today,
                        cancellationToken);
                }
            }

            foreach (var obsoleteFile in writeLocalExports
                         ? _managedMonthlyLogFiles.Except(expectedFiles).ToArray()
                         : Array.Empty<string>())
            {
                var path = Path.Combine(MonthlyLogDirectory, obsoleteFile);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            _managedMonthlyLogFiles.Clear();
            _managedMonthlyLogFiles.UnionWith(expectedFiles);

            await DailySafetyArchive.CreateDatabaseSnapshotsAsync(
                DatabasePath,
                _connectionString,
                MonthlyLogDirectory,
                today,
                cancellationToken);
        }
        finally
        {
            _monthlyLogSync.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        connection.CreateFunction<string?, string?, bool>(
            "has_tag",
            (description, tag) => TagParser.Contains(description, tag),
            isDeterministic: true);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;", cancellationToken);
        return connection;
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(map(reader));
        }

        return results;
    }

    private async Task ExecuteParameterizedAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSystemEntitiesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteParameterizedOnConnectionAsync(
            connection,
            """
            INSERT OR IGNORE INTO Clients (Id, Name, Color, IsArchived)
            VALUES ($id, '__ProjectTimeTrackerSystemClient__', '#687582', 0);
            """,
            cancellationToken,
            ("$id", SystemEntityIds.UnassignedClientId.ToString("D")));
        await ExecuteParameterizedOnConnectionAsync(
            connection,
            """
            INSERT OR IGNORE INTO Projects
                (Id, ClientId, Name, Color, IsArchived, Currency)
            VALUES
                ($id, $client, '__ProjectTimeTrackerUnassignedProject__', '#687582', 0, 'PLN');
            """,
            cancellationToken,
            ("$id", SystemEntityIds.UnassignedProjectId.ToString("D")),
            ("$client", SystemEntityIds.UnassignedClientId.ToString("D")));
    }

    private static async Task ExecuteParameterizedOnConnectionAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Task DeleteOrphanedProjectTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            DELETE FROM Tags
            WHERE IsGlobal = 0
              AND NOT EXISTS (
                  SELECT 1
                  FROM ProjectTags
                  WHERE ProjectTags.TagId = Tags.Id
              );
            """,
            cancellationToken);

    private static Task DeleteDebtAdjustmentsWithoutMonthlyTargetsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            DELETE FROM ProjectTargetDebtCancellations
            WHERE ProjectId = $project
              AND NOT EXISTS (
                  SELECT 1
                  FROM CustomTargets
                  WHERE ProjectId = $project
                    AND Cadence = 2
              );
            """,
            cancellationToken,
            ("$project", projectId.ToString("D")));

    private static async Task InsertCustomTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CustomTarget target,
        CancellationToken cancellationToken)
    {
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO CustomTargets
                (Id, Name, ProjectId, Cadence, TargetHours, CreatedUtc, ModifiedUtc, CompletedUtc, DurationMetric)
            VALUES
                ($id, $name, $project, $cadence, $hours, $created, $modified, $completed, $durationMetric);
            """,
            cancellationToken,
            ("$id", target.Id.ToString("D")),
            ("$name", target.Name),
            ("$project", target.ProjectId?.ToString("D")),
            ("$cadence", (int)target.Cadence),
            ("$hours", target.TargetHours),
            ("$created", Format(target.CreatedUtc)),
            ("$modified", Format(target.ModifiedUtc)),
            ("$completed", target.CompletedUtc is { } completed ? Format(completed) : null),
            ("$durationMetric", (int)target.DurationMetric));
    }

    private static async Task ReplaceCadenceSummaryTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        CustomTargetCadence cadence,
        double? targetHours,
        CancellationToken cancellationToken)
    {
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM CustomTargets WHERE ProjectId = $project AND Cadence = $cadence;",
            cancellationToken,
            ("$project", projectId.ToString("D")),
            ("$cadence", (int)cadence));
        if (targetHours is null)
        {
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        await InsertCustomTargetAsync(
            connection,
            transaction,
            new CustomTarget(
                Guid.NewGuid(),
                $"{cadence} target",
                projectId,
                cadence,
                targetHours.Value,
                nowUtc,
                nowUtc),
            cancellationToken);
    }

    private static Task RecalculateProjectTargetSummariesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid? projectId,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE Projects
            SET DailyTargetHours = (
                    SELECT SUM(TargetHours)
                    FROM CustomTargets
                    WHERE ProjectId = Projects.Id AND Cadence = 0
                ),
                WeeklyTargetHours = (
                    SELECT SUM(TargetHours)
                    FROM CustomTargets
                    WHERE ProjectId = Projects.Id AND Cadence = 1
                ),
                MonthlyTargetHours = (
                    SELECT SUM(TargetHours)
                    FROM CustomTargets
                    WHERE ProjectId = Projects.Id AND Cadence = 2
                )
            WHERE $project IS NULL OR Id = $project;
            """,
            cancellationToken,
            ("$project", projectId?.ToString("D")));

    private static bool TargetValuesEqual(double? left, double? right) =>
        left is null && right is null ||
        left is not null && right is not null && Math.Abs(left.Value - right.Value) < 0.005d;

    private static async Task MigrateProjectTargetsToRecordsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var legacyTargets = new List<(Guid ProjectId, CustomTargetCadence Cadence, double Hours)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT Id, DailyTargetHours, WeeklyTargetHours, MonthlyTargetHours
                FROM Projects
                WHERE DailyTargetHours IS NOT NULL
                   OR WeeklyTargetHours IS NOT NULL
                   OR MonthlyTargetHours IS NOT NULL;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var projectId = Guid.Parse(reader.GetString(0));
                if (!reader.IsDBNull(1))
                {
                    legacyTargets.Add((projectId, CustomTargetCadence.Daily, reader.GetDouble(1)));
                }

                if (!reader.IsDBNull(2))
                {
                    legacyTargets.Add((projectId, CustomTargetCadence.Weekly, reader.GetDouble(2)));
                }

                if (!reader.IsDBNull(3))
                {
                    legacyTargets.Add((projectId, CustomTargetCadence.Monthly, reader.GetDouble(3)));
                }
            }
        }

        using var transaction = connection.BeginTransaction();
        var nowUtc = DateTimeOffset.UtcNow;
        foreach (var target in legacyTargets)
        {
            await InsertCustomTargetAsync(
                connection,
                transaction,
                new CustomTarget(
                    Guid.NewGuid(),
                    $"{target.Cadence} target",
                    target.ProjectId,
                    target.Cadence,
                    target.Hours,
                    nowUtc,
                    nowUtc),
                cancellationToken);
        }

        await RecalculateProjectTargetSummariesAsync(
            connection,
            transaction,
            projectId: null,
            cancellationToken);
        transaction.Commit();
    }

    private static async Task ReplaceGlobalSoftwareSettingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid softwareId,
        bool isExcluded,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken)
    {
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "UPDATE Software SET IsGlobal = 1, IsExcluded = $excluded WHERE Id = $software;",
            cancellationToken,
            ("$software", softwareId.ToString("D")),
            ("$excluded", isExcluded ? 1 : 0));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM ProjectSoftwareSettings WHERE SoftwareId = $software;",
            cancellationToken,
            ("$software", softwareId.ToString("D")));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM SoftwareTags WHERE SoftwareId = $software;",
            cancellationToken,
            ("$software", softwareId.ToString("D")));
        foreach (var tagId in tagIds)
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                INSERT OR IGNORE INTO SoftwareTags (SoftwareId, TagId)
                SELECT $software, t.Id FROM Tags t WHERE t.Id = $tag;
                """,
                cancellationToken,
                ("$software", softwareId.ToString("D")),
                ("$tag", tagId.ToString("D")));
        }
    }

    private static async Task ReplaceProjectSoftwareSettingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        Guid softwareId,
        bool isExcluded,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken)
    {
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO ProjectSoftwareSettings (ProjectId, SoftwareId, IsExcluded)
            SELECT p.Id, s.Id, $excluded
            FROM Projects p
            JOIN Software s ON s.Id = $software
            WHERE p.Id = $project
            ON CONFLICT(ProjectId, SoftwareId)
            DO UPDATE SET IsExcluded = excluded.IsExcluded;
            """,
            cancellationToken,
            ("$project", projectId.ToString("D")),
            ("$software", softwareId.ToString("D")),
            ("$excluded", isExcluded ? 1 : 0));
        await ExecuteInTransactionAsync(
            connection,
            transaction,
            "DELETE FROM ProjectSoftwareTags WHERE ProjectId = $project AND SoftwareId = $software;",
            cancellationToken,
            ("$project", projectId.ToString("D")),
            ("$software", softwareId.ToString("D")));
        foreach (var tagId in tagIds)
        {
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                """
                INSERT OR IGNORE INTO ProjectSoftwareTags (ProjectId, SoftwareId, TagId)
                SELECT $project, $software, t.Id FROM Tags t WHERE t.Id = $tag;
                """,
                cancellationToken,
                ("$project", projectId.ToString("D")),
                ("$software", softwareId.ToString("D")),
                ("$tag", tagId.ToString("D")));
        }
    }

    private static async Task<int> DeleteSubMinuteCompletedEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid? entryId,
        CancellationToken cancellationToken)
    {
        await using (var tombstoneCommand = connection.CreateCommand())
        {
            tombstoneCommand.Transaction = transaction;
            tombstoneCommand.CommandText = """
                INSERT OR IGNORE INTO GoogleSheetsEntryDeletions (EntryId, DeletedUtc)
                SELECT Id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                FROM TimeEntries
                WHERE EndUtc IS NOT NULL
                  AND ($entry IS NULL OR Id = $entry)
                  AND MAX(
                        0,
                        (julianday(EndUtc) - julianday(StartUtc)) * 86400.0
                        - COALESCE((
                            SELECT SUM(MAX(
                                0,
                                (julianday(x.EndUtc) - julianday(x.StartUtc)) * 86400.0
                            ))
                            FROM TimeExclusions x
                            WHERE x.TimeEntryId = TimeEntries.Id
                        ), 0)
                      ) < 59.9995;
                """;
            tombstoneCommand.Parameters.AddWithValue(
                "$entry",
                entryId is Guid tombstoneId ? tombstoneId.ToString("D") : DBNull.Value);
            await tombstoneCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM TimeEntries
            WHERE EndUtc IS NOT NULL
              AND ($entry IS NULL OR Id = $entry)
              AND MAX(
                    0,
                    (julianday(EndUtc) - julianday(StartUtc)) * 86400.0
                    - COALESCE((
                        SELECT SUM(MAX(
                            0,
                            (julianday(x.EndUtc) - julianday(x.StartUtc)) * 86400.0
                        ))
                        FROM TimeExclusions x
                        WHERE x.TimeEntryId = TimeEntries.Id
                    ), 0)
                  ) < 59.9995;
            """;
        command.Parameters.AddWithValue(
            "$entry",
            entryId is Guid id ? id.ToString("D") : DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool CanResumePreviousEntry(
        TimeEntry? previous,
        Guid projectId,
        Guid? taskId,
        string? description,
        DateTimeOffset nowUtc,
        TimeSpan maximumGap)
    {
        if (previous is not { EndUtc: { } endUtc, IsPaid: false } ||
            maximumGap <= TimeSpan.Zero ||
            previous.ProjectId != projectId ||
            previous.TaskId != taskId ||
            !string.Equals(previous.Description, description, StringComparison.Ordinal))
        {
            return false;
        }

        var gap = nowUtc - endUtc;
        if (gap < TimeSpan.Zero || gap >= maximumGap)
        {
            return false;
        }

        return TagParser.Extract(previous.Description).SequenceEqual(
            TagParser.Extract(description),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<bool> IsLinkedTrelloTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var values = await QueryAsync(
            "SELECT COUNT(*) FROM ExternalTaskLinks WHERE TaskId = $id AND Provider = 'Trello' AND State = 0;",
            reader => reader.GetInt64(0),
            cancellationToken,
            ("$id", taskId.ToString("D")));
        return values.FirstOrDefault() > 0;
    }

    private async Task<long> CountLinkedTrelloTasksAsync(
        IReadOnlyList<Guid> taskIds,
        CancellationToken cancellationToken)
    {
        if (taskIds.Count == 0)
        {
            return 0;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var parameters = taskIds.Select((_, index) => $"$id{index}").ToArray();
        command.CommandText =
            $"SELECT COUNT(*) FROM ExternalTaskLinks WHERE Provider = 'Trello' AND State = 0 AND TaskId IN ({string.Join(", ", parameters)});";
        for (var index = 0; index < taskIds.Count; index++)
        {
            command.Parameters.AddWithValue(parameters[index], taskIds[index].ToString("D"));
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task DetachLinkedTasksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid? mappingId,
        CancellationToken cancellationToken)
    {
        var tasks = new List<(Guid TaskId, long EntryCount)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT x.TaskId, (SELECT COUNT(*) FROM TimeEntries e WHERE e.TaskId = x.TaskId)
                FROM ExternalTaskLinks x
                WHERE x.Provider = 'Trello'
                  AND x.State = 0
                  AND x.TaskId IS NOT NULL
                  AND ($mapping IS NULL OR x.MappingId = $mapping);
                """;
            command.Parameters.AddWithValue("$mapping", mappingId?.ToString("D") ?? (object)DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tasks.Add((Guid.Parse(reader.GetString(0)), reader.GetInt64(1)));
            }
        }

        foreach (var task in tasks)
        {
            if (task.EntryCount == 0)
            {
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    "DELETE FROM SavedTasks WHERE Id = $id;",
                    cancellationToken,
                    ("$id", task.TaskId.ToString("D")));
            }
            else
            {
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    "UPDATE SavedTasks SET Origin = 2, IsArchived = 0 WHERE Id = $id;",
                    cancellationToken,
                    ("$id", task.TaskId.ToString("D")));
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    "UPDATE ExternalTaskLinks SET State = 1, MappingId = NULL WHERE TaskId = $id AND Provider = 'Trello';",
                    cancellationToken,
                    ("$id", task.TaskId.ToString("D")));
            }
        }
    }

    private static Task InsertTrelloTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid taskId,
        Guid projectId,
        string name,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            connection,
            transaction,
            "INSERT INTO SavedTasks (Id, ProjectId, Name, IsArchived, Origin) VALUES ($id, $project, $name, 0, 1);",
            cancellationToken,
            ("$id", taskId.ToString("D")),
            ("$project", projectId.ToString("D")),
            ("$name", Required(name, nameof(name))));

    private static Task InsertExternalLinkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid mappingId,
        Guid taskId,
        TrelloCard card,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            INSERT INTO ExternalTaskLinks
                (Provider, ExternalId, TaskId, MappingId, BoardId, ListId, WebUrl, State, RemoteModifiedUtc)
            VALUES ('Trello', $card, $task, $mapping, $board, $list, $url, 0, $modified);
            """,
            cancellationToken,
            ("$card", Required(card.Id, nameof(card.Id))),
            ("$task", taskId.ToString("D")),
            ("$mapping", mappingId.ToString("D")),
            ("$board", Required(card.BoardId, nameof(card.BoardId))),
            ("$list", Required(card.ListId, nameof(card.ListId))),
            ("$url", card.Url?.Trim() ?? string.Empty),
            ("$modified", card.LastActivityUtc is { } modified ? Format(modified) : null));

    private static Task UpdateExternalLinkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid mappingId,
        Guid taskId,
        TrelloCard card,
        ExternalTaskLinkState state,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(
            connection,
            transaction,
            """
            UPDATE ExternalTaskLinks
            SET TaskId = $task,
                MappingId = $mapping,
                BoardId = $board,
                ListId = $list,
                WebUrl = $url,
                State = $state,
                RemoteModifiedUtc = $modified
            WHERE Provider = 'Trello' AND ExternalId = $card;
            """,
            cancellationToken,
            ("$task", taskId.ToString("D")),
            ("$mapping", mappingId.ToString("D")),
            ("$board", Required(card.BoardId, nameof(card.BoardId))),
            ("$list", Required(card.ListId, nameof(card.ListId))),
            ("$url", card.Url?.Trim() ?? string.Empty),
            ("$state", (int)state),
            ("$modified", card.LastActivityUtc is { } modified ? Format(modified) : null),
            ("$card", Required(card.Id, nameof(card.Id))));

    private sealed record StoredExternalLink(
        Guid? TaskId,
        string CardId,
        string BoardId,
        string ListId,
        Guid? MappingId,
        ExternalTaskLinkState State,
        long EntryCount);

    private async Task ExecuteBulkUpdateAsync(
        string table,
        IReadOnlyList<Guid> ids,
        IReadOnlyList<string> assignments,
        IReadOnlyList<(string Name, object? Value)> parameters,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0 || assignments.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var idParameters = ids.Select((_, index) => $"$id{index}").ToArray();
        command.CommandText =
            $"UPDATE {table} SET {string.Join(", ", assignments)} WHERE Id IN ({string.Join(", ", idParameters)});";
        AddParameters(command, parameters.ToArray());
        for (var index = 0; index < ids.Count; index++)
        {
            command.Parameters.AddWithValue(idParameters[index], ids[index].ToString("D"));
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    private static Guid[] NormalizeIds(IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ids
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
    }

    private static async Task SynchronizeTagsFromDescriptionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var descriptions = new List<(string Description, Guid ProjectId)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT Description, ProjectId FROM TimeEntries WHERE Description IS NOT NULL AND INSTR(Description, '#') > 0;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                descriptions.Add((reader.GetString(0), Guid.Parse(reader.GetString(1))));
            }
        }

        using var transaction = connection.BeginTransaction();
        foreach (var (description, projectId) in descriptions)
        {
            await EnsureTagsForDescriptionAsync(connection, transaction, description, projectId, cancellationToken);
        }

        transaction.Commit();
    }

    private static async Task EnsureTagsForDescriptionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? description,
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        var tags = TagParser.Extract(description);
        if (tags.Count == 0)
        {
            return;
        }

        projectId = NormalizeTagProjectId(projectId);

        var colors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Color FROM Tags;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                colors.Add(reader.GetString(0));
            }
        }

        foreach (var tag in tags)
        {
            var color = GenerateUniqueTagColor(colors);
            await ExecuteInTransactionAsync(
                connection,
                transaction,
                "INSERT OR IGNORE INTO Tags (Id, Name, Color, IsGlobal) VALUES ($id, $name, $color, $global);",
                cancellationToken,
                ("$id", Guid.NewGuid().ToString("D")),
                ("$name", tag),
                ("$color", color),
                ("$global", projectId is null ? 1 : 0));
            if (projectId is { } assignedProjectId)
            {
                await ExecuteInTransactionAsync(
                    connection,
                    transaction,
                    """
                    INSERT OR IGNORE INTO ProjectTags (TagId, ProjectId)
                    SELECT Id, $project FROM Tags
                    WHERE Name = $name AND IsGlobal = 0;
                    """,
                    cancellationToken,
                    ("$project", assignedProjectId.ToString("D")),
                    ("$name", tag));
            }
            colors.Add(color);
        }
    }

    private static string GenerateUniqueTagColor(HashSet<string> existingColors)
    {
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var hue = Random.Shared.NextDouble() * 360d;
            var saturation = 0.58d + Random.Shared.NextDouble() * 0.24d;
            var value = 0.78d + Random.Shared.NextDouble() * 0.17d;
            var color = HsvToHex(hue, saturation, value);
            if (!existingColors.Contains(color))
            {
                return color;
            }
        }

        for (var hue = 0; hue < 360; hue++)
        {
            var color = HsvToHex(hue, 0.72d, 0.9d);
            if (!existingColors.Contains(color))
            {
                return color;
            }
        }

        throw new InvalidOperationException("Could not allocate a unique tag color.");
    }

    private static string HsvToHex(double hueDegrees, double saturation, double value)
    {
        var sector = hueDegrees / 60d;
        var index = (int)Math.Floor(sector) % 6;
        var fraction = sector - Math.Floor(sector);
        var p = value * (1 - saturation);
        var q = value * (1 - fraction * saturation);
        var t = value * (1 - (1 - fraction) * saturation);
        var (red, green, blue) = index switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q),
        };
        return $"#{(byte)Math.Round(red * 255):X2}{(byte)Math.Round(green * 255):X2}{(byte)Math.Round(blue * 255):X2}";
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void AddParameters(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static TimeEntry MapTimeEntry(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        Parse(reader.GetString(4)),
        reader.IsDBNull(5) ? null : Parse(reader.GetString(5)),
        Parse(reader.GetString(6)),
        reader.GetBoolean(7),
        (TrackingSource)reader.GetInt32(8),
        Parse(reader.GetString(9)),
        Parse(reader.GetString(10)),
        reader.GetBoolean(11));

    private static SavedTask MapSavedTask(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.GetBoolean(3),
        (SavedTaskOrigin)reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5));

    private static TimeEntryView MapTimeEntryView(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        Parse(reader.GetString(7)),
        reader.IsDBNull(8) ? null : Parse(reader.GetString(8)),
        reader.GetInt64(9),
        reader.GetBoolean(10),
        (TrackingSource)reader.GetInt32(11),
        reader.GetBoolean(12),
        reader.IsDBNull(13) ? null : Convert.ToDecimal(reader.GetDouble(13), CultureInfo.InvariantCulture),
        reader.GetString(14),
        reader.GetString(15));

    private static Project MapProject(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetBoolean(4),
        reader.IsDBNull(5) ? null : reader.GetDouble(5),
        reader.IsDBNull(6) ? null : reader.GetDouble(6),
        reader.IsDBNull(7) ? null : reader.GetDouble(7),
        reader.IsDBNull(8) ? null : Convert.ToDecimal(reader.GetDouble(8), CultureInfo.InvariantCulture),
        reader.GetString(9),
        reader.GetBoolean(10));

    private static TagDefinition MapTagDefinition(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetBoolean(3),
        reader.IsDBNull(4)
            ? []
            : reader.GetString(4)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Guid.Parse)
                .Distinct()
                .ToArray());

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string NormalizeProcessName(string processName)
    {
        processName = Required(processName, nameof(processName));
        return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
    }

    private static Guid? NormalizeTagProjectId(Guid? projectId)
    {
        if (projectId is not { } id ||
            id == Guid.Empty ||
            id == SystemEntityIds.UnassignedProjectId ||
            id == SystemEntityIds.GlobalSoftwareScopeId ||
            id == SystemEntityIds.GlobalTagScopeId)
        {
            return null;
        }

        return id;
    }

    private static string NormalizeColor(string color)
    {
        color = string.IsNullOrWhiteSpace(color) ? "#E45C4A" : color.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
        {
            throw new ArgumentException("Color must be a six-digit hex value such as #E45C4A.", nameof(color));
        }

        return color.ToUpperInvariant();
    }

    private static double? NormalizeTarget(double? hours, string parameterName)
    {
        if (hours is null)
        {
            return null;
        }

        if (!double.IsFinite(hours.Value) || hours.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A target must be greater than zero hours.");
        }

        return Math.Round(hours.Value, 2);
    }

    private static (string Name, Guid? ProjectId, double TargetHours) NormalizeCustomTarget(
        string name,
        Guid? projectId,
        CustomTargetCadence cadence,
        double targetHours)
    {
        if (!Enum.IsDefined(cadence))
        {
            throw new ArgumentOutOfRangeException(nameof(cadence));
        }

        var normalizedHours = NormalizeTarget(targetHours, nameof(targetHours))
            ?? throw new ArgumentOutOfRangeException(nameof(targetHours));
        if (projectId == Guid.Empty)
        {
            projectId = null;
        }

        return (Required(name, nameof(name)), projectId, normalizedHours);
    }

    private sealed record ExistingTargetState(
        DateTimeOffset CreatedUtc,
        CustomTargetCadence Cadence,
        double TargetHours,
        DateTimeOffset? CompletedUtc,
        TargetDurationMetric DurationMetric);

    private static void ValidateTargetDurationMetric(TargetDurationMetric durationMetric)
    {
        if (!Enum.IsDefined(durationMetric))
        {
            throw new ArgumentOutOfRangeException(nameof(durationMetric));
        }
    }

    private static decimal? NormalizeRate(decimal? hourlyRate)
    {
        if (hourlyRate is null)
        {
            return null;
        }

        if (hourlyRate.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hourlyRate), "The hourly rate must be greater than zero.");
        }

        return decimal.Round(hourlyRate.Value, 2, MidpointRounding.AwayFromZero);
    }

    private static string NormalizeCurrency(string currency)
    {
        currency = Required(currency, nameof(currency)).ToUpperInvariant();
        if (currency is not ("PLN" or "USD" or "EUR"))
        {
            throw new ArgumentException("Currency must be PLN, USD, or EUR.", nameof(currency));
        }

        return currency;
    }

    private static void ValidateRange(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (endUtc <= startUtc)
        {
            throw new ArgumentException("End time must be after start time.");
        }
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string FormatLocalDate(DateTime value) => value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateTime ParseLocalDate(string value) => DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None);
    private static bool IsUpgradeRequired(long version) => version > 0 && version < SchemaVersion;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS Clients (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
            Color TEXT NOT NULL,
            IsArchived INTEGER NOT NULL DEFAULT 0 CHECK (IsArchived IN (0, 1))
        );

        CREATE TABLE IF NOT EXISTS Projects (
            Id TEXT PRIMARY KEY,
            ClientId TEXT NOT NULL REFERENCES Clients(Id),
            Name TEXT NOT NULL COLLATE NOCASE,
            Color TEXT NOT NULL,
            IsArchived INTEGER NOT NULL DEFAULT 0 CHECK (IsArchived IN (0, 1)),
            DailyTargetHours REAL NULL CHECK (DailyTargetHours IS NULL OR DailyTargetHours > 0),
            WeeklyTargetHours REAL NULL CHECK (WeeklyTargetHours IS NULL OR WeeklyTargetHours > 0),
            MonthlyTargetHours REAL NULL CHECK (MonthlyTargetHours IS NULL OR MonthlyTargetHours > 0),
            HourlyRate REAL NULL CHECK (HourlyRate IS NULL OR HourlyRate > 0),
            Currency TEXT NOT NULL DEFAULT 'PLN' CHECK (Currency IN ('PLN', 'USD', 'EUR')),
            CarryOverTargetDebtEnabled INTEGER NOT NULL DEFAULT 0 CHECK (CarryOverTargetDebtEnabled IN (0, 1)),
            UNIQUE (ClientId, Name)
        );

        CREATE TABLE IF NOT EXISTS SavedTasks (
            Id TEXT PRIMARY KEY,
            ProjectId TEXT NOT NULL REFERENCES Projects(Id),
            Name TEXT NOT NULL COLLATE NOCASE,
            IsArchived INTEGER NOT NULL DEFAULT 0 CHECK (IsArchived IN (0, 1)),
            Origin INTEGER NOT NULL DEFAULT 0 CHECK (Origin IN (0, 1, 2))
        );

        CREATE TABLE IF NOT EXISTS TrelloConnections (
            SingletonId INTEGER PRIMARY KEY CHECK (SingletonId = 1),
            MemberId TEXT NOT NULL,
            Username TEXT NOT NULL,
            DisplayName TEXT NOT NULL,
            LastSuccessfulSyncUtc TEXT NULL,
            LastError TEXT NULL,
            RequiresReconnect INTEGER NOT NULL DEFAULT 0 CHECK (RequiresReconnect IN (0, 1))
        );

        CREATE TABLE IF NOT EXISTS TrelloBoardMappings (
            Id TEXT PRIMARY KEY,
            ProjectId TEXT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            BoardId TEXT NOT NULL UNIQUE,
            BoardName TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS TrelloMappingLists (
            MappingId TEXT NOT NULL REFERENCES TrelloBoardMappings(Id) ON DELETE CASCADE,
            ListId TEXT NOT NULL,
            ListName TEXT NOT NULL,
            PRIMARY KEY (MappingId, ListId)
        );

        CREATE TABLE IF NOT EXISTS ExternalTaskLinks (
            Provider TEXT NOT NULL,
            ExternalId TEXT NOT NULL,
            TaskId TEXT NULL REFERENCES SavedTasks(Id) ON DELETE CASCADE,
            MappingId TEXT NULL REFERENCES TrelloBoardMappings(Id) ON DELETE SET NULL,
            BoardId TEXT NOT NULL,
            ListId TEXT NOT NULL,
            WebUrl TEXT NOT NULL,
            State INTEGER NOT NULL DEFAULT 0 CHECK (State IN (0, 1, 2)),
            RemoteModifiedUtc TEXT NULL,
            PRIMARY KEY (Provider, ExternalId),
            UNIQUE (TaskId)
        );

        CREATE TABLE IF NOT EXISTS Tags (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
            Color TEXT NOT NULL,
            IsGlobal INTEGER NOT NULL DEFAULT 0 CHECK (IsGlobal IN (0, 1))
        );

        CREATE TABLE IF NOT EXISTS ProjectTags (
            TagId TEXT NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE,
            ProjectId TEXT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            PRIMARY KEY (TagId, ProjectId)
        );
        CREATE INDEX IF NOT EXISTS IX_ProjectTags_ProjectId ON ProjectTags (ProjectId);

        CREATE TABLE IF NOT EXISTS Software (
            Id TEXT PRIMARY KEY,
            ProcessName TEXT NOT NULL COLLATE NOCASE UNIQUE,
            Label TEXT NOT NULL,
            IsExcluded INTEGER NOT NULL DEFAULT 0 CHECK (IsExcluded IN (0, 1)),
            IsHidden INTEGER NOT NULL DEFAULT 0 CHECK (IsHidden IN (0, 1)),
            IsGlobal INTEGER NOT NULL DEFAULT 0 CHECK (IsGlobal IN (0, 1))
        );

        CREATE TABLE IF NOT EXISTS SoftwareTags (
            SoftwareId TEXT NOT NULL REFERENCES Software(Id) ON DELETE CASCADE,
            TagId TEXT NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE,
            PRIMARY KEY (SoftwareId, TagId)
        );
        CREATE INDEX IF NOT EXISTS IX_SoftwareTags_TagId ON SoftwareTags (TagId);

        CREATE TABLE IF NOT EXISTS ProjectSoftwareSettings (
            ProjectId TEXT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            SoftwareId TEXT NOT NULL REFERENCES Software(Id) ON DELETE CASCADE,
            IsExcluded INTEGER NOT NULL DEFAULT 0 CHECK (IsExcluded IN (0, 1)),
            PRIMARY KEY (ProjectId, SoftwareId)
        );
        CREATE INDEX IF NOT EXISTS IX_ProjectSoftwareSettings_SoftwareId
            ON ProjectSoftwareSettings (SoftwareId);

        CREATE TABLE IF NOT EXISTS ProjectSoftwareTags (
            ProjectId TEXT NOT NULL,
            SoftwareId TEXT NOT NULL,
            TagId TEXT NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE,
            PRIMARY KEY (ProjectId, SoftwareId, TagId),
            FOREIGN KEY (ProjectId, SoftwareId)
                REFERENCES ProjectSoftwareSettings(ProjectId, SoftwareId)
                ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS IX_ProjectSoftwareTags_TagId
            ON ProjectSoftwareTags (TagId);

        CREATE TABLE IF NOT EXISTS RecognitionRules (
            Id TEXT PRIMARY KEY,
            ProjectId TEXT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            TitlePattern TEXT NOT NULL COLLATE NOCASE,
            ProcessName TEXT NULL COLLATE NOCASE,
            IsEnabled INTEGER NOT NULL DEFAULT 1 CHECK (IsEnabled IN (0, 1)),
            UNIQUE (ProjectId, TitlePattern, ProcessName)
        );

        CREATE TABLE IF NOT EXISTS TimeEntries (
            Id TEXT PRIMARY KEY,
            ProjectId TEXT NOT NULL REFERENCES Projects(Id),
            TaskId TEXT NULL REFERENCES SavedTasks(Id),
            Description TEXT NULL,
            StartUtc TEXT NOT NULL,
            EndUtc TEXT NULL,
            LastCheckpointUtc TEXT NOT NULL,
            DetailsPending INTEGER NOT NULL DEFAULT 1 CHECK (DetailsPending IN (0, 1)),
            Source INTEGER NOT NULL,
            CreatedUtc TEXT NOT NULL,
            ModifiedUtc TEXT NOT NULL,
            IsPaid INTEGER NOT NULL DEFAULT 0 CHECK (IsPaid IN (0, 1)),
            CHECK (EndUtc IS NULL OR EndUtc >= StartUtc)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS UX_TimeEntries_OneRunning
            ON TimeEntries ((1)) WHERE EndUtc IS NULL;
        CREATE INDEX IF NOT EXISTS IX_TimeEntries_StartUtc ON TimeEntries (StartUtc);
        CREATE INDEX IF NOT EXISTS IX_TimeEntries_ProjectId ON TimeEntries (ProjectId);

        CREATE TABLE IF NOT EXISTS GoogleSheetsEntryDeletions (
            EntryId TEXT PRIMARY KEY,
            DeletedUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS TimeEntrySoftware (
            TimeEntryId TEXT NOT NULL REFERENCES TimeEntries(Id) ON DELETE CASCADE,
            SoftwareId TEXT NOT NULL REFERENCES Software(Id) ON DELETE CASCADE,
            PRIMARY KEY (TimeEntryId, SoftwareId)
        );
        CREATE INDEX IF NOT EXISTS IX_TimeEntrySoftware_SoftwareId ON TimeEntrySoftware (SoftwareId);

        CREATE TABLE IF NOT EXISTS TimeExclusions (
            Id TEXT PRIMARY KEY,
            TimeEntryId TEXT NOT NULL REFERENCES TimeEntries(Id) ON DELETE CASCADE,
            StartUtc TEXT NOT NULL,
            EndUtc TEXT NOT NULL,
            Reason TEXT NOT NULL,
            CHECK (EndUtc > StartUtc)
        );

        CREATE TABLE IF NOT EXISTS CustomTargets (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL COLLATE NOCASE,
            ProjectId TEXT NULL REFERENCES Projects(Id),
            Cadence INTEGER NOT NULL CHECK (Cadence IN (0, 1, 2, 3)),
            TargetHours REAL NOT NULL CHECK (TargetHours > 0),
            CreatedUtc TEXT NOT NULL,
            ModifiedUtc TEXT NOT NULL,
            CompletedUtc TEXT NULL,
            DurationMetric INTEGER NOT NULL DEFAULT 0 CHECK (DurationMetric IN (0, 1)),
            CHECK (Cadence = 3 OR CompletedUtc IS NULL)
        );
        CREATE INDEX IF NOT EXISTS IX_CustomTargets_ProjectId ON CustomTargets (ProjectId);

        CREATE TABLE IF NOT EXISTS ProjectTargetDebtCancellations (
            Id TEXT PRIMARY KEY,
            ProjectId TEXT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            CanceledSeconds INTEGER NOT NULL CHECK (CanceledSeconds > 0),
            CanceledUtc TEXT NOT NULL,
            RestoredUtc TEXT NULL,
            CHECK (RestoredUtc IS NULL OR RestoredUtc >= CanceledUtc)
        );
        CREATE INDEX IF NOT EXISTS IX_ProjectTargetDebtCancellations_ProjectId
            ON ProjectTargetDebtCancellations (ProjectId, RestoredUtc, CanceledUtc);

        CREATE TABLE IF NOT EXISTS Settings (
            Key TEXT PRIMARY KEY COLLATE NOCASE,
            Value TEXT NOT NULL
        );
        """;

    private const string MigrationV2Sql = """
        ALTER TABLE Projects ADD COLUMN WeeklyTargetHours REAL NULL CHECK (WeeklyTargetHours IS NULL OR WeeklyTargetHours > 0);
        ALTER TABLE Projects ADD COLUMN MonthlyTargetHours REAL NULL CHECK (MonthlyTargetHours IS NULL OR MonthlyTargetHours > 0);
        ALTER TABLE Projects ADD COLUMN HourlyRate REAL NULL CHECK (HourlyRate IS NULL OR HourlyRate > 0);
        ALTER TABLE Projects ADD COLUMN Currency TEXT NOT NULL DEFAULT 'PLN' CHECK (Currency IN ('PLN', 'USD', 'EUR'));
        ALTER TABLE TimeEntries ADD COLUMN IsPaid INTEGER NOT NULL DEFAULT 0 CHECK (IsPaid IN (0, 1));
        """;

    private const string MigrationV4Sql = """
        ALTER TABLE Projects ADD COLUMN DailyTargetHours REAL NULL CHECK (DailyTargetHours IS NULL OR DailyTargetHours > 0);
        """;

    private const string MigrationV8Sql = """
        ALTER TABLE Software ADD COLUMN IsExcluded INTEGER NOT NULL DEFAULT 0 CHECK (IsExcluded IN (0, 1));
        """;

    private const string MigrationV10Sql = """
        INSERT OR IGNORE INTO ProjectSoftwareSettings (ProjectId, SoftwareId, IsExcluded)
        SELECT p.Id, s.Id, s.IsExcluded
        FROM Projects p
        CROSS JOIN Software s
        WHERE s.IsExcluded = 1
           OR EXISTS (SELECT 1 FROM SoftwareTags st WHERE st.SoftwareId = s.Id);

        INSERT OR IGNORE INTO ProjectSoftwareTags (ProjectId, SoftwareId, TagId)
        SELECT p.Id, st.SoftwareId, st.TagId
        FROM Projects p
        CROSS JOIN SoftwareTags st;
        """;

    private const string MigrationV11Sql = """
        ALTER TABLE Software ADD COLUMN IsHidden INTEGER NOT NULL DEFAULT 0 CHECK (IsHidden IN (0, 1));
        """;

    private const string MigrationV12Sql = """
        ALTER TABLE Software ADD COLUMN IsGlobal INTEGER NOT NULL DEFAULT 0 CHECK (IsGlobal IN (0, 1));
        """;

    private const string MigrationV13Sql = """
        ALTER TABLE Tags ADD COLUMN IsGlobal INTEGER NOT NULL DEFAULT 1 CHECK (IsGlobal IN (0, 1));
        """;

    private const string MigrationV14Sql = """
        DELETE FROM TimeEntrySoftware
        WHERE SoftwareId IN (SELECT Id FROM Software WHERE IsHidden = 1);
        """;

    private const string MigrationV15Sql = """
        ALTER TABLE Projects ADD COLUMN CarryOverTargetDebtEnabled INTEGER NOT NULL DEFAULT 0
            CHECK (CarryOverTargetDebtEnabled IN (0, 1));
        """;

    private const string MigrationV16Sql = """
        CREATE TABLE IF NOT EXISTS CustomTargets (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL COLLATE NOCASE,
            ProjectId TEXT NULL REFERENCES Projects(Id),
            Cadence INTEGER NOT NULL CHECK (Cadence IN (0, 1, 2, 3)),
            TargetHours REAL NOT NULL CHECK (TargetHours > 0),
            OneTimeDate TEXT NULL,
            CreatedUtc TEXT NOT NULL,
            ModifiedUtc TEXT NOT NULL,
            CHECK ((Cadence = 3 AND OneTimeDate IS NOT NULL) OR (Cadence <> 3 AND OneTimeDate IS NULL))
        );
        CREATE INDEX IF NOT EXISTS IX_CustomTargets_ProjectId ON CustomTargets (ProjectId);
        """;

    private const string MigrationV17Sql = """
        CREATE TABLE IF NOT EXISTS ProjectTargetDebtCancellations (
            Id TEXT PRIMARY KEY,
            ProjectId TEXT NOT NULL REFERENCES Projects(Id) ON DELETE CASCADE,
            CanceledSeconds INTEGER NOT NULL CHECK (CanceledSeconds > 0),
            CanceledUtc TEXT NOT NULL,
            RestoredUtc TEXT NULL,
            CHECK (RestoredUtc IS NULL OR RestoredUtc >= CanceledUtc)
        );
        CREATE INDEX IF NOT EXISTS IX_ProjectTargetDebtCancellations_ProjectId
            ON ProjectTargetDebtCancellations (ProjectId, RestoredUtc, CanceledUtc);
        """;

    private const string MigrationV19Sql = """
        DELETE FROM TimeEntries
        WHERE ProjectId IN (
            SELECT p.Id
            FROM Projects p
            JOIN Clients c ON c.Id = p.ClientId
            WHERE p.IsArchived = 1 OR c.IsArchived = 1
        );

        DELETE FROM CustomTargets
        WHERE ProjectId IN (
            SELECT p.Id
            FROM Projects p
            JOIN Clients c ON c.Id = p.ClientId
            WHERE p.IsArchived = 1 OR c.IsArchived = 1
        );

        DELETE FROM SavedTasks
        WHERE ProjectId IN (
            SELECT p.Id
            FROM Projects p
            JOIN Clients c ON c.Id = p.ClientId
            WHERE p.IsArchived = 1 OR c.IsArchived = 1
        );

        DELETE FROM Projects
        WHERE IsArchived = 1
           OR ClientId IN (SELECT Id FROM Clients WHERE IsArchived = 1);

        DELETE FROM Clients
        WHERE IsArchived = 1;

        DELETE FROM Tags
        WHERE IsGlobal = 0
          AND NOT EXISTS (
              SELECT 1
              FROM ProjectTags
              WHERE ProjectTags.TagId = Tags.Id
          );
        """;

    private const string MigrationV20Sql = """
        ALTER TABLE CustomTargets RENAME TO CustomTargetsBeforeV20;

        CREATE TABLE CustomTargets (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL COLLATE NOCASE,
            ProjectId TEXT NULL REFERENCES Projects(Id),
            Cadence INTEGER NOT NULL CHECK (Cadence IN (0, 1, 2, 3)),
            TargetHours REAL NOT NULL CHECK (TargetHours > 0),
            CreatedUtc TEXT NOT NULL,
            ModifiedUtc TEXT NOT NULL,
            CompletedUtc TEXT NULL,
            CHECK (Cadence = 3 OR CompletedUtc IS NULL)
        );

        INSERT INTO CustomTargets
            (Id, Name, ProjectId, Cadence, TargetHours, CreatedUtc, ModifiedUtc, CompletedUtc)
        SELECT Id, Name, ProjectId, Cadence, TargetHours, CreatedUtc, ModifiedUtc, NULL
        FROM CustomTargetsBeforeV20;

        DROP TABLE CustomTargetsBeforeV20;
        CREATE INDEX IX_CustomTargets_ProjectId ON CustomTargets (ProjectId);
        """;

    private const string MigrationV21Sql = """
        PRAGMA foreign_keys = OFF;

        CREATE TABLE SavedTasksV21 (
            Id TEXT PRIMARY KEY,
            ProjectId TEXT NOT NULL REFERENCES Projects(Id),
            Name TEXT NOT NULL COLLATE NOCASE,
            IsArchived INTEGER NOT NULL DEFAULT 0 CHECK (IsArchived IN (0, 1)),
            Origin INTEGER NOT NULL DEFAULT 0 CHECK (Origin IN (0, 1, 2))
        );

        INSERT INTO SavedTasksV21 (Id, ProjectId, Name, IsArchived, Origin)
        SELECT Id, ProjectId, Name, IsArchived, 0 FROM SavedTasks;

        DROP TABLE SavedTasks;
        ALTER TABLE SavedTasksV21 RENAME TO SavedTasks;

        PRAGMA foreign_keys = ON;
        """;

    private const string MigrationV23Sql = """
        ALTER TABLE CustomTargets
        ADD COLUMN DurationMetric INTEGER NOT NULL DEFAULT 0 CHECK (DurationMetric IN (0, 1));
        """;

    private const string SchemaV21IndexesSql = """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_SavedTasks_LocalName
            ON SavedTasks (ProjectId, Name COLLATE NOCASE)
            WHERE Origin = 0;
        CREATE INDEX IF NOT EXISTS IX_ExternalTaskLinks_TaskId
            ON ExternalTaskLinks (TaskId);
        CREATE INDEX IF NOT EXISTS IX_ExternalTaskLinks_BoardId
            ON ExternalTaskLinks (Provider, BoardId, State);
        CREATE INDEX IF NOT EXISTS IX_TrelloBoardMappings_ProjectId
            ON TrelloBoardMappings (ProjectId);
        """;
}
