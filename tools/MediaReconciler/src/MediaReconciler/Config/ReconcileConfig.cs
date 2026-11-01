using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaReconciler.Config;

public sealed class ReconcileConfig
{
    /// <summary>Every place to compare. Add as many as you like, including several accounts of one service.</summary>
    public List<SourceConfig> Sources { get; set; } = [];

    /// <summary>
    /// How many backup copies (sources with <c>isBackup: true</c>) a photo needs before the
    /// report calls it safe. Items below this land in at-risk.csv.
    /// </summary>
    public int MinBackupCopies { get; set; } = 2;

    /// <summary>
    /// Allow matching purely on capture time when one copy was renamed (Dropbox camera uploads
    /// rename to "2024-05-01 12.34.56.jpg" and may convert HEIC to JPG). These matches are
    /// reported as "weak" so you can spot-check them.
    /// </summary>
    public bool TimeOnlyMatching { get; set; } = true;

    public string OutputDirectory { get; set; } = "report";

    /// <summary>Where to keep the hash cache. Defaults to %LOCALAPPDATA%\MediaReconciler\scan-cache.json.</summary>
    public string? CachePath { get; set; }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ReconcileConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ReconcileConfig>(json, JsonOptions)
            ?? throw new ConfigException($"{path} is empty.");
        config.Validate(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return config;
    }

    public string ResolveCachePath() =>
        CachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "MediaReconciler",
            "scan-cache.json");

    private void Validate(string baseDirectory)
    {
        if (Sources.Count < 2)
        {
            throw new ConfigException("Configure at least two sources to compare.");
        }

        if (MinBackupCopies < 1)
        {
            throw new ConfigException("minBackupCopies must be at least 1.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Name)) throw new ConfigException("Every source needs a name.");
            if (!names.Add(source.Name)) throw new ConfigException($"Two sources are both called \"{source.Name}\".");
            if (string.IsNullOrWhiteSpace(source.Type)) throw new ConfigException($"Source \"{source.Name}\" needs a type.");

            if (source.Path is not null && SourceTypes.UsesLocalPath(source.Type) && !System.IO.Path.IsPathRooted(source.Path))
            {
                source.Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, source.Path));
            }
        }

        if (!Path.IsPathRooted(OutputDirectory))
        {
            OutputDirectory = Path.GetFullPath(Path.Combine(baseDirectory, OutputDirectory));
        }

        // Scheduled tasks start in System32, so relative paths must not depend on the working directory.
        if (CachePath is not null && !Path.IsPathRooted(CachePath))
        {
            CachePath = Path.GetFullPath(Path.Combine(baseDirectory, CachePath));
        }
    }
}

public static class SourceTypes
{
    public const string Folder = "folder";
    public const string Dropbox = "dropbox";
    public const string GoogleTakeout = "google-takeout";
    public const string IPhone = "iphone";
    public const string Manifest = "manifest";

    public static bool UsesLocalPath(string type) =>
        type is Folder or GoogleTakeout or Manifest;
}

public sealed class SourceConfig
{
    /// <summary>Shown in the report, e.g. "Wife iCloud" or "Dropbox (John)".</summary>
    public string Name { get; set; } = "";

    /// <summary>folder, dropbox, google-takeout, iphone or manifest.</summary>
    public string Type { get; set; } = "";

    /// <summary>
    /// Folder / Takeout / manifest: a path on this PC. Dropbox: the folder inside Dropbox
    /// ("/Camera Uploads"); empty means the whole account.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Does a copy here count as a backup? Defaults to true, except for iPhones: a photo that
    /// is only on the phone is exactly what we are trying to find.
    /// </summary>
    public bool? IsBackup { get; set; }

    /// <summary>Compute content hashes (reads every byte once; cached afterwards).</summary>
    public bool Hash { get; set; } = true;

    /// <summary>Read EXIF/QuickTime capture times (folder sources).</summary>
    public bool ReadMetadata { get; set; } = true;

    /// <summary>
    /// Folder sources: also read cloud "online-only" placeholder files (iCloud for Windows,
    /// Dropbox, OneDrive). Off by default because reading them downloads every file.
    /// </summary>
    public bool ReadOnlineOnlyFiles { get; set; }

    /// <summary>Folder / directory names to skip, e.g. ".dropbox.cache".</summary>
    public List<string> Exclude { get; set; } = [];

    /// <summary>Dropbox app key (from the Dropbox App Console).</summary>
    public string? AppKey { get; set; }

    /// <summary>Dropbox app secret; only needed if you made the refresh token without PKCE.</summary>
    public string? AppSecret { get; set; }

    /// <summary>Dropbox refresh token from <c>MediaReconciler dropbox-auth</c>. Use "env:VAR" to read it from an environment variable.</summary>
    public string? RefreshToken { get; set; }

    /// <summary>A short-lived Dropbox access token, as an alternative to a refresh token.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Google Takeout: also count Google's "-edited" copies.</summary>
    public bool IncludeEdited { get; set; }

    /// <summary>iPhone: part of the device name to pick when several are plugged in.</summary>
    public string? Device { get; set; }

    public bool CountsAsBackup => IsBackup ?? Type != SourceTypes.IPhone;

    /// <summary>Resolves "env:NAME" to the environment variable NAME.</summary>
    public static string? ResolveSecret(string? value)
    {
        if (value is null || !value.StartsWith("env:", StringComparison.OrdinalIgnoreCase)) return value;
        var name = value[4..];
        return Environment.GetEnvironmentVariable(name)
            ?? throw new ConfigException($"Environment variable {name} is not set.");
    }
}

public sealed class ConfigException(string message) : Exception(message);
