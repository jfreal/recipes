using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaReconciler.Matching;
using MediaReconciler.Model;

namespace MediaReconciler.Reporting;

/// <summary>Writes summary.md plus CSV lists (they open in Excel) into the output folder.</summary>
public static partial class ReportWriter
{
    public static string Write(Analysis analysis, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var summary = BuildSummary(analysis);
        File.WriteAllText(Path.Combine(outputDirectory, "summary.md"), summary);

        WriteAllPictures(analysis, Path.Combine(outputDirectory, "all-pictures.csv"));
        WriteGroups(analysis, Path.Combine(outputDirectory, "at-risk.csv"), analysis.AtRisk);
        WriteGroups(
            analysis,
            Path.Combine(outputDirectory, "weak-matches.csv"),
            analysis.Groups.Where(g => g.Strength == MatchStrength.Weak && g.Sources.Count() > 1));

        foreach (var stats in analysis.PerSource)
        {
            var source = stats.Source;
            var slug = Slug(source.Name);

            if (source.IsBackup)
            {
                WriteGroups(
                    analysis,
                    Path.Combine(outputDirectory, $"missing-from-{slug}.csv"),
                    analysis.Groups.Where(g => !g.IsIn(source.Index)));
            }
            else
            {
                WriteItems(
                    analysis,
                    Path.Combine(outputDirectory, $"safe-to-remove-from-{slug}.csv"),
                    analysis.Groups
                        .Where(g => g.IsIn(source.Index) && analysis.BackupCopies(g) >= analysis.MinBackupCopies)
                        .SelectMany(g => g.Items.Where(i => i.SourceIndex == source.Index).Select(i => (g, i))));
            }

            if (stats.DuplicateCopies.Count > 0)
            {
                WriteDuplicates(analysis, Path.Combine(outputDirectory, $"duplicates-in-{slug}.csv"), source.Index);
            }
        }

        return summary;
    }

