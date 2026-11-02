using MediaReconciler.Model;

namespace MediaReconciler.Matching;

public enum MatchStrength
{
    /// <summary>Identical bytes (same content hash).</summary>
    Exact = 0,

    /// <summary>Same original file name, and the same size or capture time.</summary>
    Strong = 1,

    /// <summary>Same capture time to the second, where one copy was renamed. Spot-check these.</summary>
    Weak = 2,
}

/// <summary>One picture, and every copy of it that was found.</summary>
public sealed class MatchGroup
{
    public required int Id { get; init; }
    public required IReadOnlyList<MediaItem> Items { get; init; }
    public required MatchStrength Strength { get; init; }

    public IEnumerable<int> Sources => Items.Select(i => i.SourceIndex).Distinct();

    public MediaItem Representative =>
        Items.FirstOrDefault(i => i.HasOriginalName && i.LocalPath is not null)
        ?? Items.FirstOrDefault(i => i.HasOriginalName)
        ?? Items[0];

    public long Size => Items.Max(i => i.Size);

    public DateTime? Taken =>
        Items.Select(i => i.TakenLocal).FirstOrDefault(t => t is not null)
        ?? Items.Where(i => !i.UtcIsFileTime).Select(i => i.TakenUtc).FirstOrDefault(t => t is not null)
        ?? Items.Select(i => i.TakenUtc).FirstOrDefault(t => t is not null);

    public bool IsIn(int sourceIndex) => Items.Any(i => i.SourceIndex == sourceIndex);
}

