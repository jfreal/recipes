using System.Globalization;
using MediaReconciler.Model;

namespace MediaReconciler.Sources;

/// <summary>
/// A CSV inventory written by some other program (e.g. your existing iPhone app), so any
/// source this tool can't read directly can still be reconciled. Columns, header required,
/// any order, only <c>name</c> is mandatory:
/// <code>location,name,size,hash,taken_utc,taken_local</code>
/// <c>hash</c> must be a Dropbox content hash (see DropboxContentHasher) if given.
/// Times are ISO 8601 ("2024-05-01T19:34:56Z" / "2024-05-01T12:34:56").
/// </summary>
public sealed class ManifestSource : IMediaSource
{
    public async Task<IReadOnlyList<MediaItem>> ScanAsync(ScanContext context, CancellationToken ct)
    {
        var path = context.Config.Path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException($"Source \"{context.Config.Name}\": manifest \"{path}\" does not exist.");
        }

        var lines = await File.ReadAllLinesAsync(path, ct);
        var items = Parse(lines, context.SourceIndex, out var skipped);
        if (skipped > 0) context.Note($"skipped {skipped:N0} manifest rows that aren't photos/videos or have no name");
        context.Log($"{items.Count:N0} photos/videos in {path}");
        return items;
    }

    internal static List<MediaItem> Parse(IReadOnlyList<string> lines, int sourceIndex, out int skipped)
    {
        skipped = 0;
        var items = new List<MediaItem>();
        if (lines.Count == 0) return items;

        var header = Csv.ParseLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name);
        var (location, name, size, hash, utc, local) =
            (Col("location"), Col("name"), Col("size"), Col("hash"), Col("taken_utc"), Col("taken_local"));
        if (name < 0) throw new FormatException("The manifest needs a \"name\" column.");

        for (var i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = Csv.ParseLine(lines[i]);
            string? Cell(int index) => index >= 0 && index < cells.Count && cells[index].Length > 0 ? cells[index] : null;

            var fileName = Cell(name);
            var kind = fileName is null ? null : MediaNames.GetKind(fileName);
            if (fileName is null || kind is null)
            {
                skipped++;
                continue;
            }

            items.Add(new MediaItem
            {
                SourceIndex = sourceIndex,
                Location = Cell(location) ?? fileName,
                FileName = fileName,
                Kind = kind.Value,
                Size = long.TryParse(Cell(size), out var bytes) ? bytes : -1,
                ContentHash = Cell(hash)?.ToLowerInvariant(),
                TakenUtc = ParseTime(Cell(utc), DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                TakenLocal = ParseTime(Cell(local), DateTimeStyles.None) is { } l ? DateTime.SpecifyKind(l, DateTimeKind.Unspecified) : null,
            });
        }

        return items;
    }

    private static DateTime? ParseTime(string? text, DateTimeStyles styles) =>
        text is not null && DateTime.TryParse(text, CultureInfo.InvariantCulture, styles, out var value) ? value : null;
}
