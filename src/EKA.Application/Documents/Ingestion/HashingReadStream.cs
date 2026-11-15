using System.Security.Cryptography;

namespace EKA.Application.Documents.Ingestion;

/// <summary>
/// A read-only pass-through stream that computes SHA-256 over everything read through it,
/// and refuses to read past a byte limit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why hash while storing</b> instead of hashing first and storing second: one pass over
/// the bytes instead of two, and the hash is of exactly the bytes that reached storage,
/// not of a separate read that could in principle differ.
/// </para>
/// <para>
/// <b>Why the limit is enforced here as well</b> as by the request-size limit at the edge:
/// that limit belongs to the HTTP host, and this stream belongs to the use case. A future
/// caller (an import job, an email ingester) gets the same protection without having to
/// remember it.
/// </para>
/// </remarks>
internal sealed class HashingReadStream(Stream inner, long maxBytes) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _digest;

    public long BytesRead { get; private set; }

    /// <summary>Lowercase hex SHA-256 of every byte read. Available once the stream is drained.</summary>
    public string Digest => _digest ??= Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Account(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Account(ReadOnlySpan<byte> chunk)
    {
        if (_digest is not null && !chunk.IsEmpty)
        {
            throw new InvalidOperationException("The digest was read before the stream was drained.");
        }

        BytesRead += chunk.Length;

        if (BytesRead > maxBytes)
        {
            throw new FileTooLargeException(maxBytes);
        }

        _hash.AppendData(chunk);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Thrown mid-copy when an upload exceeds the configured limit.</summary>
internal sealed class FileTooLargeException(long maxBytes)
    : Exception($"The file exceeds the {maxBytes}-byte limit.")
{
    public long MaxBytes { get; } = maxBytes;
}
