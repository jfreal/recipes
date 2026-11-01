using System.Collections.Concurrent;
using System.Text.Json;

namespace MediaReconciler.Hashing;

/// <summary>
/// Remembers the hash and capture time of every file already read, keyed by path + size +
/// modified time, so the second run over a 200 GB library takes seconds instead of an hour.
/// </summary>
public sealed class ScanCache
{
    public sealed record Entry(string? Hash, DateTime? TakenUtc, bool UtcIsFileTime, DateTime? TakenLocal);

    private readonly string? _path;
    private readonly ConcurrentDictionary<string, Entry> _entries;
    private bool _dirty;

    private ScanCache(string? path, ConcurrentDictionary<string, Entry> entries)
    {
        _path = path;
        _entries = entries;
    }

    /// <summary>A cache that never touches disk (for tests, or <c>--no-cache</c>).</summary>
    public static ScanCache InMemory() => new(null, new ConcurrentDictionary<string, Entry>());

    public static ScanCache Load(string path)
    {
        var entries = new ConcurrentDictionary<string, Entry>();
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(stream);
                if (loaded is not null)
                {
                    foreach (var (k, v) in loaded) entries[k] = v;
                }
            }
            catch (JsonException)
            {
                // A corrupt cache only costs a re-scan.
            }
        }

        return new ScanCache(path, entries);
    }

    public static string Key(string location, long size, DateTime lastWriteUtc) =>
        $"{location}|{size}|{lastWriteUtc.Ticks}";

    public bool TryGet(string key, out Entry entry) => _entries.TryGetValue(key, out entry!);

    public void Set(string key, Entry entry)
    {
        _entries[key] = entry;
        _dirty = true;
    }

    public void Save()
    {
        if (_path is null || !_dirty) return;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = _path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, _entries);
        }

        File.Move(temp, _path, overwrite: true);
        _dirty = false;
    }
}
