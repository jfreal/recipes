using System.IO.Compression;
using System.Text;
using MediaReconciler.Config;
using MediaReconciler.Hashing;
using MediaReconciler.Sources;

namespace MediaReconciler.Tests;

public class SourceTests
{
    private static readonly DateTime Taken = new(2024, 5, 1, 12, 34, 56);

    private static ScanContext Context(SourceConfig config, int index = 0) =>
        new(index, config, ScanCache.InMemory(), _ => { });

    [Fact]
    public async Task Folder_source_hashes_and_reads_exif()
    {
        var dir = TestData.TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir, "2024", ".dropbox.cache"));
        var jpeg = TestData.JpegWithExif(Taken, "+02:00");
        File.WriteAllBytes(Path.Combine(dir, "2024", "IMG_0001.JPG"), jpeg);
        File.WriteAllText(Path.Combine(dir, "2024", "notes.txt"), "not media");
        File.WriteAllBytes(Path.Combine(dir, "2024", ".dropbox.cache", "IMG_0001.JPG"), jpeg);

        var items = await new FolderSource().ScanAsync(Context(new SourceConfig { Name = "f", Type = "folder", Path = dir }), default);

        var item = Assert.Single(items);
        Assert.Equal(Path.Combine("2024", "IMG_0001.JPG"), item.Location);
        Assert.Equal(DropboxContentHasher.Compute(jpeg), item.ContentHash);
        Assert.Equal(Taken, item.TakenLocal);
        Assert.Equal(Taken.AddHours(-2), item.TakenUtc);
        Assert.False(item.UtcIsFileTime);
    }

    [Fact]
    public async Task Folder_source_uses_the_cache_on_the_second_run()
    {
        var dir = TestData.TempDirectory();
        File.WriteAllBytes(Path.Combine(dir, "IMG_0001.JPG"), TestData.JpegWithExif(Taken, null));
        var cachePath = Path.Combine(dir, "cache", "scan-cache.json");
        var config = new SourceConfig { Name = "f", Type = "folder", Path = dir };

        var first = await new FolderSource().ScanAsync(new ScanContext(0, config, ScanCache.Load(cachePath), _ => { }), default);
        Assert.True(File.Exists(cachePath));

        var cache = ScanCache.Load(cachePath);
        var key = ScanCache.Key(Path.Combine(dir, "IMG_0001.JPG"), new FileInfo(Path.Combine(dir, "IMG_0001.JPG")).Length,
            File.GetLastWriteTimeUtc(Path.Combine(dir, "IMG_0001.JPG")));
        Assert.True(cache.TryGet(key, out var entry));
        Assert.Equal(first[0].ContentHash, entry.Hash);
    }

    [Fact]
    public async Task Takeout_reads_zips_sidecars_across_parts_and_skips_album_copies_and_edits()
    {
        var dir = TestData.TempDirectory();
        var photo = TestData.JpegWithExif(Taken, null, padding: 10);
        var longName = "A very long original file name that Google truncates.jpg";
        var truncated = longName[..47] + ".jpg";
        var unix = new DateTimeOffset(2024, 5, 1, 19, 34, 56, TimeSpan.Zero).ToUnixTimeSeconds();

        // Part 1: the photos. Part 2: their sidecars, plus an album copy and an edited copy.
        WriteZip(Path.Combine(dir, "takeout-001.zip"), new()
        {
            ["Takeout/Google Photos/Photos from 2024/IMG_0001.JPG"] = photo,
            ["Takeout/Google Photos/Photos from 2024/IMG_0001(1).JPG"] = TestData.JpegWithExif(Taken, null, padding: 20),
            ["Takeout/Google Photos/Photos from 2024/" + truncated] = TestData.JpegWithExif(Taken, null, padding: 30),
        });
        WriteZip(Path.Combine(dir, "takeout-002.zip"), new()
        {
            ["Takeout/Google Photos/Photos from 2024/IMG_0001.JPG.supplemental-metadata(1).json"] = Sidecar("IMG_0001.JPG", unix + 1),
            ["Takeout/Google Photos/Photos from 2024/IMG_0001.JPG.supplemental-metadata.json"] = Sidecar("IMG_0001.JPG", unix),
            ["Takeout/Google Photos/Photos from 2024/huge.json"] = Encoding.UTF8.GetBytes(
                """{ "title": "x.jpg", "photoTakenTime": { "timestamp": "99999999999999999999" } }"""),
            ["Takeout/Google Photos/Photos from 2024/far.json"] = Encoding.UTF8.GetBytes(
                """{ "title": "y.jpg", "photoTakenTime": { "timestamp": 999999999999999 } }"""),
            ["Takeout/Google Photos/Photos from 2024/" + longName[..40] + ".json"] = Sidecar(longName, unix + 2),
            ["Takeout/Google Photos/Holiday/IMG_0001.JPG"] = photo,
            ["Takeout/Google Photos/Holiday/metadata.json"] = Encoding.UTF8.GetBytes("""{ "title": "Holiday" }"""),
            ["Takeout/Google Photos/Photos from 2024/IMG_0001-edited.JPG"] = TestData.JpegWithExif(Taken, null, padding: 40),
        });

        var context = Context(new SourceConfig { Name = "g", Type = "google-takeout", Path = dir });
        var items = await new GoogleTakeoutSource().ScanAsync(context, default);

        Assert.Equal(3, items.Count);
        var first = Assert.Single(items, i => i.Location == "takeout-001.zip!Takeout/Google Photos/Photos from 2024/IMG_0001.JPG");
        Assert.Equal(new DateTime(2024, 5, 1, 19, 34, 56), first.TakenUtc);
        Assert.False(first.UtcIsFileTime);
        Assert.Equal(DropboxContentHasher.Compute(photo), first.ContentHash);

        var duplicateName = Assert.Single(items, i => i.Location.EndsWith("IMG_0001(1).JPG"));
        Assert.Equal("IMG_0001.JPG", duplicateName.FileName);
        Assert.Equal(new DateTime(2024, 5, 1, 19, 34, 57), duplicateName.TakenUtc);

        var restored = Assert.Single(items, i => i.Location.EndsWith(truncated));
        Assert.Equal(longName, restored.FileName);
        Assert.Equal(new DateTime(2024, 5, 1, 19, 34, 58), restored.TakenUtc);

        Assert.Contains(context.Notes, n => n.Contains("-edited"));
    }

    [Fact]
    public void Manifest_rows_become_items()
    {
        var items = ManifestSource.Parse(
            [
                "name,size,hash,taken_utc,location",
                "IMG_0001.HEIC,123,ABCDEF,2024-05-01T19:34:56Z,DCIM/100APPLE/IMG_0001.HEIC",
                "\"Holiday, day 1.jpg\",,,,",
                "notes.txt,1,,,",
            ],
            sourceIndex: 2,
            out var skipped);

        Assert.Equal(1, skipped);
        Assert.Equal(2, items.Count);
        Assert.Equal("abcdef", items[0].ContentHash);
        Assert.Equal(123, items[0].Size);
        Assert.Equal(new DateTime(2024, 5, 1, 19, 34, 56, DateTimeKind.Utc), items[0].TakenUtc);
        Assert.Equal("Holiday, day 1.jpg", items[1].FileName);
        Assert.Equal(-1, items[1].Size);
    }

    [Fact]
    public void Config_resolves_paths_and_secrets()
    {
        var dir = TestData.TempDirectory();
        var path = Path.Combine(dir, "reconcile.json");
        File.WriteAllText(path, """
            {
              // comments are allowed
              "sources": [
                { "name": "a", "type": "folder", "path": "photos" },
                { "name": "b", "type": "dropbox", "path": "/Camera Uploads", "refreshToken": "env:MR_TEST_TOKEN" },
                { "name": "c", "type": "iphone" },
              ],
            }
            """);

        var config = ReconcileConfig.Load(path);

        Assert.Equal(Path.Combine(dir, "photos"), config.Sources[0].Path);
        Assert.Equal("/Camera Uploads", config.Sources[1].Path);
        Assert.Equal(Path.Combine(dir, "report"), config.OutputDirectory);
        Assert.Null(config.CachePath);
        Assert.True(config.Sources[0].CountsAsBackup);
        Assert.False(config.Sources[2].CountsAsBackup);

        Environment.SetEnvironmentVariable("MR_TEST_TOKEN", "secret");
        Assert.Equal("secret", SourceConfig.ResolveSecret(config.Sources[1].RefreshToken));
    }

    [Fact]
    public void Relative_cache_path_is_relative_to_the_config()
    {
        var dir = TestData.TempDirectory();
        var path = Path.Combine(dir, "reconcile.json");
        File.WriteAllText(path, """
            { "cachePath": "cache/scan.json", "sources": [ { "name": "a", "type": "folder" }, { "name": "b", "type": "folder" } ] }
            """);

        Assert.Equal(Path.Combine(dir, "cache", "scan.json"), ReconcileConfig.Load(path).ResolveCachePath());
    }

    [Fact]
    public void Sample_config_is_valid()
    {
        var dir = TestData.TempDirectory();
        var path = Path.Combine(dir, "reconcile.json");
        File.WriteAllText(path, SampleConfig.Json);

        Assert.Equal(4, ReconcileConfig.Load(path).Sources.Count);
    }

    [Fact]
    public void Duplicate_source_names_are_rejected()
    {
        var path = Path.Combine(TestData.TempDirectory(), "reconcile.json");
        File.WriteAllText(path, """{ "sources": [ { "name": "a", "type": "folder" }, { "name": "A", "type": "folder" } ] }""");

        Assert.Throws<ConfigException>(() => ReconcileConfig.Load(path));
    }

    private static byte[] Sidecar(string title, long unix) => Encoding.UTF8.GetBytes($$"""
        { "title": "{{title}}", "photoTakenTime": { "timestamp": "{{unix}}", "formatted": "..." } }
        """);

    private static void WriteZip(string path, Dictionary<string, byte[]> files)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in files)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(bytes);
        }
    }
}
