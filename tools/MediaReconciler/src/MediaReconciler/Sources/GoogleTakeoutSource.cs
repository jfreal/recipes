using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaReconciler.Hashing;
using MediaReconciler.Model;

namespace MediaReconciler.Sources;

/// <summary>
/// A Google Photos library, read from a Google Takeout export (takeout.google.com). Since
/// March 2025 Google's API no longer lets apps list a whole library, so Takeout is the only
/// complete view. Point <c>path</c> at the folder holding the downloaded .zip parts (they
/// don't need extracting) and/or extracted "Takeout" folders.
/// </summary>
public sealed partial class GoogleTakeoutSource : IMediaSource
{
    /// <summary>One file from an export, wherever it lives (inside a zip or extracted on disk).</summary>
    /// <param name="EntryPath">Path inside the export, always with '/' separators.</param>
    /// <param name="Location">How the report shows it: "takeout-001.zip!Takeout/..." or a relative path.</param>
    /// <param name="CacheKey">Stable identity for the scan cache.</param>
    /// <param name="LocalPath">Openable path for extracted files; null inside zips.</param>
    internal sealed record Entry(
        string EntryPath, string Location, string CacheKey, long Length, DateTime LastWriteUtc, Func<Stream> Open, string? LocalPath);

    /// <param name="CopyIndex">N for "IMG_1234.jpg(N).json": the sidecar of "IMG_1234(N).jpg".</param>
    private sealed record Sidecar(string Title, DateTime TakenUtc, int? CopyIndex);

    public async Task<IReadOnlyList<MediaItem>> ScanAsync(ScanContext context, CancellationToken ct)
    {
        var config = context.Config;
        if (string.IsNullOrWhiteSpace(config.Path) || !Directory.Exists(config.Path))
        {
            throw new DirectoryNotFoundException($"Source \"{config.Name}\": folder \"{config.Path}\" does not exist.");
        }

        var zips = Directory.EnumerateFiles(config.Path, "*.zip", SearchOption.AllDirectories).Order().ToList();
        var archives = new List<ZipArchive>();
        try
        {
            foreach (var zip in zips)
            {
                try
                {
                    archives.Add(ZipFile.OpenRead(zip));
                }
                catch (InvalidDataException e)
                {
                    throw new InvalidDataException($"Source \"{config.Name}\": {zip} is not a readable zip ({e.Message}). Re-download that part.", e);
                }
            }

            var entries = EnumerateEntries(config.Path, zips, archives).ToList();
            context.Log($"read {zips.Count} zip part(s); {entries.Count:N0} files in the export");
            return await ScanEntriesAsync(entries, context, ct);
        }
        finally
        {
            foreach (var archive in archives) archive.Dispose();
        }
    }

