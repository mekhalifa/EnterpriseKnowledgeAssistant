# Domain-Driven Design in EKA

DDD has two halves. The **strategic** half is about drawing boundaries around parts of a
system that mean different things. The **tactical** half is a toolbox — entities, value
objects, aggregates. Most teams adopt the toolbox and skip the boundaries, which is the
half that actually prevents large-system decay.

Our honest position: **EKA is "DDD-lite" and that is deliberate.** This system is mostly
a pipeline plus a search, not a domain with intricate contested rules like insurance
underwriting. We use the tactical patterns where they prevent bugs, we use bounded
contexts to organize modules, and we skip the ceremony that would buy nothing.

---

## Strategic DDD — the bounded contexts

A **bounded context** is a boundary within which a word means exactly one thing. The
classic trap: "Document" means different things in different parts of this system.

- To **Ingestion**, a Document is a file with a MIME type, a byte length, extraction
  status, and a retry count.
- To **Retrieval**, a Document is a set of vectors with ACL metadata and an authority
  score.
- To **Conversation**, a Document is a citation target — a title and a page reference.

Forcing one `Document` class to serve all three gives you a 40-property god object where
half the fields are null in any given use case. Keeping them separate, with explicit
translation between them, is the entire value of bounded contexts.

Our four contexts, which map to folders inside `EKA.Domain` and `EKA.Application` (same
deployable — this is a modular monolith, see [ADR-0001](../adr/0001-modular-monolith.md)):

```
┌────────────────────┐   document indexed    ┌────────────────────┐
│    INGESTION       │ ────────────────────► │     RETRIEVAL      │
│ Document, Upload,  │                       │ Chunk, Vector,     │
│ ExtractionJob,     │                       │ SearchQuery,       │
│ ChunkBatch         │                       │ RetrievedContext   │
└────────────────────┘                       └─────────┬──────────┘
                                                        │ grounded context
                                                        ▼
┌────────────────────┐   access decision     ┌────────────────────┐
│  IDENTITY & ACCESS │ ────────────────────► │   CONVERSATION     │
│ User, Role,        │                       │ Conversation,      │
│ DocumentAcl,       │                       │ Message, Citation, │
│ Tenant             │                       │ PromptTemplate     │
└────────────────────┘                       └────────────────────┘
```

**Why this matters more than usual here:** Identity & Access is an *upstream* context that
Retrieval must consult on every query. Modelling it as its own boundary with an explicit
contract (`IAccessPolicyProvider`) makes "did we filter by ACL?" a question with one
answer in one place — instead of a condition scattered across every query. Our worst
possible bug is a cross-user document leak, so this boundary is load-bearing.

### Ubiquitous language

One word per concept, used identically in conversation, code, tests, and the database.
Agreed for this project:

| Term | Means exactly |
|---|---|
| **Document** | A source file that was uploaded and tracked |
| **Chunk** | A contiguous slice of one document's extracted text, with a stable index |
| **Embedding** | The float vector representing a chunk's meaning |
| **Retrieval** | Finding candidate chunks for a question |
| **Grounding** | Restricting an answer to retrieved evidence |
| **Citation** | A pointer from a sentence in an answer back to a chunk |
| **Authority** | A document's trust ranking, used to break ties in retrieval |
| **Ingestion** | The whole pipeline: upload → extract → chunk → embed → index |

If code says `Section` while we say "Chunk" in conversation, fix one of them. Drift here
is how a codebase stops matching the team's mental model.

---

## Tactical DDD — the patterns we will use

### Entity — identity that persists through change

An entity is defined by its **id**, not its values. A `Document` whose title is corrected
is still the same document.

```csharp
public sealed class Document : EntityAudit<Guid>
{
    private readonly List<DocumentChunk> _chunks = [];

    public DocumentTitle Title { get; private set; }
    public FileMetadata File { get; private set; }
    public IngestionStatus Status { get; private set; }
    public IReadOnlyCollection<DocumentChunk> Chunks => _chunks.AsReadOnly();

    private Document() { }   // EF Core only

    public Document(DocumentTitle title, FileMetadata file, Guid tenantId)
    {
        Title    = title;                    // value objects already validated themselves
        File     = file;
        TenantId = tenantId != Guid.Empty ? tenantId : throw new ArgumentException(…);
        Status   = IngestionStatus.Pending;
    }
}
```

Two rules we will hold to:
- **No public setters.** State changes go through intention-named methods
  (`MarkExtracted()`, `Fail(reason)`), which is where the invariant lives.
- **Validate before assigning.** The README's P0 bug is exactly this: validation ran
  before `Content` was set, so every new chunk failed. Guard clauses come first, always.

### Value object — defined by its values, immutable

`ChunkIndex`, `DocumentTitle`, `EmbeddingVector`, `PageRange`. The reason to bother:

```csharp
// ✗ Primitive obsession — nothing stops a caller swapping the arguments
public DocumentChunk(Guid documentId, int chunkIndex, string content)

// ✓ The type system rejects the mistake, and the rule lives in one place
public readonly record struct ChunkIndex
{
    public int Value { get; }
    public ChunkIndex(int value) => Value = value >= 0
        ? value
        : throw new ArgumentOutOfRangeException(nameof(value), "Chunk index cannot be negative.");
}
```

