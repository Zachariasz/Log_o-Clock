using System.Net;
using System.Text;
using ProjectTimeTracker.Core;
using ProjectTimeTracker.Infrastructure;

namespace ProjectTimeTracker.Tests;

public sealed class GitHubReleaseClientTests
{
    [Fact]
    public async Task LatestStableReleaseUsesTheRepositoryEndpointAndGitHubReleasePage()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            "{\"tag_name\":\"v1.143.0\",\"html_url\":\"https://github.com/Zachariasz/Log_o-Clock/releases/tag/v1.143.0\",\"draft\":false,\"prerelease\":false}");
        using var client = new GitHubReleaseClient(new HttpClient(handler));

        var release = await client.GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.Equal("v1.143.0", release.TagName);
        Assert.Equal("github.com", release.ReleasePageUri.Host);
        Assert.Equal("https://api.github.com/repos/Zachariasz/Log_o-Clock/releases/latest", handler.RequestUri);
        Assert.Contains("LogOClock", handler.UserAgent);
    }

    [Fact]
    public async Task MissingReleaseReturnsNull()
    {
        using var client = new GitHubReleaseClient(new HttpClient(new StubHandler(HttpStatusCode.NotFound, "{}")));

        Assert.Null(await client.GetLatestReleaseAsync());
    }

    [Theory]
    [InlineData("{\"tag_name\":\"v1.143.0\",\"html_url\":\"https://github.com/Zachariasz/Log_o-Clock/releases/tag/v1.143.0\",\"draft\":true,\"prerelease\":false}")]
    [InlineData("{\"tag_name\":\"v1.143.0\",\"html_url\":\"https://github.com/Zachariasz/Log_o-Clock/releases/tag/v1.143.0\",\"draft\":false,\"prerelease\":true}")]
    [InlineData("{\"tag_name\":\"v1.143.0\",\"html_url\":\"https://example.test/release\",\"draft\":false,\"prerelease\":false}")]
    [InlineData("{\"tag_name\":\"v1.143.0\",\"draft\":false,\"prerelease\":false}")]
    public async Task InvalidOrNonStableReleaseResponseIsRejected(string response)
    {
        using var client = new GitHubReleaseClient(new HttpClient(new StubHandler(HttpStatusCode.OK, response)));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestReleaseAsync());
    }

    [Theory]
    [InlineData("v1.143.0", true)]
    [InlineData("1.143.0", true)]
    [InlineData("v1.143", false)]
    [InlineData("v1.143.0.1", false)]
    [InlineData("v1.143.0-beta", false)]
    public void ReleaseVersionParsingRequiresThreeStableNumericParts(string value, bool expected)
    {
        Assert.Equal(expected, UpdateCheckSettings.TryParseReleaseVersion(value, out _));
    }

    private sealed class StubHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            RequestUri = request.RequestUri?.AbsoluteUri;
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }
}
