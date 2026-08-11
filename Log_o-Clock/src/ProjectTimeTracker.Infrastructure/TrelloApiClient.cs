using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Infrastructure;

public sealed class TrelloApiClient : ITrelloApiClient, IDisposable
{
    private static readonly Uri ApiBaseUri = new("https://api.trello.com/1/");
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public TrelloApiClient(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress ??= ApiBaseUri;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public Uri CreateAuthorizationUri(string apiKey)
    {
        apiKey = Required(apiKey, nameof(apiKey));
        return new Uri(
            "https://trello.com/1/authorize" +
            $"?expiration=never&name={Uri.EscapeDataString("Log O'clock")}" +
            "&scope=read&response_type=token" +
            $"&key={Uri.EscapeDataString(apiKey)}");
    }

    public async Task<TrelloMember> GetCurrentMemberAsync(
        TrelloCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            credentials,
            "members/me?fields=id,username,fullName",
            cancellationToken);
        var root = document.RootElement;
        return new TrelloMember(
            RequiredProperty(root, "id"),
            RequiredProperty(root, "username"),
            RequiredProperty(root, "fullName"));
    }

    public async Task<IReadOnlyList<TrelloBoard>> GetBoardsAsync(
        TrelloCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            credentials,
            "members/me/boards?filter=open&fields=id,name,url",
            cancellationToken);
        return document.RootElement.EnumerateArray()
            .Select(item => new TrelloBoard(
                RequiredProperty(item, "id"),
                RequiredProperty(item, "name"),
                OptionalProperty(item, "url") ?? string.Empty))
            .OrderBy(board => board.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<TrelloList>> GetListsAsync(
        TrelloCredentials credentials,
        string boardId,
        CancellationToken cancellationToken = default)
    {
        boardId = Required(boardId, nameof(boardId));
        using var document = await GetJsonAsync(
            credentials,
            $"boards/{Uri.EscapeDataString(boardId)}/lists?filter=open&fields=id,name,idBoard",
            cancellationToken);
        return document.RootElement.EnumerateArray()
            .Select(item => new TrelloList(
                RequiredProperty(item, "id"),
                OptionalProperty(item, "idBoard") ?? boardId,
                RequiredProperty(item, "name")))
            .OrderBy(list => list.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<TrelloCard>> GetCardsAsync(
        TrelloCredentials credentials,
        string boardId,
        CancellationToken cancellationToken = default)
    {
        boardId = Required(boardId, nameof(boardId));
        using var document = await GetJsonAsync(
            credentials,
            $"boards/{Uri.EscapeDataString(boardId)}/cards?filter=open&fields=id,name,idBoard,idList,idMembers,url,dateLastActivity",
            cancellationToken);
        return document.RootElement.EnumerateArray()
            .Select(item => new TrelloCard(
                RequiredProperty(item, "id"),
                OptionalProperty(item, "idBoard") ?? boardId,
                RequiredProperty(item, "idList"),
                RequiredProperty(item, "name"),
                OptionalProperty(item, "url") ?? string.Empty,
                item.TryGetProperty("idMembers", out var members) && members.ValueKind == JsonValueKind.Array
                    ? members.EnumerateArray()
                        .Select(member => member.GetString())
                        .Where(member => !string.IsNullOrWhiteSpace(member))
                        .Cast<string>()
                        .ToArray()
                    : [],
                ParseDate(OptionalProperty(item, "dateLastActivity"))))
            .ToArray();
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<JsonDocument> GetJsonAsync(
        TrelloCredentials credentials,
        string relativePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var apiKey = Required(credentials.ApiKey, nameof(credentials.ApiKey));
        var token = Required(credentials.Token, nameof(credentials.Token));
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "OAuth",
                $"oauth_consumer_key=\"{EscapeHeader(apiKey)}\", oauth_token=\"{EscapeHeader(token)}\"");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new TrelloAuthenticationException("Trello authorization is no longer valid. Connect the profile again.");
            }

            if ((int)response.StatusCode == 429)
            {
                if (attempt == 3)
                {
                    throw new TrelloRateLimitException("Trello is temporarily rate limiting synchronization. Try again later.");
                }

                var delay = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(
                    delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay,
                    cancellationToken);
                continue;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }

        throw new TrelloRateLimitException("Trello synchronization could not continue after repeated rate limits.");
    }

    private static string RequiredProperty(JsonElement element, string propertyName) =>
        OptionalProperty(element, propertyName)
        ?? throw new JsonException($"Trello response did not contain '{propertyName}'.");

    private static string? OptionalProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();

    private static string EscapeHeader(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
