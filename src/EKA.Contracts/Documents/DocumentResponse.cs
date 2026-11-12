namespace EKA.Contracts.Documents;

/// <summary>
/// Everything a client may know about one document.
/// </summary>
/// <param name="Id">Server-assigned identifier.</param>
/// <param name="Title">Display name.</param>
/// <param name="FileName">Original file name.</param>
/// <param name="ContentType">MIME type, lowercased.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="ContentHash">Lowercase hex SHA-256 of the file's bytes.</param>
/// <param name="Status">Ingestion status name — <c>Pending</c>, <c>Completed</c>, and so on.</param>
/// <param name="Authority">Trust score from 0 to 100.</param>
/// <param name="ChunkCount">Chunks produced so far; 0 until chunking completes.</param>
/// <param name="IsSearchable">True only once ingestion has completed.</param>
/// <param name="FailureReason">Why the last attempt failed, or null.</param>
/// <param name="FailureCount">How many times ingestion has failed. Never reset.</param>
/// <param name="CreatedAt">When the document was registered.</param>
/// <param name="ModifiedAt">When it last changed, or null if never.</param>
/// <param name="ExtractedAt">When text extraction completed, or null.</param>
/// <param name="IngestedAt">When ingestion completed, or null.</param>
/// <remarks>
/// <para>
/// <b>Why this exists rather than returning <c>Document</c>.</b> Three reasons, in order of
/// how much they bite.
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>Leakage.</b> <c>Document</c> carries <c>TenantId</c>, <c>StorageKey</c>,
/// <c>CreatedBy</c> and a chunk collection. Serialising it hands a client the internal
/// storage path of every file and the subject id of whoever uploaded it. Neither is
/// anyone's business, and neither failure is visible in a code review of the endpoint.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Coupling.</b> With the entity on the wire, renaming a private field breaks the
/// Angular client. The contract and the model would be forced to change together forever.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Serialisation.</b> <c>Document</c> exposes <c>DomainEvents</c> and a lazy chunk
/// collection; serialising it either emits noise or triggers a query nobody asked for.
/// </description>
/// </item>
/// </list>
/// <para>
/// <c>Status</c> is a string, not the enum's number. <c>"Completed"</c> survives inserting a
/// new pipeline stage; <c>50</c> does not survive anything.
/// </para>
/// </remarks>
public sealed record DocumentResponse(
    Guid Id,
    string Title,
    string FileName,
    string ContentType,
    long SizeBytes,
    string ContentHash,
    string Status,
    int Authority,
    int ChunkCount,
    bool IsSearchable,
    string? FailureReason,
    int FailureCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ModifiedAt,
    DateTimeOffset? ExtractedAt,
    DateTimeOffset? IngestedAt);
