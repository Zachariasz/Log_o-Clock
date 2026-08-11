using System.Text;
using ProjectTimeTracker.Core;
using ProjectTimeTracker.Infrastructure;

namespace ProjectTimeTracker.Tests;

public sealed class CsvExporterTests
{
    [Fact]
    public async Task CsvIsUtf8WithBomAndEscapesUserText()
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-time-{Guid.NewGuid():N}.csv");
        try
        {
            var start = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
            var entry = new TimeEntryView(
                Guid.NewGuid(), Guid.NewGuid(), null, "Acme, Inc.", "Phoenix", null,
                "Review \"landing page\"", start, start.AddMinutes(30), 0, false, TrackingSource.Manual,
                SoftwareLabels: "Blender 4.5");
            await CsvExporter.ExportAsync(path, [entry], TimeZoneInfo.Utc, start.AddHours(1));
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Contains("\"Acme, Inc.\"", text, StringComparison.Ordinal);
            Assert.Contains("\"Review \"\"landing page\"\"\"", text, StringComparison.Ordinal);
            Assert.Contains("Tags,Software,Paid,HourlyRate,Currency,Amount", text, StringComparison.Ordinal);
            Assert.Contains("\"Blender 4.5\"", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
