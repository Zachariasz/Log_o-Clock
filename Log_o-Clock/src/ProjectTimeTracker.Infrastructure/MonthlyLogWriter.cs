using System.Globalization;
using System.Text;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Infrastructure;

internal static class MonthlyLogWriter
{
    internal const string FilePrefix = "TimeTracker-Logs-";

    public static string GetFileName(int year, int month) =>
        $"{FilePrefix}{year:0000}-{month:00}.csv";

    public static async Task WriteAsync(
        string path,
        IReadOnlyList<TimeEntryView> entries,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(timeZone);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                await writer.WriteLineAsync(
                    "EntryId,Date,Start,End,Duration,Client,Project,Task,Description,Tags,Software,Paid,PendingDetails,HourlyRate,Currency,Amount,Source"
                        .AsMemory(),
                    cancellationToken);

                foreach (var entry in entries.OrderBy(item => item.StartUtc))
                {
                    var start = TimeZoneInfo.ConvertTime(entry.StartUtc, timeZone);
                    var end = entry.EndUtc is null
                        ? (DateTimeOffset?)null
                        : TimeZoneInfo.ConvertTime(entry.EndUtc.Value, timeZone);
                    var duration = entry.EndUtc is null
                        ? string.Empty
                        : FormatDuration(TimeSpan.FromSeconds(entry.NetDurationSeconds(entry.EndUtc.Value)));
                    var amount = entry.EndUtc is null || entry.HourlyRate is null
                        ? string.Empty
                        : (entry.HourlyRate.Value * (decimal)TimeSpan.FromSeconds(entry.NetDurationSeconds(entry.EndUtc.Value)).TotalHours)
                            .ToString("0.00", CultureInfo.InvariantCulture);
                    var fields = new[]
                    {
                        entry.Id.ToString("D"),
                        start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        start.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                        end?.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? string.Empty,
                        duration,
                        entry.ClientName,
                        entry.ProjectName,
                        entry.TaskName ?? string.Empty,
                        entry.Description ?? string.Empty,
                        string.Join(' ', TagParser.Extract(entry.Description).Select(tag => $"#{tag}")),
                        entry.SoftwareLabels,
                        entry.IsPaid ? "Yes" : "No",
                        entry.DetailsPending ? "Yes" : "No",
                        entry.HourlyRate?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
                        entry.Currency,
                        amount,
                        entry.Source.ToString(),
                    };
                    await writer.WriteLineAsync(string.Join(',', fields.Select(Escape)).AsMemory(), cancellationToken);
                }

                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string FormatDuration(TimeSpan duration) =>
        $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
