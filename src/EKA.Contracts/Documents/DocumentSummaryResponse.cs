namespace EKA.Contracts.Documents;

/// <summary>
/// One row in a document list.
/// </summary>
/// <param name="Id">Server-assigned identifier.</param>
/// <param name="Title">Display name.</param>
/// <param name="FileName">Original file name.</param>
/// <param name="ContentType">MIME type, lowercased.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="Status">Ingestion status name.</param>
/// <param name="ChunkCount">Chunks produced so far.</param>
/// <param name="CreatedAt">When the document was registered.</param>
/// <remarks>
/// Narrower than <see cref="DocumentResponse"/> on purpose. A list of 50 documents has no
/// use for 50 failure reasons, and a list response that happens to contain every field is
/// how a list endpoint quietly becomes the slowest thing in the application.
/// </remarks>
public sealed record DocumentSummaryResponse(
    Guid Id,
    string Title,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Status,
    int ChunkCount,
    DateTimeOffset CreatedAt);