/// <summary>
/// Decides which items across all sources are the same picture, from most to least certain:
/// <list type="number">
/// <item>Same content hash → the same bytes (also catches duplicates inside one source).</item>
/// <item>Same name, extension and size (the iPhone without hashing vs. an iCloud mirror).</item>
/// <item>Same name and the same capture time, allowing for a time-zone shift (Google keeps
/// the name but may recompress; Live Photo videos become .MP4).</item>
/// <item>Same capture time to the second when one copy was renamed (Dropbox camera uploads).</item>
/// </list>
/// Rules 2–4 never put two items from the same source in one group, so a burst of photos
/// taken in the same second can't collapse into one and hide a missing copy.
/// </summary>
public static class Matcher
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxZoneShift = TimeSpan.FromHours(14);
    private static readonly long ZoneStepTicks = TimeSpan.FromMinutes(15).Ticks;
    private static readonly TimeSpan ZoneStepTicksSpan = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<MatchGroup> Match(IReadOnlyList<MediaItem> items, bool timeOnlyMatching = true)
    {
        var sets = new DisjointSets(items);

        // 1. Content hash.
        foreach (var bucket in GroupIndexes(items, i => i.ContentHash))
        {
            for (var k = 1; k < bucket.Count; k++) sets.Union(bucket[0], bucket[k], MatchStrength.Exact, requireDisjointSources: false);
        }

        // 2. Name + extension + size.
        foreach (var bucket in GroupIndexes(items, i => i.Size > 0 ? $"{i.Stem}|{i.Extension}|{i.Size}" : null))
        {
            // Two hashed files with different hashes are different files, whatever their names.
            UnionPairs(sets, bucket, (a, b) => !HashesDiffer(items[a], items[b]), MatchStrength.Strong);
        }

        // 3. Name + kind + capture time (loosely: whole quarter-hour time-zone shifts allowed).
        foreach (var bucket in GroupIndexes(items, i => i.HasOriginalName ? $"{i.Stem}|{i.Kind}" : null))
        {
            UnionPairs(sets, bucket, (a, b) => SameTimeLoosely(items[a], items[b]), MatchStrength.Strong);
        }

        // 4. Capture time alone, only when at least one side was renamed from the date.
        if (timeOnlyMatching)
        {
            foreach (var bucket in GroupIndexes(items, TimeBucket))
            {
                UnionPairs(
                    sets,
                    bucket,
                    (a, b) => (!items[a].HasOriginalName || !items[b].HasOriginalName) &&
                              items[a].Kind == items[b].Kind &&
                              SameTimeStrictly(items[a], items[b]),
                    MatchStrength.Weak);
            }
        }

        return sets.ToGroups();
    }

    /// <summary>Same instant, or same wall-clock time, within two seconds. Ignores file timestamps.</summary>
    internal static bool SameTimeStrictly(MediaItem a, MediaItem b) =>
        (a.TakenLocal is { } la && b.TakenLocal is { } lb && Abs(la - lb) <= Tolerance) ||
        (a.TakenUtc is { } ua && b.TakenUtc is { } ub && !a.UtcIsFileTime && !b.UtcIsFileTime && Abs(ua - ub) <= Tolerance);

    /// <summary>
    /// Strict match, or any pair of known times (file timestamps included) that differ by a
    /// whole number of quarter hours up to 14 h: the same moment seen in different time zones.
    /// </summary>
    internal static bool SameTimeLoosely(MediaItem a, MediaItem b)
    {
        if (SameTimeStrictly(a, b)) return true;

        foreach (var ta in Times(a))
        {
            foreach (var tb in Times(b))
            {
                var delta = Abs(ta - tb);
                // A zero shift is not a time-zone difference; the strict check above owns equal times.
                if (delta < ZoneStepTicksSpan - Tolerance) continue;
                if (delta > MaxZoneShift + Tolerance) continue;
                var offStep = delta.Ticks % ZoneStepTicks;
                if (Math.Min(offStep, ZoneStepTicks - offStep) <= Tolerance.Ticks) return true;
            }
        }

        return false;

        static IEnumerable<DateTime> Times(MediaItem item)
        {
            if (item.TakenLocal is { } local) yield return local;
            if (item.TakenUtc is { } utc) yield return utc;
        }
    }

    // Buckets by capture minute; pairs are then checked to the second (a pair that straddles
    // a minute boundary is missed, which only makes the tool more cautious).
    private static string? TimeBucket(MediaItem item)
    {
        var time = item.TakenLocal ?? (item.UtcIsFileTime ? null : item.TakenUtc);
        return time is { } t ? $"{item.Kind}|{(item.TakenLocal is null ? "u" : "l")}|{t:yyyyMMddHHmm}" : null;
    }

    private static void UnionPairs(DisjointSets sets, List<int> bucket, Func<int, int, bool> sameItem, MatchStrength strength)
    {
        for (var x = 1; x < bucket.Count; x++)
        {
            for (var y = 0; y < x; y++)
            {
                if (sameItem(bucket[y], bucket[x]) && sets.Union(bucket[y], bucket[x], strength, requireDisjointSources: true)) break;
            }
        }
    }

    private static IEnumerable<List<int>> GroupIndexes(IReadOnlyList<MediaItem> items, Func<MediaItem, string?> key)
    {
        var buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            if (key(items[i]) is not { } k) continue;
            if (!buckets.TryGetValue(k, out var list)) buckets[k] = list = [];
            list.Add(i);
        }

        return buckets.Values.Where(b => b.Count > 1);
    }

    private static bool HashesDiffer(MediaItem a, MediaItem b) =>
        a.ContentHash is not null && b.ContentHash is not null && a.ContentHash != b.ContentHash;

    private static TimeSpan Abs(TimeSpan span) => span < TimeSpan.Zero ? -span : span;

    private sealed class DisjointSets
    {
        private readonly IReadOnlyList<MediaItem> _items;
        private readonly int[] _parent;
        private readonly HashSet<int>[] _sources;
        private readonly MatchStrength[] _strength;
        private readonly HashSet<string>[] _hashes;

        public DisjointSets(IReadOnlyList<MediaItem> items)
        {
            _items = items;
            _parent = new int[items.Count];
            _sources = new HashSet<int>[items.Count];
            _strength = new MatchStrength[items.Count];
            _hashes = new HashSet<string>[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                _parent[i] = i;
                _sources[i] = [items[i].SourceIndex];
                _hashes[i] = items[i].ContentHash is { } h ? [h] : [];
            }
        }

        public int Find(int i)
        {
            while (_parent[i] != i)
            {
                _parent[i] = _parent[_parent[i]];
                i = _parent[i];
            }

            return i;
        }

        /// <summary>Merges two groups. Returns true if they are (now) the same group.</summary>
        public bool Union(int a, int b, MatchStrength strength, bool requireDisjointSources)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb) return true;
            if (requireDisjointSources && _sources[ra].Overlaps(_sources[rb])) return false;

            // An unhashed item must not bridge two groups whose known hashes differ.
            if (strength != MatchStrength.Exact && _hashes[ra].Count > 0 && _hashes[rb].Count > 0 && !_hashes[ra].Overlaps(_hashes[rb])) return false;

            if (_sources[ra].Count < _sources[rb].Count) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            _sources[ra].UnionWith(_sources[rb]);
            _hashes[ra].UnionWith(_hashes[rb]);
            _strength[ra] = (MatchStrength)Math.Max((int)Max(_strength[ra], _strength[rb]), (int)strength);
            return true;
        }

        public IReadOnlyList<MatchGroup> ToGroups()
        {
            var members = new Dictionary<int, List<MediaItem>>();
            for (var i = 0; i < _items.Count; i++)
            {
                var root = Find(i);
                if (!members.TryGetValue(root, out var list)) members[root] = list = [];
                list.Add(_items[i]);
            }

            var id = 0;
            return members
                .Select(m => (Root: m.Key, Items: m.Value))
                .OrderBy(m => m.Items[0].SourceIndex)
                .ThenBy(m => m.Items[0].Location, StringComparer.Ordinal)
                .Select(m => new MatchGroup { Id = ++id, Items = m.Items, Strength = _strength[m.Root] })
                .ToList();
        }

        private static MatchStrength Max(MatchStrength a, MatchStrength b) => a > b ? a : b;
    }
}
