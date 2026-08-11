using System.Net;
using System.Text;
using ProjectTimeTracker.Core;
using ProjectTimeTracker.Infrastructure;

namespace ProjectTimeTracker.Tests;

public sealed class TrelloApiClientTests
{
    [Fact]
    public void AuthorizationUriRequestsPermanentReadOnlyToken()
    {
        using var client = new TrelloApiClient(new HttpClient(new StubHandler("{}"))
        {
            BaseAddress = new Uri("https://api.trello.com/1/"),
        });

        var uri = client.CreateAuthorizationUri("personal-key");

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("trello.com", uri.Host);
        Assert.Contains("scope=read", uri.Query);
        Assert.Contains("expiration=never", uri.Query);
        Assert.Contains("response_type=token", uri.Query);
        Assert.Contains("name=Log O'clock", Uri.UnescapeDataString(uri.Query));
        Assert.DoesNotContain("write", uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CardResponseReadsOnlyTaskIdentityAndAssignmentFields()
    {
        const string json =
            """
            [{"id":"card","idBoard":"board","idList":"list","name":"Animation","url":"https://trello.com/c/card","idMembers":["me"],"dateLastActivity":"2026-08-01T10:00:00Z","desc":"must not be stored","labels":[{"name":"ignored"}]}]
            """;
        var handler = new StubHandler(json);
        using var client = new TrelloApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.trello.com/1/"),
        });

        var card = Assert.Single(await client.GetCardsAsync(
            new TrelloCredentials("key", "secret-token"),
            "board"));

        Assert.Equal("Animation", card.Name);
        Assert.Equal(["me"], card.MemberIds);
        Assert.NotNull(handler.LastAuthorization);
        Assert.Contains("oauth_consumer_key", handler.LastAuthorization);
        Assert.Contains("oauth_token", handler.LastAuthorization);
        Assert.DoesNotContain("secret-token", handler.LastRequestUri ?? string.Empty);
    }

    [Fact]
    public async Task UnauthorizedResponseBecomesReconnectError()
    {
        using var client = new TrelloApiClient(new HttpClient(new StubHandler("{}", HttpStatusCode.Unauthorized))
        {
            BaseAddress = new Uri("https://api.trello.com/1/"),
        });

        await Assert.ThrowsAsync<TrelloAuthenticationException>(() =>
            client.GetCurrentMemberAsync(new TrelloCredentials("key", "token")));
    }

    [Fact]
    public async Task RateLimitedRequestRetriesUsingServerGuidance()
    {
        var handler = new RateLimitThenSuccessHandler();
        using var client = new TrelloApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.trello.com/1/"),
        });

        var member = await client.GetCurrentMemberAsync(new TrelloCredentials("key", "token"));

        Assert.Equal("member", member.Id);
        Assert.Equal(2, handler.RequestCount);
    }

    private sealed class StubHandler(string response, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? LastAuthorization { get; private set; }
        public string? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastRequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
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
                var limited = new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(limited);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"member\",\"username\":\"user\",\"fullName\":\"Trello User\"}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
