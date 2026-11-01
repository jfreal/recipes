#if WINDOWS
using MediaDevices;
using MediaReconciler.Hashing;
using MediaReconciler.Model;

namespace MediaReconciler.Sources;

/// <summary>
/// An iPhone plugged into this PC by USB, read through Windows Portable Devices (the same
/// route File Explorer uses: "Apple iPhone\Internal Storage\DCIM"). Unlock the phone and tap
/// "Trust" first.
/// </summary>
/// <remarks>
/// Only photos whose full-size original is on the phone show up. With "Optimize iPhone
/// Storage" on, older photos live only in iCloud and the phone isn't the place to check them.
/// Set Settings → Photos → Transfer to Mac or PC → "Keep Originals", or Windows receives
/// converted JPEGs whose hashes won't match the iCloud originals.
/// </remarks>
public sealed class IPhoneSource : IMediaSource
{
    public static IReadOnlyList<string> ListDevices() =>
        (MediaDeviceManager.Instance.GetDevices() ?? []).Select(d => $"{d.FriendlyName} ({d.Description})").ToList();

    public Task<IReadOnlyList<MediaItem>> ScanAsync(ScanContext context, CancellationToken ct)
    {
        var config = context.Config;
        var devices = (MediaDeviceManager.Instance.GetDevices() ?? []).ToList();
        var device = devices.FirstOrDefault(d => Matches(d, config.Device))
            ?? throw new InvalidOperationException(
                $"Source \"{config.Name}\": no device matching \"{config.Device ?? "Apple iPhone"}\" is connected. " +
                $"Seen: {string.Join(", ", devices.Select(d => d.FriendlyName))}. Unlock the phone and tap Trust.");

        device.Connect();
        try
        {
            var dcim = device.GetDirectories(@"\")
                .SelectMany(storage => device.GetDirectories(storage))
                .FirstOrDefault(d => d.EndsWith(@"\DCIM", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Source \"{config.Name}\": the phone shows no DCIM folder. Unlock it and tap \"Trust\", then retry.");

            var files = device.EnumerateFiles(dcim, "*", SearchOption.AllDirectories)
                .Where(f => MediaNames.GetKind(f) is not null)
                .ToList();
            context.Log($"found {files.Count:N0} photos/videos on {device.FriendlyName}");

            var items = new List<MediaItem>(files.Count);
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                var info = device.GetFileInfo(path);
                var size = (long)info.Length;
                var modified = info.LastWriteTime ?? info.CreationTime;

                string? hash = null;
                if (config.Hash)
                {
                    var key = ScanCache.Key($"iphone:{device.SerialNumber}:{path}", size, modified ?? default);
                    if (context.Cache.TryGet(key, out var cached) && cached.Hash is not null)
                    {
                        hash = cached.Hash;
                    }
                    else
                    {
                        using var hasher = new DropboxContentHasher();
                        device.DownloadFile(path, hasher);
                        hash = hasher.GetHashString();
                        context.Cache.Set(key, new ScanCache.Entry(hash, null, false, null));
                    }
                }

                // WPD reports the phone's local times; the photo's own DateAuthored is best.
                var taken = info.DateAuthored ?? info.CreationTime;
                items.Add(new MediaItem
                {
                    SourceIndex = context.SourceIndex,
                    Location = path,
                    FileName = info.Name,
                    Kind = MediaNames.GetKind(info.Name)!.Value,
                    Size = size,
                    ContentHash = hash,
                    TakenLocal = taken is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Unspecified) : null,
                });

                if (items.Count % 250 == 0) context.Log($"read {items.Count:N0}/{files.Count:N0}");
            }

            return Task.FromResult<IReadOnlyList<MediaItem>>(items);
        }
        finally
        {
            context.Cache.Save();
            device.Disconnect();
        }
    }

    private static bool Matches(MediaDevice device, string? wanted) =>
        wanted is null
            ? device.Manufacturer?.Contains("Apple", StringComparison.OrdinalIgnoreCase) == true ||
              device.FriendlyName?.Contains("iPhone", StringComparison.OrdinalIgnoreCase) == true
            : device.FriendlyName?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true ||
              device.Description?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true;
}
#endif
