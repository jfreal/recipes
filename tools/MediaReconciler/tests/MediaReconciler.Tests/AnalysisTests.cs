using MediaReconciler.Matching;
using MediaReconciler.Reporting;
using static MediaReconciler.Tests.TestData;

namespace MediaReconciler.Tests;

public class AnalysisTests
{
    // 0 = iPhone (not a backup), 1 = iCloud, 2 = Dropbox, 3 = Google
    private static readonly SourceInfo[] Sources =
    [
        new(0, "iPhone", "iphone", IsBackup: false),
        new(1, "iCloud", "folder", IsBackup: true),
        new(2, "Dropbox", "dropbox", IsBackup: true),
        new(3, "Google", "google-takeout", IsBackup: true),
    ];

    private static Analysis Build() => Analysis.Compute(
        Sources,
        Matcher.Match([
            // a: everywhere
            Item(0, "a.heic", size: 100, hash: "a"), Item(1, "a.heic", size: 100, hash: "a"),
            Item(2, "a.heic", size: 100, hash: "a"), Item(3, "a.heic", size: 100, hash: "a"),
            // b: phone + iCloud only
            Item(0, "b.heic", size: 200, hash: "b"), Item(1, "b.heic", size: 200, hash: "b"),
            // c: only on the phone
            Item(0, "c.heic", size: 300, hash: "c"),
            // d: iCloud + Google, plus a duplicate copy inside Google
            Item(1, "d.heic", size: 400, hash: "d"), Item(3, "d.heic", size: 400, hash: "d"),
            Item(3, "d copy.heic", size: 400, hash: "d"),
        ]),
        minBackupCopies: 2);

    [Fact]
    public void Counts_backup_copies_and_finds_items_at_risk()
    {
        var analysis = Build();

        Assert.Equal(4, analysis.Groups.Count);
        Assert.Equal(1, analysis.ByBackupCopies[0].Count); // c
        Assert.Equal(1, analysis.ByBackupCopies[1].Count); // b
        Assert.Equal(1, analysis.ByBackupCopies[2].Count); // d
        Assert.Equal(1, analysis.ByBackupCopies[3].Count); // a (3+)

        Assert.Equal(["c.heic", "b.heic"], analysis.AtRisk.Select(g => g.Representative.FileName));
    }

    [Fact]
    public void Phone_items_with_enough_backups_are_safe_to_remove()
    {
        var phone = Build().PerSource[0];

        Assert.Equal(new Tally(1, 100), phone.SafeToRemove); // only a
        Assert.Equal(new Tally(1, 300), phone.OnlyHere);      // c
    }

    [Fact]
    public void Dropping_a_backup_shows_what_would_be_lost()
    {
        var analysis = Build();
        var icloud = analysis.PerSource[1];
        var google = analysis.PerSource[3];

        Assert.Equal(new Tally(1, 200), icloud.LostIfDropped);  // b exists in no other backup
        Assert.Equal(1, icloud.BelowMinimumIfDropped.Count);     // d falls to 1 copy
        Assert.Equal(new Tally(0, 0), google.LostIfDropped);
        Assert.Equal(1, google.BelowMinimumIfDropped.Count);     // d
    }

    [Fact]
    public void Duplicate_copies_inside_one_source_are_counted_once_extra()
    {
        var google = Build().PerSource[3];

        Assert.Equal(new Tally(1, 400), google.DuplicateCopies);
        Assert.Equal(3, google.Stored.Count);
        Assert.Equal(2, google.Pictures);
    }

    [Fact]
    public void Overlap_matrix_counts_shared_pictures()
    {
        var analysis = Build();

        Assert.Equal(3, analysis.Overlap[0, 0]); // phone has a, b, c
        Assert.Equal(2, analysis.Overlap[0, 1]); // a, b also in iCloud
        Assert.Equal(1, analysis.Overlap[0, 2]); // a in Dropbox
    }

    [Fact]
    public void Report_files_are_written()
    {
        var dir = TempDirectory();
        var summary = ReportWriter.Write(Build(), dir);

        Assert.Contains("1 items (300 B) are in no backup at all", summary);
        Assert.Contains("on **iPhone** is already in 2+ backups", summary);
        foreach (var file in new[]
                 {
                     "summary.md", "all-pictures.csv", "at-risk.csv", "weak-matches.csv",
                     "missing-from-icloud.csv", "missing-from-dropbox.csv", "missing-from-google.csv",
                     "safe-to-remove-from-iphone.csv", "duplicates-in-google.csv",
                 })
        {
            Assert.True(File.Exists(Path.Combine(dir, file)), file);
        }

        var missingFromDropbox = File.ReadAllLines(Path.Combine(dir, "missing-from-dropbox.csv"));
        Assert.Equal(4, missingFromDropbox.Length); // header + b, c, d
    }
}
