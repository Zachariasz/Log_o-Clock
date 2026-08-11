using Microsoft.Data.Sqlite;

namespace ProjectTimeTracker.Infrastructure;

public static class SqliteDatabaseMigrator
{
    public static async Task CopyIfTargetMissingAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        sourcePath = Path.GetFullPath(sourcePath);
        targetPath = Path.GetFullPath(targetPath);
        if (!File.Exists(sourcePath) || File.Exists(targetPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var temporaryPath = targetPath + $".{Guid.NewGuid():N}.migration";
        try
        {
            await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
            await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
            await source.OpenAsync(cancellationToken);
            await target.OpenAsync(cancellationToken);
            source.BackupDatabase(target);

            await using (var integrityCommand = target.CreateCommand())
            {
                integrityCommand.CommandText = "PRAGMA integrity_check;";
                var integrity = Convert.ToString(await integrityCommand.ExecuteScalarAsync(cancellationToken));
                if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"The migrated time-tracker database failed its integrity check: {integrity}");
                }
            }

            await target.CloseAsync();
            await source.CloseAsync();
            File.Move(temporaryPath, targetPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
