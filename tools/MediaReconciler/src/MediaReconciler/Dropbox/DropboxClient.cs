using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaReconciler.Dropbox;

/// <summary>
/// The few Dropbox API v2 calls we need: list every file (metadata only, including the
/// content hash) and the OAuth token endpoints. Read-only: the app only needs the
/// <c>files.metadata.read</c> scope.
/// </summary>
public sealed class DropboxClient(HttpClient http)
{
    private const string ApiBase = "https://api.dropboxapi.com/2/";
    private const string TokenUrl = "https://api.dropboxapi.com/oauth2/token";

    public sealed record FileEntry(
        string Id,
        string Name,
        string PathDisplay,
        long Size,
        string? ContentHash,
        DateTime ClientModified,
        DateTime ServerModified);

    public async Task<string> RefreshAccessTokenAsync(string appKey, string? appSecret, string refreshToken, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = appKey,
        };
        if (!string.IsNullOrEmpty(appSecret)) form["client_secret"] = appSecret;

        using var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        var token = await ReadJsonAsync<TokenResponse>(response, "refresh the Dropbox access token", ct);
        return token.AccessToken;
    }

    /// <summary>Lists every file under <paramref name="path"/> ("" = whole account), following cursors.</summary>
    public async IAsyncEnumerable<FileEntry> ListFilesAsync(
        string accessToken,
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var page = await PostAsync<ListFolderResult>(
            accessToken,
            "files/list_folder",
            new { path, recursive = true, include_deleted = false, include_non_downloadable_files = false, limit = 2000 },
            ct);

        while (true)
        {
            foreach (var entry in page.Entries)
            {
                if (entry.Tag == "file")
                {
                    yield return new FileEntry(
                        entry.Id ?? entry.PathDisplay ?? "",
                        entry.Name ?? "",
                        entry.PathDisplay ?? entry.PathLower ?? "",
                        entry.Size ?? -1,
                        entry.ContentHash,
                        entry.ClientModified ?? default,
                        entry.ServerModified ?? default);
                }
            }

            if (!page.HasMore) yield break;
            page = await PostAsync<ListFolderResult>(accessToken, "files/list_folder/continue", new { cursor = page.Cursor }, ct);
        }
    }

    /// <summary>OAuth 2 PKCE flow with no redirect URL: the user pastes the code Dropbox shows them.</summary>
    public static (string Url, string Verifier) BuildAuthorizeUrl(string appKey)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var url = "https://www.dropbox.com/oauth2/authorize" +
                  $"?client_id={Uri.EscapeDataString(appKey)}" +
                  "&response_type=code&token_access_type=offline" +
                  $"&code_challenge={challenge}&code_challenge_method=S256";
        return (url, verifier);
    }

    public async Task<(string RefreshToken, string? AccountId)> ExchangeCodeAsync(
        string appKey, string code, string verifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code.Trim(),
            ["client_id"] = appKey,
            ["code_verifier"] = verifier,
        };

        using var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        var token = await ReadJsonAsync<TokenResponse>(response, "exchange the Dropbox code", ct);
        if (string.IsNullOrEmpty(token.RefreshToken))
        {
            throw new DropboxException("Dropbox did not return a refresh token.");
        }

        return (token.RefreshToken, token.AccountId);
    }

    private async Task<T> PostAsync<T>(string accessToken, string endpoint, object body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + endpoint)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await http.SendAsync(request, ct);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 6)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(wait, ct);
                continue;
            }

            return await ReadJsonAsync<T>(response, endpoint, ct);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new DropboxException($"Dropbox refused to {what} ({(int)response.StatusCode}): {text}");
        }

        return await response.Content.ReadFromJsonAsync<T>(ct)
            ?? throw new DropboxException($"Dropbox returned an empty answer to {what}.");
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("account_id")] public string? AccountId { get; set; }
    }

    private sealed class ListFolderResult
    {
        [JsonPropertyName("entries")] public List<Entry> Entries { get; set; } = [];
        [JsonPropertyName("cursor")] public string Cursor { get; set; } = "";
        [JsonPropertyName("has_more")] public bool HasMore { get; set; }
    }

    private sealed class Entry
    {
        [JsonPropertyName(".tag")] public string Tag { get; set; } = "";
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("path_display")] public string? PathDisplay { get; set; }
        [JsonPropertyName("path_lower")] public string? PathLower { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
        [JsonPropertyName("content_hash")] public string? ContentHash { get; set; }
        [JsonPropertyName("client_modified")] public DateTime? ClientModified { get; set; }
        [JsonPropertyName("server_modified")] public DateTime? ServerModified { get; set; }
    }
}

public sealed class DropboxException(string message) : Exception(message);
