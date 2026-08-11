using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Services;

public sealed class GoogleAuthorizationBroker : IGoogleAuthorizationBroker
{
    public async Task<GoogleOAuthAuthorizationCode> AuthorizeAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = $"http://127.0.0.1:{port}/oauth2/callback";
            var scopes = "openid email profile https://www.googleapis.com/auth/drive.file";
            var authorizationUri = new Uri(
                "https://accounts.google.com/o/oauth2/v2/auth" +
                $"?client_id={Uri.EscapeDataString(clientId.Trim())}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&response_type=code" +
                $"&scope={Uri.EscapeDataString(scopes)}" +
                "&access_type=offline&prompt=consent" +
                $"&state={Uri.EscapeDataString(state)}" +
                $"&code_challenge={Uri.EscapeDataString(challenge)}" +
                "&code_challenge_method=S256");
            _ = Process.Start(new ProcessStartInfo(authorizationUri.AbsoluteUri)
            {
                UseShellExecute = true,
            }) ?? throw new InvalidOperationException("The Google authorization page could not be opened.");

            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken)
                ?? throw new InvalidDataException("Google returned an empty authorization response.");
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
            {
            }

            var requestTarget = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ElementAtOrDefault(1)
                ?? throw new InvalidDataException("Google returned an invalid authorization response.");
            var callback = new Uri($"http://127.0.0.1:{port}{requestTarget}");
            var values = ParseQuery(callback.Query);
            values.TryGetValue("code", out var code);
            var success = values.TryGetValue("state", out var returnedState) &&
                          CryptographicOperations.FixedTimeEquals(
                              Encoding.UTF8.GetBytes(state),
                              Encoding.UTF8.GetBytes(returnedState)) &&
                          !string.IsNullOrWhiteSpace(code);
            var message = success
                ? "Log O'clock is connected. You can close this browser tab."
                : "Log O'clock could not complete Google authorization. Return to the app and try again.";
            var html = $"<!doctype html><html><body style=\"font-family:Segoe UI;background:#181818;color:white;padding:32px\"><h2>{WebUtility.HtmlEncode(message)}</h2></body></html>";
            var bytes = Encoding.UTF8.GetBytes(html);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            if (!success)
            {
                var error = values.GetValueOrDefault("error") ?? "Authorization was canceled or returned invalid state.";
                throw new InvalidOperationException(error);
            }

            return new GoogleOAuthAuthorizationCode(code!, redirectUri, verifier);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0].Replace('+', ' ')),
                part => part.Length > 1 ? Uri.UnescapeDataString(part[1].Replace('+', ' ')) : string.Empty,
                StringComparer.Ordinal);

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