Use `readonly record struct` for small ones — value equality for free, no allocation.
Candidates in this project: `DocumentTitle` (non-empty, max length), `ChunkIndex`
(non-negative), `TokenCount`, `SimilarityScore` (0..1), `FileMetadata` (name + MIME +
size), `TenantId`.

**When not to:** if a string has no rules, it is a string. Do not wrap for ceremony.

### Aggregate and aggregate root — the consistency boundary

An aggregate is a cluster of objects treated as one unit for changes; the **root** is the
only member outsiders may hold a reference to. The real question an aggregate answers is
*"what must be transactionally consistent?"*

- `Document` is a root; `DocumentChunk` lives inside it. You never load a chunk and save
  it independently — chunk ordering is a property of the document.
- `Conversation` is a root; `Message` lives inside it.
- `PromptTemplate` and `AIProvider` are separate roots, referenced **by id** across the
  boundary, never by object reference.

```csharp
// ✓ Chunks are added through the root, which can enforce ordering
document.AddChunks(chunks);

// ✗ Reaching past the root — nothing can enforce the (DocumentId, ChunkIndex) rule
chunkRepository.Add(new DocumentChunk(documentId, 7, text));
```

**Important caveat for this project:** a 300-page PDF yields thousands of chunks. Loading
the whole `Document` aggregate to add one chunk would be a performance disaster, and
"keep aggregates small" is standard DDD advice for exactly this reason. So in Step 8 the
bulk chunk write is a deliberate, documented exception — a set-based insert in
`EKA.Infrastructure` — while the `(DocumentId, ChunkIndex)` invariant is enforced by a
**unique database index** rather than by in-memory aggregate logic. This is the right
trade-off, and the pattern to notice is: when an invariant cannot live in the aggregate,
move it to a constraint that cannot be bypassed, and write down why.

### Domain events — record what happened

```csharp
public sealed record DocumentIngestedEvent(Guid DocumentId, int ChunkCount, DateTimeOffset At);
```

Raised by the aggregate, dispatched after the transaction commits. In Step 7 this
decouples ingestion from what happens next (notify the user, warm a cache, trigger
re-evaluation) without the pipeline knowing about any of it. In a monolith these are
in-process handlers; the same events become message-bus events if we ever split.

### Repository — a collection-like façade over one aggregate

One repository **per aggregate root**, not per table. Intention-named methods, no
`IQueryable` leaking out:

```csharp
public interface IDocumentRepository
{
    Task<Document?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Document>> GetPendingIngestionAsync(int limit, CancellationToken ct);
    Task AddAsync(Document document, CancellationToken ct);
}
```

Why not `IRepository<T>` with `GetAll()`: it moves query construction into use cases,
which means the ACL filter becomes something a developer must remember. Named methods let
the filter be a guaranteed part of the query. See
[design-patterns.md](design-patterns.md#repository).

---

## What we are deliberately NOT doing

| Pattern | Why we skip it |
|---|---|
| **Event sourcing** | Storing every state change as an event log is a large commitment for audit value we can get from an audit table |
| **Full CQRS with separate read/write databases** | Our read load is vector search, which already lives in Qdrant — a second relational read model adds sync complexity for nothing |
| **A mediator library (MediatR) from day one** | Handlers injected directly are simpler and traceable; add a mediator only when cross-cutting pipeline behaviours justify it |
| **Specification pattern everywhere** | Used in Step 9 for composable ACL filters, where it genuinely pays; not as a general query mechanism |
| **One microservice per bounded context** | See [ADR-0001](../adr/0001-modular-monolith.md) — boundaries are enforced by projects and folders, not by the network |

The guiding idea: bounded contexts and invariant-protecting entities are **cheap and pay
back immediately**. The heavy machinery is expensive and pays back only at a scale and
complexity we have not reached. Adopt in that order.

---

## Anemic vs rich domain model

This is the choice that decides whether the layering was worth it.

```csharp
// ✗ Anemic: a data bag. Any caller can set any state, in any order.
public class Document
{
    public Guid Id { get; set; }
    public string Title { get; set; }
    public IngestionStatus Status { get; set; }
}
// …and the rules live in a service, where nothing forces them to be applied
document.Status = IngestionStatus.Completed;   // even with zero chunks. Nothing stops it.

// ✓ Rich: the object refuses to be invalid
public void MarkIngested(int chunkCount)
{
    if (Status is not IngestionStatus.Extracting)
        throw new DomainException($"Cannot complete ingestion from state {Status}.");
    if (chunkCount <= 0)
        throw new DomainException("Ingestion cannot complete with no chunks.");

    Status      = IngestionStatus.Completed;
    IngestedAt  = DateTimeOffset.UtcNow;
    Raise(new DocumentIngestedEvent(Id, chunkCount, IngestedAt.Value));
}
```

The practical difference: in the anemic version, "a completed document always has chunks"
is a rule you hope every code path remembers. In the rich version it is a rule that cannot
be broken, and the test for it needs no database.

**Where anemic is fine:** DTOs in `EKA.Contracts` and EF configuration types are *supposed*
to be data bags. The rule applies to `EKA.Domain` only.

## Further reading

- Eric Evans, *Domain-Driven Design* (2003) — the strategic half, chapters 14–17
- Vaughn Vernon, *Implementing Domain-Driven Design* — the practical companion; the
  aggregate-design rules of thumb are in chapter 10
- Microsoft, [DDD-oriented microservice design](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/)
