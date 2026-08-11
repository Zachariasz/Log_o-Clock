using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Infrastructure;

public sealed class GoogleSheetsApiClient : IGoogleSheetsApiClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public GoogleSheetsApiClient(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(45);
    }

    public async Task<GoogleOAuthTokens> ExchangeAuthorizationCodeAsync(
        string clientId,
        string clientSecret,
        GoogleOAuthAuthorizationCode authorization,
        CancellationToken cancellationToken = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = Required(clientId, nameof(clientId)),
            ["client_secret"] = Required(clientSecret, nameof(clientSecret)),
            ["code"] = authorization.Code,
            ["code_verifier"] = authorization.CodeVerifier,
            ["redirect_uri"] = authorization.RedirectUri,
            ["grant_type"] = "authorization_code",
        });
        using var response = await _httpClient.PostAsync(
            "https://oauth2.googleapis.com/token",
            content,
            cancellationToken);
        return await ParseTokensAsync(response, requireRefreshToken: true, cancellationToken);
    }

    public async Task<GoogleOAuthTokens> RefreshAccessTokenAsync(
        GoogleSheetsCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = credentials.ClientId,
            ["client_secret"] = credentials.ClientSecret,
            ["refresh_token"] = credentials.RefreshToken,
            ["grant_type"] = "refresh_token",
        });
        using var response = await _httpClient.PostAsync(
            "https://oauth2.googleapis.com/token",
            content,
            cancellationToken);
        return await ParseTokensAsync(response, requireRefreshToken: false, cancellationToken);
    }

    public async Task<GoogleUser> GetCurrentUserAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonAsync(
            HttpMethod.Get,
            "https://openidconnect.googleapis.com/v1/userinfo",
            accessToken,
            body: null,
            cancellationToken);
        var root = document.RootElement;
        return new GoogleUser(
            RequiredProperty(root, "email"),
            OptionalProperty(root, "name") ?? RequiredProperty(root, "email"));
    }

    public async Task<GoogleSpreadsheet> CreateSpreadsheetAsync(
        string accessToken,
        string title,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            properties = new { title },
            sheets = new[] { new { properties = new { title = "About" } } },
        });
        using var document = await SendJsonAsync(
            HttpMethod.Post,
            "https://sheets.googleapis.com/v4/spreadsheets",
            accessToken,
            body,
            cancellationToken);
        var id = RequiredProperty(document.RootElement, "spreadsheetId");
        var url = OptionalProperty(document.RootElement, "spreadsheetUrl")
            ?? $"https://docs.google.com/spreadsheets/d/{id}/edit";
        return new GoogleSpreadsheet(id, url);
    }

    public async Task<IReadOnlyList<string>> GetWorksheetNamesAsync(
        string accessToken,
        string spreadsheetId,
        CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonAsync(
            HttpMethod.Get,
            $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(spreadsheetId)}?fields=sheets.properties.title",
            accessToken,
            body: null,
            cancellationToken);
        if (!document.RootElement.TryGetProperty("sheets", out var sheets))
        {
            return [];
        }

        return sheets.EnumerateArray()
            .Select(sheet => RequiredProperty(sheet.GetProperty("properties"), "title"))
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>>> ReadWorksheetsAsync(
        string accessToken,
        string spreadsheetId,
        IReadOnlyList<string> worksheetNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worksheetNames);
        if (worksheetNames.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(StringComparer.Ordinal);
        }

        var requestedNames = worksheetNames.Distinct(StringComparer.Ordinal).ToArray();
        var ranges = string.Join(
            "&",
            requestedNames.Select(name => $"ranges={Uri.EscapeDataString(WorksheetRange(name, "A:Q"))}"));
        using var document = await SendJsonAsync(
            HttpMethod.Get,
            $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(spreadsheetId)}/values:batchGet?majorDimension=ROWS&{ranges}",
            accessToken,
            body: null,
            cancellationToken);
        var result = new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(StringComparer.Ordinal);
        var valueRanges = document.RootElement.TryGetProperty("valueRanges", out var responseRanges)
            ? responseRanges.EnumerateArray().ToArray()
            : [];
        for (var index = 0; index < requestedNames.Length; index++)
        {
            var rows = index < valueRanges.Length &&
                       valueRanges[index].TryGetProperty("values", out var values)
                ? values.EnumerateArray()
                    .Select(row => (IReadOnlyList<string>)row.EnumerateArray()
                        .Select(cell => cell.ValueKind == JsonValueKind.String
                            ? cell.GetString() ?? string.Empty
                            : cell.ToString())
                        .ToArray())
                    .ToArray()
                : [];
            result[requestedNames[index]] = rows;
        }

        return result;
    }

    public async Task AddWorksheetsAsync(
        string accessToken,
        string spreadsheetId,
        IReadOnlyList<string> worksheetNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worksheetNames);
        var names = worksheetNames.Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0)
        {
            return;
        }

        var body = JsonSerializer.Serialize(new
        {
            requests = names.Select(name => new
            {
                addSheet = new { properties = new { title = name } },
            }),
        });
        using var document = await SendJsonAsync(
            HttpMethod.Post,
            $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(spreadsheetId)}:batchUpdate",
            accessToken,
            body,
            cancellationToken);
    }

    public async Task WriteWorksheetsAsync(
        string accessToken,
        string spreadsheetId,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<object?>>> worksheets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worksheets);
        if (worksheets.Count == 0)
        {
            return;
        }

        var body = JsonSerializer.Serialize(new
        {
            valueInputOption = "RAW",
            data = worksheets.Select(item => new
            {
                range = WorksheetRange(item.Key, $"A1:Q{Math.Max(1, item.Value.Count)}"),
                majorDimension = "ROWS",
                values = item.Value,
            }),
        });
        using var document = await SendJsonAsync(
            HttpMethod.Post,
            $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(spreadsheetId)}/values:batchUpdate",
            accessToken,
            body,
            cancellationToken);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string uri,
        string accessToken,
        string? body,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, uri, accessToken, body, cancellationToken);
        return await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string uri,
        string accessToken,
        string? body,
        CancellationToken cancellationToken,
        bool allowNotFound = false)
    {
        const int maximumRateLimitRetries = 6;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
            {
                return response;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < maximumRateLimitRetries)
            {
                var delay = RateLimitDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            var status = response.StatusCode;
            var detail = await ReadGoogleErrorAsync(response, cancellationToken);
            response.Dispose();
            if (status == HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException("Google authorization expired. Reconnect this profile.");
            }

            if (status == HttpStatusCode.TooManyRequests)
            {
                throw new HttpRequestException(
                    "Google Sheets temporarily exhausted its request quota after automatic retries. " +
                    "Wait one minute and synchronize again." + FormatDetail(detail));
            }

            throw new HttpRequestException(
                $"Google Sheets request failed with HTTP {(int)status} ({status}).{FormatDetail(detail)}");
        }
    }

    private static TimeSpan RateLimitDelay(HttpResponseMessage response, int attempt)
    {
        var serverDelay = response.Headers.RetryAfter?.Delta;
        if (serverDelay is null && response.Headers.RetryAfter?.Date is { } retryDate)
        {
            serverDelay = retryDate - DateTimeOffset.UtcNow;
        }

        if (serverDelay is { } requested && requested >= TimeSpan.Zero)
        {
            return requested > TimeSpan.FromSeconds(64) ? TimeSpan.FromSeconds(64) : requested;
        }

        var seconds = Math.Min(32, Math.Pow(2, attempt));
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1001));
    }

    private static async Task<string?> ReadGoogleErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("error", out var error))
            {
                return null;
            }

            var message = OptionalProperty(error, "message");
            var status = OptionalProperty(error, "status");
            var detail = string.Join(
                " ",
                new[] { status, message }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(detail))
            {
                return null;
            }

            var sanitized = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return sanitized[..Math.Min(500, sanitized.Length)];
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string FormatDetail(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? string.Empty : $" Google says: {detail}";

    private static string WorksheetRange(string worksheetName, string cells) =>
        $"'{worksheetName.Replace("'", "''")}'!{cells}";

    private static async Task<GoogleOAuthTokens> ParseTokensAsync(
        HttpResponseMessage response,
        bool requireRefreshToken,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            throw new InvalidOperationException(
                $"Google authorization failed with HTTP {(int)status} ({status}). Check the desktop OAuth credentials and try again.");
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        var accessToken = RequiredProperty(root, "access_token");
        var refreshToken = OptionalProperty(root, "refresh_token");
        if (requireRefreshToken && string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException(
                "Google did not return an offline refresh token. Remove the app from your Google account permissions and connect again.");
        }

        var expiresIn = root.TryGetProperty("expires_in", out var expiration) && expiration.TryGetInt32(out var seconds)
            ? seconds
            : 3600;
        return new GoogleOAuthTokens(
            accessToken,
            refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)));
    }

    private static string Required(string value, string parameterName) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException("A value is required.", parameterName);

    private static string RequiredProperty(JsonElement element, string name) =>
        OptionalProperty(element, name)
        ?? throw new InvalidDataException($"Google returned a response without '{name}'.");

    private static string? OptionalProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
