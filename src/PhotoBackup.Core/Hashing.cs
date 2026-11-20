using System.Security.Cryptography;

namespace PhotoBackup.Core;

public static class Hashing
{
    public static string HashFile(string path, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, n);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

/// <summary>Write-only stream that hashes and counts everything passed through to the inner stream.</summary>
public sealed class HashingWriteStream(Stream inner, CancellationToken ct, Action<long>? onBytes = null) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _hex;

    public long BytesWritten { get; private set; }
    public string HashHex => _hex ??= Convert.ToHexString(_hash.GetHashAndReset());

    public override void Write(byte[] buffer, int offset, int count)
    {
        ct.ThrowIfCancellationRequested();
        _hash.AppendData(buffer, offset, count);
        inner.Write(buffer, offset, count);
        BytesWritten += count;
        onBytes?.Invoke(BytesWritten);
    }

    public override void Flush() => inner.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => BytesWritten;
    public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}
