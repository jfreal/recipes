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
            // Equal sizes should mean identical bytes, so never let an unhashed item join groups
            // whose known hashes differ: that would bridge two different photos into one.
            UnionPairs(sets, bucket, (_, _) => true, MatchStrength.Strong, requireCompatibleHashes: true);
        }

        // 3. Name + kind + capture time (loosely: whole quarter-hour time-zone shifts allowed).
        UnionByNameAndTime(sets, items);

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

    /// <summary>
    /// Rule 3 without comparing every same-named file with every other (a folder of thousands of
    /// "image.jpg" would otherwise crawl). Times that differ by whole quarter hours sit at the same
    /// second within the quarter hour, so bucketing on that second (±2 s) finds every pair
    /// <see cref="SameTimeLoosely"/> could accept.
    /// </summary>
    private static void UnionByNameAndTime(DisjointSets sets, IReadOnlyList<MediaItem> items)
    {
        const int QuarterSeconds = 15 * 60;
        var buckets = new Dictionary<(string Stem, MediaKind Kind, int Phase), List<int>>();

        for (var x = 0; x < items.Count; x++)
        {
            var item = items[x];
            if (!item.HasOriginalName) continue;

            var phases = new HashSet<int>();
            foreach (var (time, _) in CaptureTimes(item)) phases.Add(Phase(time));
            if (phases.Count == 0) continue;

            var candidates = new SortedSet<int>();
            foreach (var phase in phases)
            {
                for (var d = -2; d <= 2; d++)
                {
                    var neighbour = ((phase + d) % QuarterSeconds + QuarterSeconds) % QuarterSeconds;
                    if (buckets.TryGetValue((item.Stem, item.Kind, neighbour), out var list)) candidates.UnionWith(list);
                }
            }

            foreach (var y in candidates)
            {
                if (SameTimeLoosely(items[y], item) &&
                    sets.Union(y, x, MatchStrength.Strong, requireDisjointSources: true)) break;
            }

            foreach (var phase in phases)
            {
                if (!buckets.TryGetValue((item.Stem, item.Kind, phase), out var list)) buckets[(item.Stem, item.Kind, phase)] = list = [];
                list.Add(x);
            }
        }

        static int Phase(DateTime time) => (int)(time.Ticks / TimeSpan.TicksPerSecond % QuarterSeconds);
    }

    /// <summary>Same instant, or same wall-clock time, within two seconds. Ignores file timestamps.</summary>
    internal static bool SameTimeStrictly(MediaItem a, MediaItem b) =>
        (a.TakenLocal is { } la && b.TakenLocal is { } lb && Abs(la - lb) <= Tolerance) ||
        (a.TakenUtc is { } ua && b.TakenUtc is { } ub && !a.UtcIsFileTime && !b.UtcIsFileTime && Abs(ua - ub) <= Tolerance);

    /// <summary>
    /// Strict match, or a camera wall-clock time and a UTC time that differ by a whole number of
    /// quarter hours up to 14 h (zero included, for UTC+0): the same moment seen in two time
    /// bases. Two times on the same basis must agree to the second; a zone shift can't explain
    /// a gap between them.
    /// </summary>
    internal static bool SameTimeLoosely(MediaItem a, MediaItem b)
    {
        if (SameTimeStrictly(a, b)) return true;

        foreach (var (ta, baseA) in CaptureTimes(a))
        {
            foreach (var (tb, baseB) in CaptureTimes(b))
            {
                var delta = Abs(ta - tb);
                if (baseA == baseB)
                {
                    if (delta <= Tolerance) return true;
                    continue;
                }

                if (delta > MaxZoneShift + Tolerance) continue;
                var offStep = delta.Ticks % ZoneStepTicks;
                if (Math.Min(offStep, ZoneStepTicks - offStep) <= Tolerance.Ticks) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// When a picture was taken, on its time basis (camera wall clock or UTC). File modified
    /// times are left out: files copied together share them, so they'd join different photos.
    /// </summary>
    private static IEnumerable<(DateTime Time, bool IsLocal)> CaptureTimes(MediaItem item)
    {
        if (item.TakenLocal is { } local) yield return (local, true);
        if (item.TakenUtc is { } utc && !item.UtcIsFileTime) yield return (utc, false);
    }

    // Buckets by capture minute; pairs are then checked to the second (a pair that straddles
    // a minute boundary is missed, which only makes the tool more cautious).
    private static string? TimeBucket(MediaItem item)
    {
        var time = item.TakenLocal ?? (item.UtcIsFileTime ? null : item.TakenUtc);
        return time is { } t ? $"{item.Kind}|{(item.TakenLocal is null ? "u" : "l")}|{t:yyyyMMddHHmm}" : null;
    }

    private static void UnionPairs(
        DisjointSets sets, List<int> bucket, Func<int, int, bool> sameItem, MatchStrength strength, bool requireCompatibleHashes = false)
    {
        for (var x = 1; x < bucket.Count; x++)
        {
            for (var y = 0; y < x; y++)
            {
                if (sameItem(bucket[y], bucket[x]) &&
                    sets.Union(bucket[y], bucket[x], strength, requireDisjointSources: true, requireCompatibleHashes)) break;
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
        public bool Union(int a, int b, MatchStrength strength, bool requireDisjointSources, bool requireCompatibleHashes = false)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb) return true;
            if (requireDisjointSources && _sources[ra].Overlaps(_sources[rb])) return false;

            // Recompressed copies (rules 3-4) legitimately differ in hash; same-size matches don't.
            if (requireCompatibleHashes && _hashes[ra].Count > 0 && _hashes[rb].Count > 0 && !_hashes[ra].Overlaps(_hashes[rb])) return false;

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
