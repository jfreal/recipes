using System.Buffers.Binary;
using System.Text;

namespace MediaReconciler.Metadata;

/// <summary>
/// Reads "com.apple.quicktime.creationdate" straight from a MOV/MP4's moov → meta atoms.
/// iPhones store it with the recording's UTC offset ("2024-05-01T12:34:56-0700"), which is the
/// only source of a video's camera wall-clock time. MetadataExtractor converts it to UTC and
/// drops the offset, so it can't be used for this.
/// </summary>
internal static class AppleVideoDate
{
    private const string Key = "com.apple.quicktime.creationdate";
    private const int MaxValueBytes = 256;
    private const int MaxKeys = 4096;

    private readonly record struct Atom(string Type, long BodyStart, long End);

    public static bool TryRead(Stream stream, out DateTimeOffset value)
    {
        value = default;
        if (!stream.CanSeek) return false;

        try
        {
            if (Find(stream, 0, stream.Length, "moov") is not { } moov) return false;

            // Usually moov/meta; some writers put it under moov/udta/meta.
            var meta = Find(stream, moov.BodyStart, moov.End, "meta");
            if (meta is null && Find(stream, moov.BodyStart, moov.End, "udta") is { } udta)
            {
                meta = Find(stream, udta.BodyStart, udta.End, "meta");
            }

            if (meta is not { } m) return false;

            // QuickTime's meta is a plain container; the ISO variant adds 4 bytes of version/flags.
            var childStart = ReadType(stream, m.BodyStart + 4) == "hdlr" ? m.BodyStart : m.BodyStart + 4;

            if (Find(stream, childStart, m.End, "keys") is not { } keys ||
                Find(stream, childStart, m.End, "ilst") is not { } ilst ||
                KeyIndex(stream, keys) is not { } index ||
                ItemValue(stream, ilst, index) is not { } text)
            {
                return false;
            }

            return CaptureTimeReader.TryParseQuickTimeCreationDate(text, out value);
        }
        catch (Exception e) when (e is IOException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static int? KeyIndex(Stream stream, Atom keys)
    {
        // version/flags (4), entry count (4), then entries: size (4), namespace (4), name.
        var header = ReadBytes(stream, keys.BodyStart, 8);
        var count = Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)), MaxKeys);
        var pos = keys.BodyStart + 8;

        for (var i = 1; i <= count && pos + 8 <= keys.End; i++)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(stream, pos, 4));
            if (size < 8 || pos + size > keys.End) return null;

            if (size - 8 == Key.Length &&
                Encoding.ASCII.GetString(ReadBytes(stream, pos + 8, (int)size - 8)) == Key)
            {
                return i;
            }

            pos += size;
        }

        return null;
    }

    private static string? ItemValue(Stream stream, Atom ilst, int index)
    {
        // Each ilst child's type field is the 1-based key index; it holds a 'data' atom with
        // type (4) and locale (4) before the UTF-8 value.
        for (var pos = ilst.BodyStart; pos + 8 <= ilst.End;)
        {
            var (size, bodyStart) = ReadSize(stream, pos, ilst.End);
            var itemIndex = BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(stream, pos + 4, 4));
            var end = pos + size;

            if (itemIndex == index && Find(stream, bodyStart, end, "data") is { } data)
            {
                var length = data.End - data.BodyStart - 8;
                if (length <= 0 || length > MaxValueBytes) return null;
                return Encoding.UTF8.GetString(ReadBytes(stream, data.BodyStart + 8, (int)length));
            }

            pos = end;
        }

        return null;
    }

    private static Atom? Find(Stream stream, long start, long end, string type)
    {
        for (var pos = start; pos + 8 <= end;)
        {
            var (size, bodyStart) = ReadSize(stream, pos, end);
            if (ReadType(stream, pos + 4) == type) return new Atom(type, bodyStart, pos + size);
            pos += size;
        }

        return null;
    }

    /// <summary>Atom size (32-bit, 64-bit when 1, "to the end" when 0) and where its body starts.</summary>
    private static (long Size, long BodyStart) ReadSize(Stream stream, long pos, long end)
    {
        long size = BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(stream, pos, 4));
        var bodyStart = pos + 8;
        if (size == 1)
        {
            size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(stream, pos + 8, 8)));
            bodyStart = pos + 16;
        }
        else if (size == 0)
        {
            size = end - pos;
        }

        if (size < bodyStart - pos || pos + size > end) throw new IOException("Malformed QuickTime atom.");
        return (size, bodyStart);
    }

    private static string ReadType(Stream stream, long pos) => Encoding.ASCII.GetString(ReadBytes(stream, pos, 4));

    private static byte[] ReadBytes(Stream stream, long pos, int count)
    {
        var buffer = new byte[count];
        stream.Position = pos;
        stream.ReadExactly(buffer);
        return buffer;
    }
}
