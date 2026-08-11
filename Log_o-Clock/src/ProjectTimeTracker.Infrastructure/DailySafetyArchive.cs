using Microsoft.Data.Sqlite;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Infrastructure;

internal static class DailySafetyArchive
{
    internal const string DailyLogsDirectoryName = "Daily Logs";
    internal const string DailyBackupsDirectoryName = "Daily Backups";

    public static string GetLogFileName(DateOnly date) =>
        $"TimeTracker-Logs-{date:yyyy-MM-dd}.csv";

    public static string GetBackupFileName(DateOnly date) =>
        $"TimeTracker-Backup-{date:yyyy-MM-dd}.db";

    public static string GetFirstBackupFileName(DateOnly date) =>
        $"TimeTracker-Backup-{date:yyyy-MM-dd}-first.db";

    public static async Task WriteLogAsync(
        string rootDirectory,
        DateOnly date,
        IReadOnlyList<TimeEntryView> entries,
        TimeZoneInfo timeZone,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(rootDirectory, DailyLogsDirectoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, GetLogFileName(date));
        var candidate = path + $".{Guid.NewGuid():N}.candidate";
        try
        {
            await MonthlyLogWriter.WriteAsync(candidate, entries, timeZone, cancellationToken);
            if (File.Exists(path) && await FilesMatchAsync(path, candidate, cancellationToken))
            {
                File.Delete(candidate);
                return;
            }

            // A past day's export is immutable history. If an edit changes it, retain the
            // previous representation before publishing the revised file.
            if (date < today && File.Exists(path))
            {
                var revisionDirectory = Path.Combine(directory, "Revisions", date.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(revisionDirectory);
                var revisionPath = Path.Combine(
                    revisionDirectory,
                    $"TimeTracker-Logs-{date:yyyy-MM-dd}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.csv");
                File.Copy(path, revisionPath, overwrite: false);
            }

            File.Move(candidate, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    public static async Task CreateDatabaseSnapshotsAsync(
        string databasePath,
        string connectionString,
        string rootDirectory,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(rootDirectory, DailyBackupsDirectoryName);
        Directory.CreateDirectory(directory);
        var currentPath = Path.Combine(directory, GetBackupFileName(date));
        var firstPath = Path.Combine(directory, GetFirstBackupFileName(date));
        var temporaryPath = currentPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using var source = new SqliteConnection(connectionString);
            await source.OpenAsync(cancellationToken);
            var destinationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();
            await using (var destination = new SqliteConnection(destinationConnectionString))
            {
                await destination.OpenAsync(cancellationToken);
                source.BackupDatabase(destination);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, currentPath, overwrite: true);
            if (!File.Exists(firstPath))
            {
                try
                {
                    File.Copy(currentPath, firstPath, overwrite: false);
                }
                catch (IOException) when (File.Exists(firstPath))
                {
                    // Another in-process save won the one-time first-snapshot race.
                }
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        _ = databasePath; // Kept explicit in the contract for diagnostics and future restore UI.
    }

    private static async Task<bool> FilesMatchAsync(
        string leftPath,
        string rightPath,
        CancellationToken cancellationToken)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        const int bufferSize = 81920;
        var leftBuffer = new byte[bufferSize];
        var rightBuffer = new byte[bufferSize];
        await using var left = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, true);
        await using var right = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, true);
        while (true)
        {
            var leftRead = await left.ReadAsync(leftBuffer, cancellationToken);
            var rightRead = await right.ReadAsync(rightBuffer, cancellationToken);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }
        }
    }
}