    internal static string BuildSummary(Analysis analysis)
    {
        var sb = new StringBuilder();
        var total = analysis.Groups.Count;
        var min = analysis.MinBackupCopies;

        sb.AppendLine("# Photo & video backup reconciliation");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}. **{total:N0} distinct photos/videos** across {analysis.Sources.Count} sources. " +
                      $"Safe means at least **{min}** backup copies.");
        sb.AppendLine();

        var notBackedUp = analysis.ByBackupCopies[0];
        var belowMin = Tally.Of(analysis.AtRisk);
        sb.AppendLine("## Verdict");
        sb.AppendLine();
        if (notBackedUp.Count > 0)
        {
            sb.AppendLine($"- ⚠️ **{notBackedUp.Count:N0} items ({Bytes(notBackedUp.Bytes)}) are in no backup at all** — only on a device. See `at-risk.csv` (listed first).");
        }

        if (belowMin.Count > notBackedUp.Count)
        {
            var more = notBackedUp.Count > 0 ? " more" : "";
            sb.AppendLine($"- {belowMin.Count - notBackedUp.Count:N0}{more} items have fewer than {min} backup copies. See `at-risk.csv`.");
        }

        if (belowMin.Count == 0)
        {
            sb.AppendLine($"- ✅ Everything has at least {min} backup copies.");
        }

        foreach (var stats in analysis.PerSource.Where(s => !s.Source.IsBackup && s.SafeToRemove.Count > 0))
        {
            sb.AppendLine($"- 📱 **{Bytes(stats.SafeToRemove.Bytes)}** ({stats.SafeToRemove.Count:N0} items) on **{stats.Source.Name}** is already in {min}+ backups and can be removed from it. See `safe-to-remove-from-{Slug(stats.Source.Name)}.csv`.");
        }

        var dupes = analysis.PerSource.Where(s => s.DuplicateCopies.Count > 0).ToList();
        if (dupes.Count > 0)
        {
            var bytes = dupes.Sum(s => s.DuplicateCopies.Bytes);
            sb.AppendLine($"- 🧹 {Bytes(bytes)} is spent on byte-identical duplicates inside a single source ({string.Join(", ", dupes.Select(d => $"{d.Source.Name}: {d.DuplicateCopies.Count:N0}"))}).");
        }

        sb.AppendLine();
        sb.AppendLine("## Sources");
        sb.AppendLine();
        sb.AppendLine("| Source | Backup? | Files | Stored | Only here | Missing here | Duplicate copies |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|");
        foreach (var s in analysis.PerSource)
        {
            sb.AppendLine($"| {s.Source.Name} ({s.Source.Type}) | {(s.Source.IsBackup ? "yes" : "no")} | {s.Stored.Count:N0} | {Bytes(s.Stored.Bytes)} | " +
                          $"{s.OnlyHere.Count:N0} ({Bytes(s.OnlyHere.Bytes)}) | {s.Missing.Count:N0} | {s.DuplicateCopies.Count:N0} ({Bytes(s.DuplicateCopies.Bytes)}) |");
        }

        sb.AppendLine();
        sb.AppendLine("## How many backup copies each item has");
        sb.AppendLine();
        sb.AppendLine("| Backup copies | Items | Size |");
        sb.AppendLine("|---:|---:|---:|");
        for (var n = 0; n < analysis.ByBackupCopies.Count; n++)
        {
            var label = n == analysis.ByBackupCopies.Count - 1 ? $"{n}+" : n.ToString(CultureInfo.InvariantCulture);
            sb.AppendLine($"| {label} | {analysis.ByBackupCopies[n].Count:N0} | {Bytes(analysis.ByBackupCopies[n].Bytes)} |");
        }

        var backups = analysis.PerSource.Where(s => s.Source.IsBackup).ToList();
        if (backups.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine("## What if you dropped one backup?");
            sb.AppendLine();
            sb.AppendLine("Use this to decide which subscription to cancel. \"Lost\" items exist in no other backup, so copy them elsewhere first (`missing-from-<source>.csv` of the backup you keep lists them).");
            sb.AppendLine();
            sb.AppendLine("| If you dropped | Frees | Items lost from every backup | Items falling below " + min + " copies |");
            sb.AppendLine("|---|---:|---:|---:|");
            foreach (var s in backups)
            {
                sb.AppendLine($"| {s.Source.Name} | {Bytes(s.Stored.Bytes)} | {s.LostIfDropped.Count:N0} ({Bytes(s.LostIfDropped.Bytes)}) | {s.BelowMinimumIfDropped.Count:N0} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Overlap (items in the row source that are also in the column source)");
        sb.AppendLine();
        sb.Append("| |");
        foreach (var s in analysis.Sources) sb.Append($" {s.Name} |");
        sb.AppendLine();
        sb.Append("|---|");
        foreach (var _ in analysis.Sources) sb.Append("---:|");
        sb.AppendLine();
        foreach (var a in analysis.Sources)
        {
            sb.Append($"| **{a.Name}** |");
            var own = analysis.Overlap[a.Index, a.Index];
            foreach (var b in analysis.Sources)
            {
                var n = analysis.Overlap[a.Index, b.Index];
                sb.Append(own == 0 ? " – |" : $" {n:N0} ({100.0 * n / own:0}%) |");
            }

            sb.AppendLine();
        }

        var strengths = analysis.Groups.Where(g => g.Sources.Count() > 1).GroupBy(g => g.Strength).ToDictionary(g => g.Key, g => g.Count());
        sb.AppendLine();
        sb.AppendLine("## How matches were made");
        sb.AppendLine();
        sb.AppendLine($"- Exact (identical bytes): {strengths.GetValueOrDefault(MatchStrength.Exact):N0}");
        sb.AppendLine($"- Strong (same original name + size or capture time): {strengths.GetValueOrDefault(MatchStrength.Strong):N0}");
        sb.AppendLine($"- Weak (same capture second, one copy renamed): {strengths.GetValueOrDefault(MatchStrength.Weak):N0} — spot-check a few in `weak-matches.csv`");

        var notes = analysis.Sources.Where(s => s.Notes.Count > 0).ToList();
        if (notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Notes");
            sb.AppendLine();
            foreach (var source in notes)
            {
                foreach (var note in source.Notes) sb.AppendLine($"- **{source.Name}**: {note}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("_Read-only: this tool never deletes or changes anything. Check a sample of any list before acting on it._");
        return sb.ToString();
    }

    private static void WriteAllPictures(Analysis analysis, string path)
    {
        var header = new List<string> { "id", "name", "taken", "size", "match", "backup_copies" };
        header.AddRange(analysis.Sources.Select(s => s.Name));

        Csv.Write(path, header, analysis.Groups.Select(g =>
        {
            var row = new List<string?> { Id(g), g.Representative.FileName, Time(g.Taken), Size(g.Size), g.Strength.ToString(), Count(analysis.BackupCopies(g)) };
            row.AddRange(analysis.Sources.Select(s => string.Join(" | ", g.Items.Where(i => i.SourceIndex == s.Index).Select(i => i.Location))));
            return row;
        }));
    }

    private static void WriteGroups(Analysis analysis, string path, IEnumerable<MatchGroup> groups)
    {
        Csv.Write(
            path,
            ["id", "name", "taken", "size", "kind", "backup_copies", "found_in", "open_from", "locations"],
            groups.Select(g => new[]
            {
                Id(g),
                g.Representative.FileName,
                Time(g.Taken),
                Size(g.Size),
                g.Representative.Kind.ToString(),
                Count(analysis.BackupCopies(g)),
                string.Join(" + ", g.Sources.Select(s => analysis.Sources[s].Name)),
                g.Items.Select(i => i.LocalPath).FirstOrDefault(p => p is not null),
                string.Join(" | ", g.Items.Select(i => $"{analysis.Sources[i.SourceIndex].Name}: {i.Location}")),
            }));
    }

    private static void WriteItems(Analysis analysis, string path, IEnumerable<(MatchGroup Group, MediaItem Item)> items)
    {
        Csv.Write(
            path,
            ["id", "name", "taken", "size", "location", "match", "also_in"],
            items.Select(x => new[]
            {
                Id(x.Group),
                x.Item.FileName,
                Time(x.Group.Taken),
                Size(x.Item.Size),
                x.Item.Location,
                x.Group.Strength.ToString(),
                string.Join(" + ", x.Group.Sources.Where(s => s != x.Item.SourceIndex).Select(s => analysis.Sources[s].Name)),
            }));
    }

    private static void WriteDuplicates(Analysis analysis, string path, int sourceIndex)
    {
        var rows = new List<string?[]>();
        foreach (var group in analysis.Groups)
        {
            // Keep the copy with the plainest name; list the rest.
            var copies = group.Items
                .Where(i => i.SourceIndex == sourceIndex)
                .OrderByDescending(i => i.HasOriginalName)
                .ThenBy(i => i.FileName.Length)
                .ThenBy(i => i.Location, StringComparer.Ordinal)
                .ToList();
            rows.AddRange(copies.Skip(1).Select(extra => new[]
            {
                Id(group), extra.FileName, Size(extra.Size), extra.Location, copies[0].Location, extra.LocalPath,
            }));
        }

        Csv.Write(path, ["id", "name", "size", "extra_copy", "same_bytes_as", "open_from"], rows);
    }

    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    internal static string Slug(string name) =>
        SlugChars().Replace(name.ToLowerInvariant(), "-").Trim('-');

    private static string Id(MatchGroup g) => g.Id.ToString(CultureInfo.InvariantCulture);
    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Size(long n) => n < 0 ? "" : n.ToString(CultureInfo.InvariantCulture);
    // Camera wall-clock times have no zone; UTC ones are marked so the two aren't confused.
    private static string Time(DateTime? t) =>
        t is { } v ? v.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + (v.Kind == DateTimeKind.Utc ? " UTC" : "") : "";

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugChars();
}
