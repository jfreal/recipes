using System.Net;
using System.Text;
using System.Text.Json;
using MediaReconciler.Config;
using MediaReconciler.Hashing;
using MediaReconciler.Sources;

namespace MediaReconciler.Tests;

public class DropboxSourceTests
{
    [Fact]
    public async Task Lists_every_page_after_refreshing_the_token()
    {
        var handler = new FakeDropbox(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            var body = request.Content!.ReadAsStringAsync().Result;

            if (url == "https://api.dropboxapi.com/oauth2/token")
            {
                Assert.Contains("grant_type=refresh_token", body);
                Assert.Contains("refresh_token=the-refresh-token", body);
                Assert.Contains("client_id=the-app-key", body);
                return Json("""{ "access_token": "short-lived", "token_type": "bearer", "expires_in": 14400 }""");
            }

            Assert.Equal("Bearer short-lived", request.Headers.Authorization!.ToString());
            if (url.EndsWith("/files/list_folder"))
            {
                using var doc = JsonDocument.Parse(body);
                Assert.Equal("/Camera Uploads", doc.RootElement.GetProperty("path").GetString());
                Assert.True(doc.RootElement.GetProperty("recursive").GetBoolean());
                return Json("""
                    {
                      "entries": [
                        { ".tag": "folder", "name": "2024", "path_display": "/Camera Uploads/2024", "id": "id:f" },
                        { ".tag": "file", "name": "2024-05-01 12.34.56.jpg", "path_display": "/Camera Uploads/2024-05-01 12.34.56.jpg",
                          "id": "id:1", "size": 2100000, "content_hash": "abc123",
                          "client_modified": "2024-05-01T19:34:56Z", "server_modified": "2024-05-02T08:00:00Z" },
                        { ".tag": "file", "name": "notes.txt", "path_display": "/Camera Uploads/notes.txt", "id": "id:2", "size": 5,
                          "client_modified": "2024-05-01T19:34:56Z", "server_modified": "2024-05-02T08:00:00Z" }
                      ],
                      "cursor": "page-2", "has_more": true
                    }
                    """);
            }

            if (url.EndsWith("/files/list_folder/continue"))
            {
                Assert.Contains("page-2", body);
                return Json("""
                    {
                      "entries": [
                        { ".tag": "file", "name": "IMG_0001.HEIC", "path_display": "/Camera Uploads/IMG_0001.HEIC",
                          "id": "id:3", "size": 3000000, "content_hash": "def456",
                          "client_modified": "2024-05-03T10:00:00Z", "server_modified": "2024-05-03T10:05:00Z" }
                      ],
                      "cursor": "page-3", "has_more": false
                    }
                    """);
            }

            throw new InvalidOperationException($"Unexpected call to {url}");
        });

        var config = new SourceConfig
        {
            Name = "Dropbox",
            Type = SourceTypes.Dropbox,
            Path = "Camera Uploads",
            AppKey = "the-app-key",
            RefreshToken = "the-refresh-token",
        };

        using var http = new HttpClient(handler);
        var items = await new DropboxSource(http).ScanAsync(new ScanContext(1, config, ScanCache.InMemory(), _ => { }), default);

        Assert.Equal(2, items.Count);
        var renamed = items[0];
        Assert.Equal("/Camera Uploads/2024-05-01 12.34.56.jpg", renamed.Location);
        Assert.Equal("abc123", renamed.ContentHash);
        Assert.Equal(2_100_000, renamed.Size);
        Assert.Equal(new DateTime(2024, 5, 1, 12, 34, 56), renamed.TakenLocal);
        Assert.True(renamed.UtcIsFileTime);
        Assert.Equal(1, renamed.SourceIndex);
        Assert.Equal("IMG_0001.HEIC", items[1].FileName);
        Assert.Null(items[1].TakenLocal);
    }

    [Fact]
    public async Task Retries_when_rate_limited()
    {
        var calls = 0;
        var handler = new FakeDropbox(_ =>
        {
            if (++calls == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return limited;
            }

            return Json("""{ "entries": [], "cursor": "c", "has_more": false }""");
        });

        var config = new SourceConfig { Name = "Dropbox", Type = SourceTypes.Dropbox, AccessToken = "token" };
        using var http = new HttpClient(handler);
        var items = await new DropboxSource(http).ScanAsync(new ScanContext(0, config, ScanCache.InMemory(), _ => { }), default);

        Assert.Empty(items);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Missing_credentials_are_a_config_error()
    {
        var config = new SourceConfig { Name = "Dropbox", Type = SourceTypes.Dropbox };
        using var http = new HttpClient(new FakeDropbox(_ => throw new InvalidOperationException("no calls expected")));

        await Assert.ThrowsAsync<ConfigException>(() =>
            new DropboxSource(http).ScanAsync(new ScanContext(0, config, ScanCache.InMemory(), _ => { }), default));
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeDropbox(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
