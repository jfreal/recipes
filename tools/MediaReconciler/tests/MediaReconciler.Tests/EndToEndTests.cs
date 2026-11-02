using MediaReconciler.Config;
using MediaReconciler.Hashing;
using MediaReconciler.Matching;
using MediaReconciler.Reporting;

namespace MediaReconciler.Tests;

public class EndToEndTests
{
    [Fact]
    public async Task Reconciles_an_icloud_mirror_a_dropbox_folder_and_a_phone_manifest()
    {
        var root = TestData.TempDirectory();
        var icloud = Directory.CreateDirectory(Path.Combine(root, "icloud")).FullName;
        var dropbox = Directory.CreateDirectory(Path.Combine(root, "dropbox")).FullName;
        var t1 = new DateTime(2024, 5, 1, 12, 0, 0);
        var t2 = new DateTime(2024, 5, 2, 9, 30, 15);

        var photo1 = TestData.JpegWithExif(t1, "-07:00", padding: 1);
        var photo2 = TestData.JpegWithExif(t2, "-07:00", padding: 2);
        File.WriteAllBytes(Path.Combine(icloud, "IMG_0001.JPG"), photo1);
        File.WriteAllBytes(Path.Combine(icloud, "IMG_0002.JPG"), photo2);

        // Dropbox camera upload: renamed by date, and a different encode (different bytes).
        File.WriteAllBytes(Path.Combine(dropbox, "2024-05-01 12.00.00.jpg"), TestData.JpegWithExif(t1, null, padding: 99));

        // The phone (from another program's CSV): IMG_0003 isn't backed up anywhere.
        var manifest = Path.Combine(root, "phone.csv");
        File.WriteAllLines(manifest,
        [
            "name,size,hash",
            $"IMG_0001.JPG,{photo1.Length},{DropboxContentHasher.Compute(photo1)}",
            $"IMG_0003.JPG,5000,",
        ]);

        var configPath = Path.Combine(root, "reconcile.json");
        File.WriteAllText(configPath, $$"""
            {
              "sources": [
                { "name": "iCloud", "type": "folder", "path": "icloud" },
                { "name": "Dropbox", "type": "folder", "path": "dropbox" },
                { "name": "Phone", "type": "manifest", "path": "phone.csv", "isBackup": false }
              ],
              "minBackupCopies": 2
            }
            """);

        var config = ReconcileConfig.Load(configPath);
        using var http = new HttpClient();
        var analysis = await new Reconciler(config, ScanCache.InMemory(), http, _ => { }).RunAsync(default);
        var summary = ReportWriter.Write(analysis, config.OutputDirectory);

        Assert.Equal(3, analysis.Groups.Count);

        var img1 = Assert.Single(analysis.Groups, g => g.Representative.FileName == "IMG_0001.JPG");
        Assert.Equal(3, img1.Sources.Count());
        Assert.Equal(2, analysis.BackupCopies(img1));

        var img3 = Assert.Single(analysis.AtRisk, g => g.Representative.FileName == "IMG_0003.JPG");
        Assert.Equal(0, analysis.BackupCopies(img3));

        Assert.Contains("are in no backup at all", summary);
        var safe = File.ReadAllLines(Path.Combine(config.OutputDirectory, "safe-to-remove-from-phone.csv"));
        // IMG_0001 is in two backups, but the Dropbox copy only matches by capture time (Weak),
        // so it isn't evidence enough to delete the phone's copy.
        Assert.Equal(MatchStrength.Weak, img1.Strength);
        Assert.Single(safe);
    }
}
