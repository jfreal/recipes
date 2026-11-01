using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaReconciler.Model;

/// <summary>File-name rules: which files are media, and how names are normalized for matching.</summary>
public static partial class MediaNames
{
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".png", ".gif", ".webp", ".tif", ".tiff", ".bmp",
        ".dng", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", ".mp4", ".m4v", ".avi", ".3gp", ".mkv", ".mts", ".m2ts", ".wmv", ".webm",
    };

    public static MediaKind? GetKind(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (PhotoExtensions.Contains(ext)) return MediaKind.Photo;
        if (VideoExtensions.Contains(ext)) return MediaKind.Video;
        return null;
    }

    public static string NormalizeExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpeg" => ".jpg",
            ".heif" => ".heic",
            ".tiff" => ".tif",
            _ => ext,
        };
    }

    /// <summary>
    /// Lower-cased name without extension, with copy suffixes removed:
    /// "IMG_1234(1).HEIC", "IMG_1234 (2).heic" and "IMG_1234_HEVC.MOV" (icloudpd's Live Photo video)
    /// all become "img_1234".
    /// </summary>
    public static string NormalizeStem(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        stem = CopySuffix().Replace(stem, "");
        if (stem.EndsWith("_hevc", StringComparison.Ordinal)) stem = stem[..^5];
        return stem;
    }

    /// <summary>True for names like "2024-05-01 12.34.56.jpg" or "PXL_20240501_123456789.jpg".</summary>
    public static bool IsDateDerivedName(string fileName) =>
        TryParseDateFromName(fileName, out _);

    /// <summary>
    /// Reads the capture time (camera wall-clock) out of common generated names:
    /// Dropbox camera uploads ("2024-05-01 12.34.56"), Android/Pixel ("IMG_20240501_123456",
    /// "PXL_20240501_123456789"), Samsung ("20240501_123456"), screenshots.
    /// </summary>
    public static bool TryParseDateFromName(string fileName, out DateTime local)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var m = DropboxName().Match(stem);
        if (!m.Success) m = CompactName().Match(stem);
        if (m.Success &&
            DateTime.TryParseExact(
                m.Groups["d"].Value + m.Groups["t"].Value.Replace(".", "").Replace("-", "").Replace(":", ""),
                ["yyyy-MM-ddHHmmss", "yyyyMMddHHmmss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out local))
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            return true;
        }

        local = default;
        return false;
    }

    [GeneratedRegex(@"\s?\(\d+\)$")]
    private static partial Regex CopySuffix();

    // "2024-05-01 12.34.56" optionally followed by "-1" (Dropbox's collision suffix).
    [GeneratedRegex(@"^(?<d>\d{4}-\d{2}-\d{2}) (?<t>\d{2}\.\d{2}\.\d{2})(-\d+)?(\s?\(\d+\))?$")]
    private static partial Regex DropboxName();

    // "IMG_20240501_123456", "VID_20240501_123456", "PXL_20240501_123456789", "20240501_123456",
    // "Screenshot_20240501-123456".
    [GeneratedRegex(@"^(?:(?:IMG|VID|PXL|MVIMG|Screenshot|Screen_Recording)[_-])?(?<d>(?:19|20)\d{6})[_-](?<t>\d{6})(?:\d{0,3})(?:[_~.-].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CompactName();
}
