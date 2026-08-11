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
        Assert.Contains("values:batchUpdate", handler.RequestUris[2]);
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
                    "{\"valueRanges\":[{\"range\":\"'2026-08-06'!A:Q\",\"values\":[[\"EntryId\"]]},{\"range\":\"'2026-08-07'!A:Q\"}]}"));
            }

            return Task.FromResult(Response(HttpStatusCode.OK, "{}"));
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string content) =>
        new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
}
