using System.Net;
using System.Text;
using ProjectTimeTracker.Infrastructure;

namespace ProjectTimeTracker.Tests;

public sealed class GoogleSheetsApiClientTests
{
    [Fact]
    public async Task RateLimitedRequestRetriesUsingServerGuidance()
    {
        var handler = new RateLimitThenSuccessHandler();
        using var client = new GoogleSheetsApiClient(new HttpClient(handler));

        var spreadsheet = await client.CreateSpreadsheetAsync("access", "Log O'clock");

        Assert.Equal("sheet-1", spreadsheet.Id);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ForbiddenResponseIncludesSafeGoogleReason()
    {
        const string response =
            """
            {"error":{"code":403,"message":"Google Sheets API has not been used in this project.","status":"PERMISSION_DENIED"}}
            """;
        using var client = new GoogleSheetsApiClient(
            new HttpClient(new StubHandler(HttpStatusCode.Forbidden, response)));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CreateSpreadsheetAsync("access", "Log O'clock"));

        Assert.Contains("PERMISSION_DENIED", exception.Message);
        Assert.Contains("Google Sheets API has not been used", exception.Message);
        Assert.DoesNotContain("access", exception.Message);
    }

    [Fact]
    public async Task DailyWorksheetOperationsUseOneRequestPerBatch()
    {
        var handler = new WorksheetBatchHandler();
        using var client = new GoogleSheetsApiClient(new HttpClient(handler));
        var names = new[] { "2026-08-06", "2026-08-07" };

        await client.AddWorksheetsAsync("access", "sheet-1", names);
        var rows = await client.ReadWorksheetsAsync("access", "sheet-1", names);
        await client.WriteWorksheetsAsync(
            "access",
            "sheet-1",
            names.ToDictionary(
                name => name,
                name => (IReadOnlyList<IReadOnlyList<object?>>)[["EntryId"], [name]],
                StringComparer.Ordinal));

        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(["EntryId"], Assert.Single(rows["2026-08-06"]));
        Assert.Empty(rows["2026-08-07"]);
        Assert.Contains("values:batchGet", handler.RequestUris[1]);
        Assert.Contains("A%3AT", handler.RequestUris[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("values:batchUpdate", handler.RequestUris[2]);
    }

    [Fact]
    public async Task TechnicalJournalOperationsUseHiddenAppendIncrementalReadAndTargetedWrite()
    {
        var handler = new TechnicalWorksheetHandler();
        using var client = new GoogleSheetsApiClient(new HttpClient(handler));

        await client.AddHiddenWorksheetsAsync("secret-access-token", "sheet-1", ["__LogOClockChanges"]);
        await client.AppendRowsAsync(
            "secret-access-token",
            "sheet-1",
            "'__LogOClockChanges'!A:J",
            [["revision-1", "TimeEntries"]]);
        var rows = await client.ReadRangeAsync(
            "secret-access-token",
            "sheet-1",
            "'__LogOClockChanges'!A2:J");
        await client.WriteRangeAsync(
            "secret-access-token",
            "sheet-1",
            "'__LogOClockDevices'!A2:I2",
            [["device-1", "Laptop"]]);

        Assert.Equal(["revision-1", "TimeEntries"], Assert.Single(rows));
        Assert.Contains("\"hidden\":true", handler.RequestBodies[0]);
        Assert.Contains(":append", handler.RequestUris[1]);
        Assert.Contains("insertDataOption=INSERT_ROWS", handler.RequestUris[1]);
        Assert.Contains("A2%3AJ", handler.RequestUris[2], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpMethod.Put, handler.Methods[3]);
        Assert.All(handler.AuthorizationValues, value => Assert.Equal("Bearer secret-access-token", value));
        Assert.DoesNotContain(handler.RequestBodies, body => body.Contains("secret-access-token", StringComparison.Ordinal));
    }

    private sealed class StubHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromResult(Response(status, content));
        }
    }

    private sealed class RateLimitThenSuccessHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            RequestCount++;
            if (RequestCount == 1)
            {
                var limited = Response(HttpStatusCode.TooManyRequests, "{}");
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(limited);
            }

            return Task.FromResult(Response(
                HttpStatusCode.OK,
                "{\"spreadsheetId\":\"sheet-1\",\"spreadsheetUrl\":\"https://docs.google.com/spreadsheets/d/sheet-1/edit\"}"));
        }
    }

    private sealed class WorksheetBatchHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            RequestCount++;
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            RequestUris.Add(uri);
            if (uri.Contains("values:batchGet", StringComparison.Ordinal))
            {
                return Task.FromResult(Response(
                    HttpStatusCode.OK,
                    "{\"valueRanges\":[{\"range\":\"'2026-08-06'!A:T\",\"values\":[[\"EntryId\"]]},{\"range\":\"'2026-08-07'!A:T\"}]}"));
            }

            return Task.FromResult(Response(HttpStatusCode.OK, "{}"));
        }
    }

    private sealed class TechnicalWorksheetHandler : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = [];
        public List<string> RequestBodies { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        public List<string> AuthorizationValues { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            RequestUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            AuthorizationValues.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            var content = request.Method == HttpMethod.Get
                ? "{\"values\":[[\"revision-1\",\"TimeEntries\"]]}"
                : "{}";
            return Response(HttpStatusCode.OK, content);
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string content) =>
        new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
}
