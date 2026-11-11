using EKA.Domain.Common;

namespace EKA.Domain.Ingestion;

/// <summary>
/// Facts about the uploaded file itself, as distinct from the document it becomes.
/// </summary>
/// <remarks>
/// <para>
/// These four values always travel together and are meaningless apart — a content type
/// without a filename describes nothing. Grouping them into one value object keeps
/// <see cref="Document"/>'s constructor honest instead of taking four loose parameters
/// where two are strings that can be swapped silently.
/// </para>
/// <para>
/// <b>What is deliberately absent: a maximum size.</b> "Uploads may not exceed 50 MB" is
/// configuration policy that changes per environment and per customer tier, not a truth
/// about files. Baking it in here would mean a domain test fails when Ops raises the limit.
/// The domain requires only that a file has <i>some</i> content; the ceiling is enforced
/// at the upload boundary in Step 7 via <c>ValidateOnStart</c>-checked options.
/// </para>
/// </remarks>
public sealed record FileMetadata
{
    /// <summary>Length of a lowercase hex SHA-256 digest.</summary>
    private const int Sha256HexLength = 64;

    public FileMetadata(string fileName, string contentType, long sizeBytes, string contentHash)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new DomainException("File name cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new DomainException("Content type cannot be empty.");
        }

        if (sizeBytes <= 0)
        {
            throw new DomainException($"File size must be positive; got {sizeBytes} bytes.");
        }

        if (string.IsNullOrWhiteSpace(contentHash))
        {
            throw new DomainException("Content hash cannot be empty.");
        }

        var normalizedHash = contentHash.Trim().ToLowerInvariant();

        if (normalizedHash.Length != Sha256HexLength
            || !normalizedHash.All(char.IsAsciiHexDigitLower))
        {
            throw new DomainException(
                "Content hash must be a 64-character lowercase hex SHA-256 digest.");
        }

        FileName = fileName.Trim();
        ContentType = contentType.Trim().ToLowerInvariant();
        SizeBytes = sizeBytes;
        ContentHash = normalizedHash;
    }

    public string FileName { get; }

    /// <summary>MIME type, lowercased — <c>application/pdf</c>, <c>text/markdown</c>.</summary>
    public string ContentType { get; }

    public long SizeBytes { get; }

    /// <summary>
    /// SHA-256 of the file's bytes. This is what makes ingestion idempotent: re-uploading
    /// the same bytes is recognisable without re-reading the file, so a retried job cannot
    /// double a document's chunks.
    /// </summary>
    public string ContentHash { get; }

    public override string ToString() => $"{FileName} ({ContentType}, {SizeBytes} bytes)";
}
