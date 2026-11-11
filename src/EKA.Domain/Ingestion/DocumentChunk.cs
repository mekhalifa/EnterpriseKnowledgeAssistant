using EKA.Domain.Common;

namespace EKA.Domain.Ingestion;

/// <summary>
/// A contiguous slice of one document's extracted text, small enough to embed usefully and
/// to fit several into a prompt.
/// </summary>
/// <remarks>
/// <para>
/// Why chunks exist at all: embedding a whole document produces one vector averaging
/// everything it says, which points nowhere in particular and matches nothing well.
/// Chunking trades that for many vectors that each mean something specific.
/// </para>
/// <para>
/// Part of the <see cref="Document"/> aggregate — chunks are added through the root so
/// ordering can be enforced, never constructed and saved independently.
/// </para>
/// </remarks>
public sealed class DocumentChunk : EntityAudit<Guid>
{
    /// <summary>EF Core materialization only.</summary>
    private DocumentChunk()
    {
    }

    public DocumentChunk(
        Guid id,
        Guid documentId,
        ChunkIndex index,
        string content,
        TokenCount tokenCount,
        DateTimeOffset createdAt,
        PageRange? pageRange = null,
        string? sectionPath = null)
        : base(id, createdAt)
    {
        // ── Validate BEFORE assigning. ──────────────────────────────────────────────
        // This ordering is the whole point of the original codebase's P0 bug: validation
        // referenced Content before Content had been set, so the guard tested a null that
        // the caller never passed and every chunk failed construction. Guards first,
        // assignment last, with nothing in between.
        if (documentId == Guid.Empty)
        {
            throw new DomainException("A chunk must belong to a document.");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new DomainException(
                $"Chunk {index} of document {documentId} has no content.");
        }

        if (sectionPath is not null && string.IsNullOrWhiteSpace(sectionPath))
        {
            throw new DomainException(
                "Section path must be null or meaningful, not blank whitespace.");
        }

        DocumentId = documentId;
        Index = index;
        Content = content;
        TokenCount = tokenCount;
        PageRange = pageRange;
        SectionPath = sectionPath?.Trim();
    }

    public Guid DocumentId { get; private set; }

    public ChunkIndex Index { get; private set; }

    /// <summary>
    /// The chunk text. Not trimmed: leading and trailing whitespace can carry meaning for
    /// overlap reconstruction, and normalization is the extraction stage's job, done once,
    /// rather than something every chunk re-decides.
    /// </summary>
    public string Content { get; private set; } = string.Empty;

    public TokenCount TokenCount { get; private set; }

    /// <summary>Where this text sat in the source. Null for formats without pages.</summary>
    public PageRange? PageRange { get; private set; }

    /// <summary>Heading trail, e.g. <c>"Benefits / Leave / Parental"</c>. Null if unknown.</summary>
    public string? SectionPath { get; private set; }

    /// <summary>
    /// Which model produced this chunk's vector. Recorded per chunk, not per document,
    /// because it determines whether a stored vector is still comparable to a query
    /// vector: changing the embedding model invalidates every chunk embedded with the old
    /// one, and without this field there is no way to tell which those are.
    /// </summary>
    public string? EmbeddingModel { get; private set; }

    public DateTimeOffset? EmbeddedAt { get; private set; }

    public bool IsEmbedded => EmbeddingModel is not null;

    public void MarkEmbedded(string embeddingModel, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(embeddingModel))
        {
            throw new DomainException("Embedding model name is required.");
        }

        EmbeddingModel = embeddingModel.Trim();
        EmbeddedAt = at;
        MarkModified(at);
    }

    public override string ToString() =>
        $"Chunk {Index} of {DocumentId} ({TokenCount} tokens)";
}