    internal static async Task<IReadOnlyList<MediaItem>> ScanEntriesAsync(
        IReadOnlyList<Entry> entries, ScanContext context, CancellationToken ct)
    {
        var config = context.Config;

        // Pass 1: the JSON sidecars carry the real capture time (photoTakenTime) and the
        // original file name ("title"), which Takeout truncates in the media file's own name.
        // A sidecar can sit in a different zip part than its photo, so index all of them first.
        var sidecars = new Dictionary<string, List<Sidecar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(e => e.EntryPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            if (TryReadSidecar(entry) is { } sidecar)
            {
                var dir = DirectoryOf(entry.EntryPath);
                if (!sidecars.TryGetValue(dir, out var list)) sidecars[dir] = list = [];
                list.Add(sidecar);
            }
        }

        // Pass 2: the media. The same photo appears once per album it is in ("Photos from
        // 2023" plus every album folder); Google stores it once, so we keep it once.
        var items = new List<MediaItem>();
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        var seenWithoutHash = new HashSet<(string, long, DateTime?)>();
        var albumCopies = 0;
        var edited = 0;
        var done = 0;

        try
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(entry.EntryPath);
                var kind = MediaNames.GetKind(fileName);
                if (kind is null) continue;

                if (!config.IncludeEdited && EditedSuffix().IsMatch(Path.GetFileNameWithoutExtension(fileName)))
                {
                    edited++;
                    continue;
                }

                var sidecar = FindSidecar(sidecars, entry.EntryPath);
                var originalName = sidecar?.Title is { Length: > 0 } title && MediaNames.GetKind(title) is not null ? title : fileName;
                var takenUtc = sidecar?.TakenUtc ?? (entry.LastWriteUtc == default ? null : entry.LastWriteUtc);

                string? hash = null;
                if (config.Hash)
                {
                    var key = ScanCache.Key(entry.CacheKey, entry.Length, entry.LastWriteUtc);
                    if (context.Cache.TryGet(key, out var cached) && cached.Hash is not null)
                    {
                        hash = cached.Hash;
                    }
                    else
                    {
                        await using var stream = entry.Open();
                        hash = await DropboxContentHasher.ComputeAsync(stream, ct);
                        context.Cache.Set(key, new ScanCache.Entry(hash, null, false, null));
                    }
                }

                var isAlbumCopy = hash is not null
                    ? !seenHashes.Add(hash)
                    // iPhone counters wrap at IMG_9999, so name + size alone can repeat years apart.
                    : !seenWithoutHash.Add((MediaNames.NormalizeStem(originalName), entry.Length, takenUtc));
                if (isAlbumCopy)
                {
                    albumCopies++;
                    continue;
                }

                MediaNames.TryParseDateFromName(originalName, out var localFromName);
                items.Add(new MediaItem
                {
                    SourceIndex = context.SourceIndex,
                    Location = entry.Location,
                    FileName = originalName,
                    Kind = kind.Value,
                    Size = entry.Length,
                    ContentHash = hash,
                    TakenUtc = takenUtc,
                    UtcIsFileTime = sidecar is null,
                    TakenLocal = localFromName == default ? null : localFromName,
                    LocalPath = entry.LocalPath,
                });

                if (++done % 500 == 0) context.Log($"read {done:N0} photos/videos");
            }
        }
        finally
        {
            context.Cache.Save();
        }

        if (albumCopies > 0) context.Log($"skipped {albumCopies:N0} album copies of photos already counted");
        if (edited > 0) context.Note($"skipped {edited:N0} Google \"-edited\" copies (set \"includeEdited\": true to count them)");
        return items;
    }

    private static IEnumerable<Entry> EnumerateEntries(string root, List<string> zips, List<ZipArchive> archives)
    {
        for (var i = 0; i < zips.Count; i++)
        {
            foreach (var zipEntry in archives[i].Entries)
            {
                if (zipEntry.FullName.EndsWith('/')) continue;
                yield return new Entry(
                    zipEntry.FullName,
                    $"{Path.GetFileName(zips[i])}!{zipEntry.FullName}",
                    $"{zips[i]}!{zipEntry.FullName}",
                    zipEntry.Length,
                    zipEntry.LastWriteTime.UtcDateTime,
                    zipEntry.Open,
                    LocalPath: null);
            }
        }

        // Already-extracted exports.
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(path);
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            yield return new Entry(relative, relative, path, info.Length, info.LastWriteTimeUtc, () => File.OpenRead(path), path);
        }
    }

    private static Sidecar? TryReadSidecar(Entry entry)
    {
        // Sidecars are a few hundred bytes; anything big is not one.
        if (entry.Length > 1024 * 1024) return null;
        try
        {
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("title", out var title) ||
                !root.TryGetProperty("photoTakenTime", out var taken) ||
                !taken.TryGetProperty("timestamp", out var timestamp))
            {
                return null;
            }

            var parsed = timestamp.ValueKind == JsonValueKind.Number
                ? timestamp.TryGetInt64(out var seconds)
                : long.TryParse(timestamp.GetString(), out seconds);
            if (!parsed || seconds < UnixMin || seconds > UnixMax) return null;

            var copy = SidecarCopySuffix().Match(entry.EntryPath);
            return new Sidecar(
                title.GetString() ?? "",
                DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime,
                copy.Success && int.TryParse(copy.Groups[1].Value, out var n) ? n : null);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Sidecar? FindSidecar(Dictionary<string, List<Sidecar>> sidecars, string mediaPath)
    {
        if (!sidecars.TryGetValue(DirectoryOf(mediaPath), out var candidates)) return null;

        var name = Path.GetFileName(mediaPath);
        var stemOnly = Path.GetFileNameWithoutExtension(name);
        var copy = CopySuffix().Match(stemOnly);
        int? copyIndex = copy.Success && int.TryParse(copy.Groups[1].Value, out var n) ? n : null;
        var withoutCopy = CopySuffix().Replace(stemOnly, "") + Path.GetExtension(name);

        // "IMG_1234(1).jpg" belongs to "IMG_1234.jpg(1).json", whose title is also "IMG_1234.jpg".
        var exact =
            candidates.FirstOrDefault(s => s.CopyIndex == copyIndex && s.Title.Equals(withoutCopy, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(s => s.Title.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(s => s.Title.Equals(withoutCopy, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // Long names are truncated in the export; the sidecar title still has the full name.
        var stem = Path.GetFileNameWithoutExtension(withoutCopy);
        return stem.Length >= 30
            ? candidates.FirstOrDefault(s => s.Title.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private static string DirectoryOf(string entryPath)
    {
        var slash = entryPath.LastIndexOf('/');
        return slash < 0 ? "" : entryPath[..slash];
    }

    // DateTimeOffset.FromUnixTimeSeconds' supported range (years 1–9999).
    private const long UnixMin = -62_135_596_800;
    private const long UnixMax = 253_402_300_799;

    [GeneratedRegex(@"\((\d+)\)$")]
    private static partial Regex CopySuffix();

    // "IMG_1234.jpg(1).json", "IMG_1234.jpg.supplemental-metadata(1).json"
    [GeneratedRegex(@"\((\d+)\)\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex SidecarCopySuffix();

    // English plus the common localized suffixes Takeout uses for edited copies.
    [GeneratedRegex(@"-(edited|bearbeitet|modifié|editado|modificato|bewerkt|redigerad)(\(\d+\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex EditedSuffix();
}
