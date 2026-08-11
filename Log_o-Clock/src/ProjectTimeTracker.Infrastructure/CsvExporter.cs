using System.Globalization;
using System.Text;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Infrastructure;

public static class CsvExporter
{
    public static async Task ExportAsync(
        string path,
        IReadOnlyList<TimeEntryView> entries,
        TimeZoneInfo timeZone,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(timeZone);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("Date,Start,End,Duration,Client,Project,Task,Description,Tags,Software,Paid,HourlyRate,Currency,Amount,Source".AsMemory(), cancellationToken);

        foreach (var entry in entries)
        {
            var start = TimeZoneInfo.ConvertTime(entry.StartUtc, timeZone);
            var end = TimeZoneInfo.ConvertTime(entry.EndUtc ?? nowUtc, timeZone);
            var duration = TimeSpan.FromSeconds(entry.NetDurationSeconds(nowUtc));
            var amount = entry.HourlyRate is null
                ? string.Empty
                : (entry.HourlyRate.Value * (decimal)duration.TotalHours).ToString("0.00", CultureInfo.InvariantCulture);
            var fields = new[]
            {
                start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                start.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                end.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}",
                entry.ClientName,
                entry.ProjectName,
                entry.TaskName ?? string.Empty,
                entry.Description ?? string.Empty,
                string.Join(' ', TagParser.Extract(entry.Description).Select(tag => $"#{tag}")),
                entry.SoftwareLabels,
                entry.IsPaid ? "Yes" : "No",
                entry.HourlyRate?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
                entry.Currency,
                amount,
                entry.Source.ToString(),
            };
            await writer.WriteLineAsync(string.Join(',', fields.Select(Escape)).AsMemory(), cancellationToken);
        }
    }

    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
