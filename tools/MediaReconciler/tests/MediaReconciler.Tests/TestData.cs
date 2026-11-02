using System.Buffers.Binary;
using System.Text;
using MediaReconciler.Model;

namespace MediaReconciler.Tests;

internal static class TestData
{
    public static MediaItem Item(
        int source,
        string name,
        long size = 1000,
        string? hash = null,
        DateTime? local = null,
        DateTime? utc = null,
        bool utcIsFileTime = false) => new()
    {
        SourceIndex = source,
        Location = $"s{source}/{name}",
        FileName = name,
        Kind = MediaNames.GetKind(name) ?? MediaKind.Photo,
        Size = size,
        ContentHash = hash,
        TakenLocal = local,
        TakenUtc = utc,
        UtcIsFileTime = utcIsFileTime,
    };

    /// <summary>
    /// A tiny but valid JPEG whose EXIF says it was taken at <paramref name="taken"/>
    /// (camera wall-clock) with the given UTC offset, like an iPhone photo.
    /// </summary>
    public static byte[] JpegWithExif(DateTime taken, string? offset, int padding = 0)
    {
        var date = Encoding.ASCII.GetBytes(taken.ToString("yyyy:MM:dd HH:mm:ss") + "\0"); // 20 bytes
        var zone = offset is null ? null : Encoding.ASCII.GetBytes(offset + "\0");         // 7 bytes

        // TIFF, little-endian. IFD0 holds only the pointer to the Exif sub-IFD.
        var tiff = new MemoryStream();
        var w = new BinaryWriter(tiff);
        w.Write("II"u8);
        w.Write((ushort)42);
        w.Write(8u);                       // IFD0 at offset 8

        w.Write((ushort)1);                // IFD0: 1 entry
        WriteEntry(w, 0x8769, 4, 1, 26);   // ExifIFDPointer -> offset 26
        w.Write(0u);                       // no next IFD

        var exifEntries = zone is null ? 1 : 2;
        var dataStart = 26 + 2 + exifEntries * 12 + 4;
        w.Write((ushort)exifEntries);
        WriteEntry(w, 0x9003, 2, (uint)date.Length, (uint)dataStart);                 // DateTimeOriginal
        if (zone is not null) WriteEntry(w, 0x9011, 2, (uint)zone.Length, (uint)(dataStart + date.Length)); // OffsetTimeOriginal
        w.Write(0u);
        w.Write(date);
        if (zone is not null) w.Write(zone);
        w.Flush();

        var exif = new MemoryStream();
        exif.Write("Exif\0\0"u8);
        exif.Write(tiff.ToArray());
        var app1 = exif.ToArray();

        var jpeg = new MemoryStream();
        jpeg.Write([0xFF, 0xD8]);                       // SOI
        jpeg.Write([0xFF, 0xE1]);                       // APP1
        var len = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)(app1.Length + 2));
        jpeg.Write(len);
        jpeg.Write(app1);
        if (padding > 0)
        {
            // A COM segment so test files can differ in size/content without changing EXIF.
            jpeg.Write([0xFF, 0xFE]);
            BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)(padding + 2));
            jpeg.Write(len);
            jpeg.Write(new byte[padding]);
        }

        jpeg.Write([0xFF, 0xD9]);                       // EOI
        return jpeg.ToArray();

        static void WriteEntry(BinaryWriter w, ushort tag, ushort type, uint count, uint value)
        {
            w.Write(tag);
            w.Write(type);
            w.Write(count);
            w.Write(value);
        }
    }

    /// <summary>
    /// A minimal MOV holding Apple's "com.apple.quicktime.creationdate" the way an iPhone writes
    /// it: moov → meta (hdlr 'mdta', keys, ilst). <paramref name="isoMeta"/> adds the 4-byte
    /// version/flags some writers put on meta; <paramref name="inUdta"/> nests it in moov/udta.
    /// </summary>
    public static byte[] AppleMov(string creationDate, bool isoMeta = false, bool inUdta = false)
    {
        const string key = "com.apple.quicktime.creationdate";
        var hdlr = Atom("hdlr", U32(0), U32(0), Encoding.ASCII.GetBytes("mdta"), new byte[12], [0]);
        var keys = Atom("keys", U32(0), U32(1), U32((uint)(8 + key.Length)), Encoding.ASCII.GetBytes("mdta"), Encoding.ASCII.GetBytes(key));
        var data = Atom("data", U32(1), U32(0), Encoding.UTF8.GetBytes(creationDate));
        var item = U32((uint)(8 + data.Length)).Concat(U32(1)).Concat(data).ToArray(); // type = key index 1
        var ilst = Atom("ilst", item);
        var meta = isoMeta ? Atom("meta", U32(0), hdlr, keys, ilst) : Atom("meta", hdlr, keys, ilst);
        var moov = inUdta ? Atom("moov", Atom("udta", meta)) : Atom("moov", meta);
        return Atom("ftyp", Encoding.ASCII.GetBytes("qt  "), U32(0), Encoding.ASCII.GetBytes("qt  ")).Concat(moov).ToArray();

        static byte[] Atom(string type, params byte[][] parts)
        {
            var body = parts.SelectMany(p => p).ToArray();
            return U32((uint)(8 + body.Length)).Concat(Encoding.ASCII.GetBytes(type)).Concat(body).ToArray();
        }

        static byte[] U32(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            return bytes;
        }
    }

    private static readonly List<string> Created = [];

    static TestData()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var dir in Created)
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        };
    }

    public static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mediareconciler-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        lock (Created) Created.Add(dir);
        return dir;
    }
}
