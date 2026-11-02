using MediaReconciler.Matching;

namespace MediaReconciler.Reporting;

public sealed record SourceInfo(int Index, string Name, string Type, bool IsBackup)
{
    public List<string> Notes { get; init; } = [];
}

public sealed record Tally(int Count, long Bytes)
{
    public static Tally Of(IEnumerable<MatchGroup> groups) => Of(groups, g => g.Size);

    public static Tally Of<T>(IEnumerable<T> things, Func<T, long> size)
    {
        var list = things.ToList();
        return new Tally(list.Count, list.Sum(t => Math.Max(0, size(t))));
    }
}

public sealed record SourceStats(
    SourceInfo Source,
    Tally Stored,
    int Pictures,
    Tally OnlyHere,
    Tally Missing,
    Tally DuplicateCopies,
    Tally SafeToRemove,
    Tally LostIfDropped,
    Tally BelowMinimumIfDropped);

/// <summary>The numbers behind the report, computed from the match groups.</summary>
public sealed class Analysis
{
    public required IReadOnlyList<SourceInfo> Sources { get; init; }
    public required IReadOnlyList<MatchGroup> Groups { get; init; }
    public required int MinBackupCopies { get; init; }
    public required IReadOnlyList<SourceStats> PerSource { get; init; }

    /// <summary>Pictures with fewer than <see cref="MinBackupCopies"/> backup copies.</summary>
    public required IReadOnlyList<MatchGroup> AtRisk { get; init; }

    /// <summary>Index = number of backup copies (last bucket is "that many or more").</summary>
    public required IReadOnlyList<Tally> ByBackupCopies { get; init; }

    /// <summary>[a, b] = pictures in source a that are also in source b.</summary>
    public required int[,] Overlap { get; init; }

    public int BackupCopies(MatchGroup group) => group.Sources.Count(s => Sources[s].IsBackup);

    public bool IsSafeToRemove(MatchGroup group, SourceInfo source) =>
        SafeToRemove(group, BackupCopies(group), source.IsBackup, MinBackupCopies);

    /// <summary>
    /// A copy can go when enough *other* backups hold the picture. A group joined only by
    /// capture time (Weak) is never deletion evidence: a burst shot in the same second could
    /// be standing in for the real backup.
    /// </summary>
    private static bool SafeToRemove(MatchGroup group, int backupCopies, bool sourceIsBackup, int minBackupCopies) =>
        group.Strength != MatchStrength.Weak && backupCopies - (sourceIsBackup ? 1 : 0) >= minBackupCopies;

    public static Analysis Compute(IReadOnlyList<SourceInfo> sources, IReadOnlyList<MatchGroup> groups, int minBackupCopies)
    {
        var backupCopies = groups.ToDictionary(g => g, g => g.Sources.Count(s => sources[s].IsBackup));

        var perSource = sources.Select(source =>
        {
            var here = groups.Where(g => g.IsIn(source.Index)).ToList();
            var itemsHere = here.SelectMany(g => g.Items.Where(i => i.SourceIndex == source.Index)).ToList();

            // Extra byte-identical copies inside this one source.
            var duplicates = here
                .Select(g => g.Items.Where(i => i.SourceIndex == source.Index).ToList())
                .Where(copies => copies.Count > 1)
                .SelectMany(copies => copies.Skip(1));

            var safeToRemove = here
                .Where(g => SafeToRemove(g, backupCopies[g], source.IsBackup, minBackupCopies))
                .SelectMany(g => g.Items.Where(i => i.SourceIndex == source.Index));

            var lostIfDropped = source.IsBackup ? here.Where(g => backupCopies[g] == 1) : [];
            var belowIfDropped = source.IsBackup
                ? here.Where(g => backupCopies[g] - 1 < minBackupCopies && backupCopies[g] >= minBackupCopies)
                : [];

            return new SourceStats(
                source,
                Tally.Of(itemsHere, i => i.Size),
                here.Count,
                Tally.Of(here.Where(g => g.Sources.Count() == 1)),
                Tally.Of(groups.Where(g => !g.IsIn(source.Index))),
                Tally.Of(duplicates, i => i.Size),
                Tally.Of(safeToRemove, i => i.Size),
                Tally.Of(lostIfDropped),
                Tally.Of(belowIfDropped));
        }).ToList();

        var maxBucket = Math.Max(minBackupCopies + 1, 3);
        var byCopies = Enumerable.Range(0, maxBucket + 1)
            .Select(n => Tally.Of(groups.Where(g => n == maxBucket ? backupCopies[g] >= n : backupCopies[g] == n)))
            .ToList();

        var overlap = new int[sources.Count, sources.Count];
        foreach (var group in groups)
        {
            var present = group.Sources.ToList();
            foreach (var a in present)
            {
                foreach (var b in present) overlap[a, b]++;
            }
        }

        return new Analysis
        {
            Sources = sources,
            Groups = groups,
            MinBackupCopies = minBackupCopies,
            PerSource = perSource,
            AtRisk = groups.Where(g => backupCopies[g] < minBackupCopies)
                .OrderBy(g => backupCopies[g])
                .ThenBy(g => g.Taken ?? DateTime.MaxValue)
                .ToList(),
            ByBackupCopies = byCopies,
            Overlap = overlap,
        };
    }
}
