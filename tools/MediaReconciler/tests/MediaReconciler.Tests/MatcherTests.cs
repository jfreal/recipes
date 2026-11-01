using MediaReconciler.Matching;
using static MediaReconciler.Tests.TestData;

namespace MediaReconciler.Tests;

public class MatcherTests
{
    private static readonly DateTime Noon = new(2024, 5, 1, 12, 0, 0);

    [Fact]
    public void Identical_bytes_match_whatever_the_names()
    {
        var groups = Matcher.Match([Item(0, "IMG_0001.HEIC", hash: "aa"), Item(1, "renamed.heic", hash: "aa")]);

        var group = Assert.Single(groups);
        Assert.Equal(MatchStrength.Exact, group.Strength);
        Assert.Equal([0, 1], group.Sources.Order());
    }

    [Fact]
    public void Identical_bytes_twice_in_one_source_are_duplicates_in_one_group()
    {
        var groups = Matcher.Match([Item(0, "a.jpg", hash: "aa"), Item(0, "copy of a.jpg", hash: "aa")]);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Items.Count);
    }

    [Fact]
    public void Same_name_and_size_match_when_one_side_has_no_hash()
    {
        var groups = Matcher.Match([Item(0, "IMG_0001.HEIC", size: 2_345_678, hash: "aa"), Item(1, "IMG_0001.HEIC", size: 2_345_678)]);

        Assert.Equal(MatchStrength.Strong, Assert.Single(groups).Strength);
    }

    [Fact]
    public void Same_name_but_different_hashes_and_times_are_different_photos()
    {
        // Two phones both reach IMG_0001 eventually.
        var groups = Matcher.Match([
            Item(0, "IMG_0001.HEIC", size: 500, hash: "aa", local: Noon),
            Item(1, "IMG_0001.HEIC", size: 500, hash: "bb", local: Noon.AddDays(40)),
        ]);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Recompressed_copy_matches_by_name_and_time_across_time_zones()
    {
        // iCloud original has camera local time; Google's copy has a UTC timestamp 7 h later.
        var groups = Matcher.Match([
            Item(0, "IMG_0001.HEIC", size: 3_000_000, hash: "aa", local: Noon),
            Item(1, "IMG_0001.HEIC", size: 1_200_000, hash: "bb", utc: Noon.AddHours(7)),
        ]);

        Assert.Equal(MatchStrength.Strong, Assert.Single(groups).Strength);
    }

    [Fact]
    public void Name_match_needs_compatible_times()
    {
        var groups = Matcher.Match([
            Item(0, "IMG_0001.HEIC", size: 3_000_000, local: Noon),
            Item(1, "IMG_0001.HEIC", size: 1_200_000, utc: Noon.AddHours(7).AddMinutes(3)),
        ]);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Dropbox_renamed_and_converted_copy_is_a_weak_match()
    {
        var groups = Matcher.Match([
            Item(0, "IMG_0001.HEIC", size: 3_000_000, hash: "aa", local: Noon),
            Item(1, "2024-05-01 12.00.00.jpg", size: 2_100_000, hash: "cc", local: Noon, utc: Noon.AddHours(7), utcIsFileTime: true),
        ]);

        Assert.Equal(MatchStrength.Weak, Assert.Single(groups).Strength);
    }

    [Fact]
    public void Time_only_matching_can_be_turned_off()
    {
        var groups = Matcher.Match(
            [Item(0, "IMG_0001.HEIC", local: Noon), Item(1, "2024-05-01 12.00.00.jpg", local: Noon)],
            timeOnlyMatching: false);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Two_people_photographing_the_same_second_are_not_matched()
    {
        // Both names are real camera names and they differ: different photos.
        var groups = Matcher.Match([Item(0, "IMG_0001.HEIC", local: Noon), Item(1, "IMG_9876.HEIC", local: Noon)]);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void A_burst_never_collapses_into_one_picture()
    {
        // Three burst shots in one second on the phone; Dropbox only has two of them.
        var groups = Matcher.Match([
            Item(0, "IMG_0001.HEIC", local: Noon),
            Item(0, "IMG_0002.HEIC", local: Noon),
            Item(0, "IMG_0003.HEIC", local: Noon),
            Item(1, "2024-05-01 12.00.00.jpg", local: Noon),
            Item(1, "2024-05-01 12.00.00-1.jpg", local: Noon),
        ]);

        Assert.Equal(3, groups.Count);
        Assert.Equal(2, groups.Count(g => g.Sources.Count() == 2));
        Assert.All(groups, g => Assert.Equal(g.Items.Count, g.Sources.Count()));
    }

    [Fact]
    public void File_timestamps_alone_never_make_a_time_only_match()
    {
        var groups = Matcher.Match([
            Item(0, "IMG_0001.HEIC", utc: Noon, utcIsFileTime: true),
            Item(1, "2024-05-01 19.00.00.jpg", utc: Noon, utcIsFileTime: true),
        ]);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Live_photo_still_and_video_stay_separate()
    {
        var groups = Matcher.Match([
            Item(0, "IMG_0001.HEIC", local: Noon),
            Item(0, "IMG_0001.MOV", local: Noon),
            Item(1, "IMG_0001.HEIC", local: Noon),
            Item(1, "IMG_0001_HEVC.MOV", local: Noon),
        ]);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(2, g.Sources.Count()));
    }

    [Fact]
    public void Many_sources_chain_into_one_group()
    {
        var groups = Matcher.Match([
            Item(0, "IMG_0001.HEIC", size: 100, hash: "aa", local: Noon),        // iCloud mirror
            Item(1, "IMG_0001.HEIC", size: 100, hash: "aa"),                     // Dropbox A, kept as HEIC
            Item(2, "IMG_0001.HEIC", size: 80, hash: "bb", utc: Noon.AddHours(-2)), // Google, recompressed
            Item(3, "IMG_0001.HEIC", size: 100),                                 // iPhone, not hashed
            Item(4, "2024-05-01 12.00.00.jpg", size: 70, local: Noon),           // Dropbox B, converted
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(5, group.Sources.Count());
        Assert.Equal(MatchStrength.Weak, group.Strength);
    }
}
