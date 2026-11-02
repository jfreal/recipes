using System.Security.Cryptography;
using MediaReconciler.Hashing;
using MediaReconciler.Metadata;
using MediaReconciler.Model;

namespace MediaReconciler.Tests;

public class HashingAndNamesTests
{
    [Fact]
    public void Empty_content_hashes_to_sha256_of_nothing()
    {
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            DropboxContentHasher.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(DropboxContentHasher.BlockSize)]
    [InlineData(DropboxContentHasher.BlockSize + 1)]
    [InlineData(2 * DropboxContentHasher.BlockSize + 12345)]
    public void Content_hash_is_sha256_of_the_4MiB_block_hashes(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);

        var blockHashes = new List<byte>();
        for (var offset = 0; offset < length; offset += DropboxContentHasher.BlockSize)
        {
            var size = Math.Min(DropboxContentHasher.BlockSize, length - offset);
            blockHashes.AddRange(SHA256.HashData(data.AsSpan(offset, size)));
        }

        var expected = Convert.ToHexStringLower(SHA256.HashData(blockHashes.ToArray()));
        Assert.Equal(expected, DropboxContentHasher.Compute(data));

        // Same answer however the stream happens to be chunked.
        using var odd = new DropboxContentHasher();
        for (var offset = 0; offset < length; offset += 777_777)
        {
            odd.Write(data.AsSpan(offset, Math.Min(777_777, length - offset)));
        }

        Assert.Equal(expected, odd.GetHashString());
    }

    [Theory]
    [InlineData("IMG_1234.HEIC", "img_1234", ".heic")]
    [InlineData("IMG_1234(1).heic", "img_1234", ".heic")]
    [InlineData("IMG_1234 (2).JPEG", "img_1234", ".jpg")]
    [InlineData("IMG_1234_HEVC.MOV", "img_1234", ".mov")]
    public void Names_normalize_for_matching(string name, string stem, string ext)
    {
        Assert.Equal(stem, MediaNames.NormalizeStem(name));
        Assert.Equal(ext, MediaNames.NormalizeExtension(name));
    }

    [Theory]
    [InlineData("2024-05-01 12.34.56.jpg", "2024-05-01T12:34:56")]
    [InlineData("2024-05-01 12.34.56-1.heic", "2024-05-01T12:34:56")]
    [InlineData("PXL_20240501_123456789.jpg", "2024-05-01T12:34:56")]
    [InlineData("IMG_20240501_123456.jpg", "2024-05-01T12:34:56")]
    [InlineData("20240501_123456.mp4", "2024-05-01T12:34:56")]
    [InlineData("Screenshot_20240501-123456.png", "2024-05-01T12:34:56")]
    public void Dates_are_read_from_generated_names(string name, string expected)
    {
        Assert.True(MediaNames.TryParseDateFromName(name, out var local));
        Assert.Equal(DateTime.Parse(expected), local);
        Assert.False(new MediaItem { SourceIndex = 0, Location = name, FileName = name, Kind = MediaKind.Photo }.HasOriginalName);
    }

    [Theory]
    [InlineData("IMG_1234.HEIC")]
    [InlineData("holiday.jpg")]
    [InlineData("IMG-20240501-WA0001.jpg")]
    public void Camera_names_are_not_date_names(string name)
    {
        Assert.False(MediaNames.TryParseDateFromName(name, out _));
    }

    [Fact]
    public void Exif_capture_time_and_offset_are_read()
    {
        var jpeg = TestData.JpegWithExif(new DateTime(2024, 5, 1, 12, 34, 56), "-07:00");
        var time = CaptureTimeReader.Read(new MemoryStream(jpeg));

        Assert.Equal(new DateTime(2024, 5, 1, 12, 34, 56), time.Local);
        Assert.Equal(new DateTime(2024, 5, 1, 19, 34, 56), time.Utc);
    }

    [Fact]
    public void Exif_without_offset_gives_local_time_only()
    {
        var jpeg = TestData.JpegWithExif(new DateTime(2024, 5, 1, 12, 34, 56), offset: null);
        var time = CaptureTimeReader.Read(new MemoryStream(jpeg));

        Assert.Equal(new DateTime(2024, 5, 1, 12, 34, 56), time.Local);
        Assert.Null(time.Utc);
    }

    [Theory]
    [InlineData("2024-05-01T12:34:56-0700", "2024-05-01T12:34:56", "2024-05-01T19:34:56")]
    [InlineData("2024-05-01T12:34:56+05:30", "2024-05-01T12:34:56", "2024-05-01T07:04:56")]
    [InlineData("2024-05-01T12:34:56.123+0000", "2024-05-01T12:34:56.123", "2024-05-01T12:34:56.123")]
    public void Apple_video_creation_dates_give_local_and_utc(string text, string local, string utc)
    {
        Assert.True(CaptureTimeReader.TryParseQuickTimeCreationDate(text, out var value));
        Assert.Equal(DateTime.Parse(local), value.DateTime);
        Assert.Equal(DateTime.Parse(utc), value.UtcDateTime);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Iphone_video_keeps_its_local_capture_time(bool isoMeta, bool inUdta)
    {
        var mov = TestData.AppleMov("2024-05-01T12:34:56-0700", isoMeta, inUdta);
        var time = CaptureTimeReader.Read(new MemoryStream(mov));

        Assert.Equal(new DateTime(2024, 5, 1, 12, 34, 56), time.Local);
        Assert.Equal(new DateTime(2024, 5, 1, 19, 34, 56), time.Utc);
    }

    [Fact]
    public void Unreadable_files_have_no_capture_time()
    {
        Assert.Equal(default, CaptureTimeReader.Read(new MemoryStream([1, 2, 3, 4])));
    }
}
