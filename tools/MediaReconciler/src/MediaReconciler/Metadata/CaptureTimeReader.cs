using System.Globalization;
using System.Text.RegularExpressions;
using MediaReconciler.Model;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;

namespace MediaReconciler.Metadata;

public readonly record struct CaptureTime(DateTime? Utc, DateTime? Local);

/// <summary>Reads when a photo or video was taken from its EXIF / QuickTime metadata.</summary>
public static class CaptureTimeReader
{
    /// <summary>Reads capture time from a seekable stream. Never throws for unreadable files.</summary>
    public static CaptureTime Read(Stream stream)
    {
        IReadOnlyList<MetadataExtractor.Directory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(stream);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Third-party parser over arbitrary files: a malformed one must not stop the scan.
            return default;
        }

        DateTime? local = null;
        DateTime? utc = null;

        var exif = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (exif is not null &&
            TryGetExifDate(exif, ExifDirectoryBase.TagDateTimeOriginal, out var original))
        {
            local = original;
            var offset = exif.GetString(ExifDirectoryBase.TagTimeZoneOriginal);
            if (TryParseOffset(offset, out var span))
            {
                utc = DateTime.SpecifyKind(original - span, DateTimeKind.Utc);
            }
        }

        if (local is null && utc is null &&
            directories.OfType<QuickTimeMetadataHeaderDirectory>().FirstOrDefault()?
                .GetString(QuickTimeMetadataHeaderDirectory.TagCreationDate) is { } appleDate &&
            TryParseQuickTimeCreationDate(appleDate, out var withZone))
        {
            // iPhone videos record "com.apple.quicktime.creationdate" with the local offset,
            // giving the camera wall-clock time that renamed Dropbox copies are matched on.
            local = DateTime.SpecifyKind(withZone.DateTime, DateTimeKind.Unspecified);
            utc = withZone.UtcDateTime;
        }

        if (local is null && utc is null)
        {
            // QuickTime (MOV/MP4) movie-header creation dates are UTC by spec.
            var movie = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
            if (movie is not null &&
                movie.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created) &&
                created.Year > 1970)
            {
                utc = DateTime.SpecifyKind(created, DateTimeKind.Utc);
            }
        }

        return new CaptureTime(utc, local);
    }

    /// <summary>
    /// Best-effort capture time for a local file: metadata, then a date in the file name,
    /// then (marked as low confidence) the file's modified time. icloudpd sets the modified
    /// time to the photo's creation date, so that last fallback is better than nothing.
    /// </summary>
    public static (DateTime? Utc, bool UtcIsFileTime, DateTime? Local) ForFile(
        string fileName, CaptureTime fromMetadata, DateTime? fileTimeUtc)
    {
        var local = fromMetadata.Local;
        if (local is null && MediaNames.TryParseDateFromName(fileName, out var fromName))
        {
            local = fromName;
        }

        if (fromMetadata.Utc is not null) return (fromMetadata.Utc, false, local);
        return (fileTimeUtc, fileTimeUtc is not null, local);
    }

    private static bool TryGetExifDate(ExifSubIfdDirectory exif, int tag, out DateTime value)
    {
        // EXIF dates are "yyyy:MM:dd HH:mm:ss" camera wall-clock time.
        var text = exif.GetString(tag);
        if (text is not null &&
            DateTime.TryParseExact(text.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Parses "2024-05-01T12:34:56-0700" (Apple's form) or an ISO 8601 offset like "-07:00".</summary>
    internal static bool TryParseQuickTimeCreationDate(string text, out DateTimeOffset value)
    {
        var normalized = Regex.Replace(text.Trim(), @"([+-]\d{2})(\d{2})$", "$1:$2");
        return DateTimeOffset.TryParseExact(
            normalized,
            ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);
    }

    private static bool TryParseOffset(string? text, out TimeSpan offset)
    {
        // "+02:00" / "-07:00"
        offset = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var sign = text[0] == '-' ? -1 : 1;
        if (text[0] is '+' or '-') text = text[1..];
        if (!TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out var span)) return false;
        offset = sign * span;
        return true;
    }
}
