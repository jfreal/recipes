using System.Collections.Concurrent;
using MediaReconciler.Hashing;
using MediaReconciler.Metadata;
using MediaReconciler.Model;

namespace MediaReconciler.Sources;

/// <summary>
/// Any folder on this PC: an icloudpd mirror of iCloud Photos, the iCloud for Windows Photos
/// folder, the Dropbox desktop folder, a phone copy, an external drive, a NAS share...
/// </summary>
public sealed class FolderSource : IMediaSource
{
    // FILE_ATTRIBUTE_RECALL_ON_OPEN / FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: cloud placeholders.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private static readonly string[] DefaultExcludes =
        [".dropbox.cache", "$RECYCLE.BIN", "System Volume Information", "@eaDir", ".thumbnails", ".trashed"];

    public async Task<IReadOnlyList<MediaItem>> ScanAsync(ScanContext context, CancellationToken ct)
    {
        var config = context.Config;
        if (string.IsNullOrWhiteSpace(config.Path) || !Directory.Exists(config.Path))
        {
            throw new DirectoryNotFoundException($"Source \"{config.Name}\": folder \"{config.Path}\" does not exist.");
        }

        var root = config.Path;
        var excludes = new HashSet<string>(DefaultExcludes.Concat(config.Exclude), StringComparer.OrdinalIgnoreCase);
        var files = EnumerateMedia(root, excludes).ToList();
        context.Log($"found {files.Count:N0} photos/videos under {root}");

        var items = new ConcurrentBag<MediaItem>();
        var placeholders = 0;
        var unreadable = 0;
        var done = 0;

        try
        {
            await Parallel.ForEachAsync(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (file, token) =>
                {
                    try
                    {
                        var item = await ReadFileAsync(file, context, token);
                        if (item.ContentHash is null && config.Hash && IsPlaceholder(file)) Interlocked.Increment(ref placeholders);
                        items.Add(item);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        Interlocked.Increment(ref unreadable);
                        context.Log($"could not read {file.FullName}: {e.Message}");
                    }

                    var n = Interlocked.Increment(ref done);
                    if (n % 500 == 0) context.Log($"read {n:N0}/{files.Count:N0}");
                });
        }
        finally
        {
            // Keep what was hashed even if the scan is cancelled.
            context.Cache.Save();
        }

        if (placeholders > 0)
        {
            context.Note($"{placeholders:N0} files are online-only placeholders and were matched by name/date/size only. " +
                         "Set \"readOnlineOnlyFiles\": true to download and hash them.");
        }

        if (unreadable > 0) context.Note($"{unreadable:N0} files could not be read.");

        return items.OrderBy(i => i.Location, StringComparer.Ordinal).ToList();
    }

    internal static IEnumerable<FileInfo> EnumerateMedia(string root, HashSet<string> excludes)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in dir.EnumerateDirectories("*", options))
            {
                if (!excludes.Contains(sub.Name) && !sub.Attributes.HasFlag(FileAttributes.Hidden)) pending.Push(sub);
            }

            foreach (var file in dir.EnumerateFiles("*", options))
            {
                if (MediaNames.GetKind(file.Name) is not null) yield return file;
            }
        }
    }

    private static async Task<MediaItem> ReadFileAsync(FileInfo file, ScanContext context, CancellationToken ct)
    {
        var config = context.Config;
        var lastWrite = file.LastWriteTimeUtc;
        var key = ScanCache.Key(file.FullName, file.Length, lastWrite);
        var skipContent = IsPlaceholder(file) && !config.ReadOnlineOnlyFiles;

        if (!context.Cache.TryGet(key, out var cached) || (cached.Hash is null && config.Hash && !skipContent))
        {
            string? hash = null;
            var metadata = default(CaptureTime);

            if (!skipContent)
            {
                if (config.ReadMetadata)
                {
                    await using var metaStream = OpenRead(file.FullName, sequential: false);
                    metadata = CaptureTimeReader.Read(metaStream);
                }

                if (config.Hash)
                {
                    await using var stream = OpenRead(file.FullName, sequential: true);
                    hash = await DropboxContentHasher.ComputeAsync(stream, ct);
                }
            }

            var (utc, isFileTime, local) = CaptureTimeReader.ForFile(file.Name, metadata, lastWrite);
            cached = new ScanCache.Entry(hash, utc, isFileTime, local);

            // Don't cache "nothing read" for placeholders: once downloaded they should be hashed.
            if (!skipContent) context.Cache.Set(key, cached);
        }

        return new MediaItem
        {
            SourceIndex = context.SourceIndex,
            Location = Path.GetRelativePath(config.Path!, file.FullName),
            FileName = file.Name,
            Kind = MediaNames.GetKind(file.Name)!.Value,
            Size = file.Length,
            ContentHash = cached.Hash,
            TakenUtc = cached.TakenUtc,
            UtcIsFileTime = cached.UtcIsFileTime,
            TakenLocal = cached.TakenLocal,
            LocalPath = file.FullName,
        };
    }

    private static bool IsPlaceholder(FileInfo file) =>
        (file.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    private static FileStream OpenRead(string path, bool sequential) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: sequential ? 1024 * 1024 : 64 * 1024,
        sequential ? FileOptions.SequentialScan | FileOptions.Asynchronous : FileOptions.RandomAccess);
}
