using EKA.Domain.Common;

namespace EKA.Domain.Ingestion;

/// <summary>
/// An uploaded source file tracked through the ingestion pipeline. Aggregate root for its
/// chunks.
/// </summary>
/// <remarks>
/// <para>
/// <b>No public setters.</b> Every state change goes through a method named for the
/// business event that caused it — <see cref="BeginExtraction"/>, <see cref="Fail"/>,
/// <see cref="CompleteIngestion"/>. That is where the invariant lives. With a public
/// <c>Status</c> setter, "a completed document always has chunks" is a rule you hope every
/// code path remembers; here it is a rule that cannot be broken.
/// </para>
/// <para>
/// <b>The lifecycle:</b>
/// </para>
/// <code>
///   Pending ──► Extracting ──► Chunking ──► Embedding ──► Completed
///      ▲             │             │            │
///      │             └─────────────┴────────────┴──► Failed
///      └──────────────────── Reset ◄───────────────────┘
/// </code>
/// <para>
/// Every edge is explicit and every other move throws
/// <see cref="InvalidStateTransitionException"/>. A state machine written as a diagram in
/// a wiki drifts from the code; written as methods, it is the code.
/// </para>
/// </remarks>
public sealed class Document : EntityAudit<Guid>
{
    private readonly List<DocumentChunk> _chunks = [];

    /// <summary>EF Core materialization only.</summary>
    private Document()
    {
    }

    public Document(
        Guid id,
        TenantId tenantId,
        DocumentTitle title,
        FileMetadata file,
        string storageKey,
        DateTimeOffset createdAt,
        AuthorityScore? authority = null)
        : base(id, createdAt)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(file);

