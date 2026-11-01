using MediaReconciler.Config;
using MediaReconciler.Dropbox;
using MediaReconciler.Model;

namespace MediaReconciler.Sources;

/// <summary>
/// A Dropbox account, read through the API. Only metadata is fetched: Dropbox gives us each
/// file's content hash, so nothing has to be downloaded to compare it with local copies.
/// </summary>
public sealed class DropboxSource(HttpClient http) : IMediaSource
{
    public async Task<IReadOnlyList<MediaItem>> ScanAsync(ScanContext context, CancellationToken ct)
    {
        var config = context.Config;
        var client = new DropboxClient(http);
        var token = await GetAccessTokenAsync(client, config, ct);

        // The API wants "" for the root and "/Folder" otherwise.
        var path = (config.Path ?? "").Trim();
        if (path == "/") path = "";
        if (path.Length > 0 && !path.StartsWith('/')) path = "/" + path;

        var items = new List<MediaItem>();
        var seen = 0;
        await foreach (var file in client.ListFilesAsync(token, path, ct))
        {
            if (++seen % 5000 == 0) context.Log($"listed {seen:N0} files");

            var kind = MediaNames.GetKind(file.Name);
            if (kind is null) continue;

            // Camera Uploads names files by local capture time; client_modified is a UTC file
            // timestamp, so it is only trusted as loosely as a file's modified time.
            MediaNames.TryParseDateFromName(file.Name, out var localFromName);
            items.Add(new MediaItem
            {
                SourceIndex = context.SourceIndex,
                Location = file.PathDisplay,
                FileName = file.Name,
                Kind = kind.Value,
                Size = file.Size,
                ContentHash = file.ContentHash,
                TakenUtc = file.ClientModified == default ? null : DateTime.SpecifyKind(file.ClientModified, DateTimeKind.Utc),
                UtcIsFileTime = true,
                TakenLocal = localFromName == default ? null : localFromName,
            });
        }

        context.Log($"{items.Count:N0} photos/videos out of {seen:N0} files in {(path == "" ? "the whole account" : path)}");
        return items;
    }

    private static async Task<string> GetAccessTokenAsync(DropboxClient client, SourceConfig config, CancellationToken ct)
    {
        var refreshToken = SourceConfig.ResolveSecret(config.RefreshToken);
        if (!string.IsNullOrEmpty(refreshToken))
        {
            var appKey = SourceConfig.ResolveSecret(config.AppKey)
                ?? throw new ConfigException($"Source \"{config.Name}\": a Dropbox refreshToken also needs the appKey it was issued for.");
            return await client.RefreshAccessTokenAsync(appKey, SourceConfig.ResolveSecret(config.AppSecret), refreshToken, ct);
        }

        return SourceConfig.ResolveSecret(config.AccessToken)
            ?? throw new ConfigException(
                $"Source \"{config.Name}\": set refreshToken (run `MediaReconciler dropbox-auth --app-key <key>`) or accessToken.");
    }
}
