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

    /// <summary>
    /// True while text extraction is still owed: the document is waiting for it, or an
    /// earlier attempt began it and never finished.
    /// </summary>
    /// <remarks>
    /// Background jobs are delivered <i>at least</i> once. A job can run twice, or run again
    /// after a worker crashed mid-extraction. The aggregate answers "is there work here?" so
    /// the job does not have to read <see cref="Status"/> and re-derive the state machine
    /// (ADR-0011).
    /// </remarks>
    public bool AwaitsExtraction => Status is IngestionStatus.Pending or IngestionStatus.Extracting;

    /// <summary>
    /// <see cref="BeginExtraction"/>, or a no-op if extraction already began. That covers a
    /// crashed attempt, which left the status at Extracting with no one working on it.
    /// </summary>
    /// <remarks>
    /// <see cref="BeginExtraction"/> stays strict, because "begin twice" is a bug in an
    /// interactive caller. Only the redelivered job needs re-entry, and it gets its own name
    /// so the leniency is explicit at the call site.
    /// </remarks>
    public void StartOrResumeExtraction(DateTimeOffset at)
    {
        if (Status is IngestionStatus.Extracting)
        {
            return;
        }

        BeginExtraction(at);
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
    /// The ordering invariant is ALSO enforced by a unique database index on
    /// <c>(DocumentId, ChunkIndex)</c> added in Step 3, so a future bulk writer that
    /// bypasses this method for very large documents still cannot break it. Step 8 goes
    /// through this method: EF Core batches the inserts, which is fast enough for the
    /// document sizes the upload limit allows.
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

    /// <summary>Text is extracted and waiting to be cut into chunks.</summary>
    public bool AwaitsChunking => Status is IngestionStatus.Chunking;

    /// <summary>Chunks exist, and at least one still needs its vector.</summary>
    /// <remarks>
    /// Like <see cref="AwaitsExtraction"/>, it lets a redelivered job ask the aggregate
    /// which work is owed instead of re-deriving the state machine from <see cref="Status"/>.
    /// </remarks>
    public bool AwaitsEmbedding => Status is IngestionStatus.Embedding;

    /// <summary>
    /// The loaded chunks that have no vector yet, in index order. Empty when the chunk
    /// collection was not loaded, so read it only after loading the chunks.
    /// </summary>
    public IReadOnlyList<DocumentChunk> ChunksAwaitingEmbedding =>
        _chunks.Where(chunk => !chunk.IsEmbedded).OrderBy(chunk => chunk.Index).ToList();

    /// <summary>
    /// Records that the chunks at <paramref name="indexes"/> now have vectors from
    /// <paramref name="embeddingModel"/>, durably stored in the vector index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once per completed batch rather than once at the end, so a failure halfway
    /// through a 3,000-chunk document keeps the first half: the retry embeds only what is
    /// still missing. That is the "one failure must not lose the batch's progress" rule.
    /// </para>
    /// <para>
    /// Through the root rather than <see cref="DocumentChunk.MarkEmbedded"/> directly, so the
    /// status rule holds: vectors are recorded only while the document is embedding.
    /// </para>
    /// </remarks>
    public void RecordEmbeddings(IEnumerable<ChunkIndex> indexes, string embeddingModel, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        EnsureStatusIs(IngestionStatus.Embedding, IngestionStatus.Embedding);

        foreach (var index in indexes)
        {
            var chunk = _chunks.Find(c => c.Index == index)
                ?? throw new DomainException(
                    $"Document {Id} has no loaded chunk {index}; load the chunks before recording embeddings.");

            chunk.MarkEmbedded(embeddingModel, at);
        }

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

        // Checked against the loaded collection, which is why it must be loaded: "completed"
        // is the promise that every chunk is searchable. A document completed with a chunk
        // missing its vector answers questions with a silent hole in it (Step 8).
        if (_chunks.Count != ChunkCount)
        {
            throw new DomainException(
                $"Document {Id} must be loaded with its {ChunkCount} chunks to complete ingestion.");
        }

        var missing = _chunks.Find(chunk => !chunk.IsEmbedded);
        if (missing is not null)
        {
            throw new DomainException(
                $"Document {Id} cannot complete ingestion: chunk {missing.Index} has no vector.");
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