        // TenantId is a struct, so `default(TenantId)` slips past its own constructor
        // validation (see TenantId's remarks). The aggregate re-checks rather than trust
        // a guarantee the language cannot make.
        if (tenantId.IsEmpty)
        {
            throw new DomainException("A document must belong to a tenant.");
        }

        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new DomainException("A document must record where its file is stored.");
        }

        TenantId = tenantId;
        Title = title;
        File = file;
        StorageKey = storageKey.Trim();
        Authority = authority ?? AuthorityScore.Standard;
        Status = IngestionStatus.Pending;
    }

    public TenantId TenantId { get; private set; }

    public DocumentTitle Title { get; private set; } = null!;

    public FileMetadata File { get; private set; } = null!;

    /// <summary>Where the original bytes live — a blob path or object key, not a URL.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    public IngestionStatus Status { get; private set; }

    public AuthorityScore Authority { get; private set; }

    public DateTimeOffset? ExtractedAt { get; private set; }

    public DateTimeOffset? IngestedAt { get; private set; }

    /// <summary>
    /// Readable without loading the chunk collection — which is the common case, since a
    /// 300-page PDF yields thousands of chunks that a document list has no use for.
    /// </summary>
    public int ChunkCount { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>How many times ingestion has failed. Never reset, so a document that
    /// fails repeatedly can be escalated rather than retried forever.</summary>
    public int FailureCount { get; private set; }

    public IReadOnlyCollection<DocumentChunk> Chunks => _chunks.AsReadOnly();

    /// <summary>Only completed documents may be returned by retrieval.</summary>
    public bool IsSearchable => Status is IngestionStatus.Completed;

    // ── Lifecycle ───────────────────────────────────────────────────────────────────

    public void BeginExtraction(DateTimeOffset at)
    {
        EnsureStatusIs(IngestionStatus.Pending, IngestionStatus.Extracting);

        Status = IngestionStatus.Extracting;
        MarkModified(at);
    }

    public void CompleteExtraction(DateTimeOffset at)
    {
        EnsureStatusIs(IngestionStatus.Extracting, IngestionStatus.Chunking);

        Status = IngestionStatus.Chunking;
        ExtractedAt = at;
        MarkModified(at);
    }

    /// <summary>
    /// Attaches the chunks produced from the extracted text and moves to
    /// <see cref="IngestionStatus.Embedding"/>.
    /// </summary>
    /// <remarks>
    /// Enforces that indexes are complete and contiguous from zero. A gap means the
    /// chunker dropped a slice of the document, and the symptom would otherwise appear
    /// much later as an answer that is confidently missing a paragraph — the kind of bug
    /// that is nearly impossible to trace backwards from the output.
    /// <para>
    /// Step 8 writes chunks in bulk for large documents rather than through this method,
    /// for performance. The ordering invariant is therefore ALSO enforced by a unique
    /// database index on <c>(DocumentId, ChunkIndex)</c> added in Step 3 — when an
    /// invariant cannot live in the aggregate, it moves to a constraint that cannot be
    /// bypassed.
    /// </para>
    /// </remarks>
    public void AddChunks(IEnumerable<DocumentChunk> chunks, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        EnsureStatusIs(IngestionStatus.Chunking, IngestionStatus.Embedding);

        var ordered = chunks.OrderBy(c => c.Index).ToList();

        if (ordered.Count == 0)
        {
            throw new DomainException(
                $"Document {Id} produced no chunks; there is nothing to embed.");
        }

        var foreign = ordered.Find(c => c.DocumentId != Id);
        if (foreign is not null)
        {
            throw new DomainException(
                $"Chunk {foreign.Index} belongs to document {foreign.DocumentId}, not {Id}.");
        }

        for (var expected = 0; expected < ordered.Count; expected++)
        {
            if (ordered[expected].Index.Value != expected)
            {
                throw new DomainException(
                    $"Chunk indexes must be contiguous from 0; expected {expected} but found "
                    + $"{ordered[expected].Index.Value}.");
            }
        }

        _chunks.Clear();
        _chunks.AddRange(ordered);
        ChunkCount = ordered.Count;
        Status = IngestionStatus.Embedding;
        MarkModified(at);
    }

    public void CompleteIngestion(DateTimeOffset at)
    {
        EnsureStatusIs(IngestionStatus.Embedding, IngestionStatus.Completed);

        if (ChunkCount <= 0)
        {
            throw new DomainException(
                $"Document {Id} cannot complete ingestion with no chunks.");
        }

        Status = IngestionStatus.Completed;
        IngestedAt = at;
        FailureReason = null;
        MarkModified(at);

        Raise(new DocumentIngestedEvent(Id, TenantId, ChunkCount, at));
    }

    /// <summary>Records a failure at whatever stage the document currently occupies.</summary>
    public void Fail(string reason, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("A failure must record why.");
        }

        if (Status is IngestionStatus.Completed)
        {
            throw new InvalidStateTransitionException(
                nameof(Document), Status, IngestionStatus.Failed);
        }

        var failedAt = Status;

        Status = IngestionStatus.Failed;
        FailureReason = reason.Trim();
        FailureCount++;
        MarkModified(at);

        Raise(new DocumentIngestionFailedEvent(
            Id, TenantId, failedAt, FailureReason, FailureCount, at));
    }

    /// <summary>
    /// Returns a failed document to <see cref="IngestionStatus.Pending"/> so it can be
    /// ingested again.
    /// </summary>
    /// <remarks>
    /// Discards existing chunks, which is what makes re-ingestion <b>idempotent</b>:
    /// without this, a retry appends a second full set of chunks and the document is
    /// silently indexed twice, returning duplicate passages for every query.
    /// <see cref="FailureCount"/> deliberately survives the reset.
    /// </remarks>
    public void Reset(DateTimeOffset at)
    {
        EnsureStatusIs(IngestionStatus.Failed, IngestionStatus.Pending);

        _chunks.Clear();
        ChunkCount = 0;
        ExtractedAt = null;
        IngestedAt = null;
        Status = IngestionStatus.Pending;
        MarkModified(at);
    }

    // ── Metadata changes, legal in any state ────────────────────────────────────────

    public void Rename(DocumentTitle title, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(title);

        Title = title;
        MarkModified(at);
    }

    public void SetAuthority(AuthorityScore authority, DateTimeOffset at)
    {
        Authority = authority;
        MarkModified(at);
    }

    private void EnsureStatusIs(IngestionStatus required, IngestionStatus target)
    {
        if (Status != required)
        {
            throw new InvalidStateTransitionException(nameof(Document), Status, target);
        }
    }

    public override string ToString() => $"{Title} [{Status}]";
}
