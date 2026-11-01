using MediaReconciler.Config;
using MediaReconciler.Hashing;
using MediaReconciler.Model;

namespace MediaReconciler.Sources;

/// <summary>Anything that can list the photos and videos it holds.</summary>
public interface IMediaSource
{
    Task<IReadOnlyList<MediaItem>> ScanAsync(ScanContext context, CancellationToken ct);
}

public sealed class ScanContext(int sourceIndex, SourceConfig config, ScanCache cache, Action<string> log)
{
    public int SourceIndex { get; } = sourceIndex;
    public SourceConfig Config { get; } = config;
    public ScanCache Cache { get; } = cache;

    /// <summary>Notes worth showing in the report (skipped files, placeholders...).</summary>
    public List<string> Notes { get; } = [];

    public void Log(string message) => log($"[{Config.Name}] {message}");

    public void Note(string message)
    {
        Notes.Add(message);
        Log(message);
    }
}

public static class MediaSourceFactory
{
    public static IMediaSource Create(SourceConfig config, HttpClient http) => config.Type switch
    {
        SourceTypes.Folder => new FolderSource(),
        SourceTypes.Dropbox => new DropboxSource(http),
        SourceTypes.GoogleTakeout => new GoogleTakeoutSource(),
        SourceTypes.Manifest => new ManifestSource(),
#if WINDOWS
        SourceTypes.IPhone => new IPhoneSource(),
#else
        SourceTypes.IPhone => throw new ConfigException(
            $"Source \"{config.Name}\": reading an iPhone over USB needs the Windows build (net10.0-windows)."),
#endif
        _ => throw new ConfigException($"Source \"{config.Name}\" has unknown type \"{config.Type}\"."),
    };
}
