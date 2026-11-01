using System.Security.Cryptography;

namespace MediaReconciler.Hashing;

/// <summary>
/// Dropbox's content hash: split the file into 4 MiB blocks, SHA-256 each block, then SHA-256
/// the concatenated block hashes. Dropbox returns this for every file in its API, so computing
/// it locally lets local folders, Takeout exports and Dropbox be compared byte-for-byte
/// without downloading anything from Dropbox.
/// https://www.dropbox.com/developers/reference/content-hash
/// </summary>
/// <remarks>Write-only stream so anything can be hashed with <c>source.CopyTo(hasher)</c>.</remarks>
public sealed class DropboxContentHasher : Stream
{
    public const int BlockSize = 4 * 1024 * 1024;

    private readonly IncrementalHash _overall = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly IncrementalHash _block = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _blockFill;
    private long _length;

    public static string Compute(Stream content)
    {
        using var hasher = new DropboxContentHasher();
        content.CopyTo(hasher, 1024 * 1024);
        return hasher.GetHashString();
    }

    public static async Task<string> ComputeAsync(Stream content, CancellationToken ct)
    {
        using var hasher = new DropboxContentHasher();
        await content.CopyToAsync(hasher, 1024 * 1024, ct);
        return hasher.GetHashString();
    }

    public static string Compute(ReadOnlySpan<byte> content)
    {
        using var hasher = new DropboxContentHasher();
        hasher.Write(content);
        return hasher.GetHashString();
    }

    /// <summary>Finishes the hash. Call once, after all content has been written.</summary>
    public string GetHashString()
    {
        if (_blockFill > 0)
        {
            _overall.AppendData(_block.GetHashAndReset());
            _blockFill = 0;
        }

        return Convert.ToHexStringLower(_overall.GetHashAndReset());
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var take = Math.Min(BlockSize - _blockFill, buffer.Length);
            _block.AppendData(buffer[..take]);
            _blockFill += take;
            _length += take;
            buffer = buffer[take..];

            if (_blockFill == BlockSize)
            {
                _overall.AppendData(_block.GetHashAndReset());
                _blockFill = 0;
            }
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _overall.Dispose();
            _block.Dispose();
        }

        base.Dispose(disposing);
    }
}
