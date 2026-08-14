using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Infrastructure;

public sealed class GitHubReleaseClient : IGitHubReleaseClient, IDisposable
{
    private static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/Zachariasz/Log_o-Clock/releases/latest");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public GitHubReleaseClient(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("LogOClock", "1.0"));
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseUri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !IsFalse(root, "draft") ||
            !IsFalse(root, "prerelease") ||
            !root.TryGetProperty("tag_name", out var tagElement) ||
            tagElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("html_url", out var releasePageElement) ||
            releasePageElement.ValueKind != JsonValueKind.String ||
            !UpdateCheckSettings.TryParseGitHubReleasePageUri(releasePageElement.GetString(), out var releasePageUri))
        {
            throw new InvalidDataException("GitHub returned an invalid release response.");
        }

        return new GitHubRelease(tagElement.GetString()!, releasePageUri!);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static bool IsFalse(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;
}
