# Design Patterns used in EKA

Every pattern below is one we will actually write, with the step it appears in and the
specific problem it solves. A pattern with no problem behind it is just extra indirection,
so the "why here" column is the important one.

## Quick map

| Pattern | Where in EKA | Step |
|---|---|---|
| [Repository](#repository) | Document, Conversation persistence | 3 |
| [Unit of Work](#unit-of-work) | Transaction across chunk + document writes | 3 |
| [Result](#result) | Every use case's return type | 4 |
| [Options](#options) | All configuration binding | 4 |
| [Adapter](#adapter) | OpenAI / Azure OpenAI / Qdrant clients | 5, 9 |
| [Strategy](#strategy) | Chunking algorithms, retrieval modes | 8, 9 |
| [Factory](#factory) | Resolving a provider by configured name | 5 |
| [Decorator / Pipeline](#decorator) | Logging, retry, caching around `IChatClient` | 5 |
| [Builder](#builder) | Prompt assembly with a token budget | 6, 10 |
| [Chain of Responsibility](#chain-of-responsibility) | The ingestion pipeline stages | 7 |
| [Specification](#specification) | Composable ACL and metadata filters | 9 |
| [Domain Events + Outbox](#domain-events) | Post-ingestion side effects | 7 |
| [Circuit Breaker / Retry](#resilience) | Provider calls | 5 |
| [Vertical slices](#vertical-slices) | Folder layout inside `EKA.Application` | 4 |

---

## Repository

**Problem:** use cases need to load and save aggregates without knowing SQL exists, and
every query touching documents must be ACL-filtered.

**Shape:** one interface per aggregate root, declared in `EKA.Application`, implemented in
`EKA.Infrastructure`. Intention-named methods only.

```csharp
public interface IDocumentRepository
{
    Task<Document?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Document>> GetPendingIngestionAsync(int limit, CancellationToken ct);
    Task AddAsync(Document document, CancellationToken ct);
}
```

**Why here, when EF Core's `DbSet` is already a repository:** that criticism is fair, and
for many apps injecting `DbContext` into handlers is the right, simpler answer. Our reason
to add the layer is the security rule — `GetAccessibleChunksAsync(userId, …)` makes the
ACL filter part of the method's identity, where a raw `IQueryable` makes it something a
developer must remember on every call site. One forgotten `.Where()` is a data breach.

**Alternatives:**

- **`DbContext` directly in handlers** — less code, full LINQ power, no leaky abstraction.
  Best when there is no cross-cutting query rule to enforce.
- **Generic `IRepository<T>`** — avoid. It cannot express aggregate-specific queries, so
  logic migrates into use cases and the ACL guarantee evaporates.
- **CQRS split** — commands through repositories, reads through hand-written SQL or Dapper.
  Worth considering in Step 9 if EF Core's generated SQL becomes a bottleneck.

---

## Unit of Work

**Problem:** ingesting a document writes the document row, many chunk rows, and a status
change. Half-applied, that leaves a "completed" document with no chunks.

**Shape:** EF Core's `DbContext` **is** a Unit of Work — change tracking plus one
`SaveChangesAsync` equals one transaction. We expose it narrowly:

```csharp
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct);
}
```

Repositories stage changes; the use case commits once, at the end. Do not call
`SaveChangesAsync` inside a repository method — that destroys the caller's ability to
compose a transaction.

---

## Result

**Problem:** "document not found" and "file type unsupported" are **expected** outcomes,
not bugs. Throwing exceptions for them is slow, hides the API contract, and makes every
caller guess what might come flying out.

**Shape:**

```csharp
public readonly record struct Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public Error? Error { get; }

    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(Error error) => new(false, default, error);
}

public sealed record Error(string Code, string Message, ErrorType Type);
public enum ErrorType { Validation, NotFound, Conflict, Forbidden, Provider }
```

The endpoint maps `ErrorType` to an HTTP status once, in one place, producing RFC 9457
`ProblemDetails`. The method signature now tells the truth about what can happen.

**Keep exceptions for:** genuinely exceptional states — a broken invariant
(`DomainException`), a missing connection string, a bug. Those *should* crash loudly.

**Alternatives:** exceptions everywhere (simple, but control flow by exception is slow and
opaque); `OneOf`/`LanguageExt` discriminated unions (more powerful, heavier dependency and
a steeper learning curve).

---

## Options

**Problem:** configuration read as `IConfiguration["Ai:OpenAi:Model"]` is stringly-typed,
unvalidated, and fails at the moment of use rather than at startup.

**Shape:**

```csharp
public sealed class ChunkingOptions
{
    public const string SectionName = "Ingestion:Chunking";

    [Range(100, 8000)]  public int MaxTokens { get; init; } = 1000;
    [Range(0, 2000)]    public int OverlapTokens { get; init; } = 200;
    [Required]          public string Strategy { get; init; } = "sentence";
}

builder.Services.AddOptions<ChunkingOptions>()
    .BindConfiguration(ChunkingOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();        // ← fail at boot, not at 3 a.m. mid-ingestion
```

`ValidateOnStart()` is the part people omit and the part that matters: a misconfigured
overlap larger than the chunk size should stop the app from starting, not silently produce
garbage vectors.

**Which interface to inject:** `IOptions<T>` for values fixed at startup (the common case),
`IOptionsSnapshot<T>` for per-request refresh, `IOptionsMonitor<T>` for live reload in
singletons.

---

## Adapter

**Problem:** OpenAI, Azure OpenAI, and Qdrant each have their own SDK types. If those
types appear in our use cases, switching provider means rewriting the application layer.

**Shape:** `EKA.AI` wraps each SDK behind an interface owned by `EKA.Application`.
Microsoft.Extensions.AI already supplies the target abstractions — `IChatClient` and
`IEmbeddingGenerator<string, Embedding<float>>` — which is precisely why the README chose
it. We adapt *to* those rather than inventing our own.

```csharp
// EKA.AI/Providers/QdrantVectorStore.cs — our own adapter, since no std abstraction exists
public sealed class QdrantVectorStore(QdrantClient client, IOptions<QdrantOptions> options)
    : IVectorStore
{
    public async Task UpsertAsync(IEnumerable<VectorRecord> records, CancellationToken ct)
    { /* translate VectorRecord → Qdrant PointStruct */ }
}
```

**The discipline:** no Qdrant, OpenAI, or Azure type may appear in a signature outside
`EKA.AI`. Enforced by the project reference rules — those packages are only installed
there.

---

## Strategy

**Problem:** Step 8 needs multiple chunking algorithms and Step 11 compares them in
repeatable runs. A `switch` means every experiment edits production code.

**Shape:** an interface plus one class per algorithm, selected by configuration.

```csharp
public interface IChunkingStrategy
{
    string Name { get; }
    IReadOnlyList<TextChunk> Chunk(ExtractedText text, ChunkingOptions options);
}
```

Registered as a set; resolved by name. Full example in [solid.md](solid.md#o--openclosed-principle).

**Also used for:** retrieval mode (dense / keyword / hybrid) in Step 9, text extraction per
file type in Step 7 (PDF, DOCX, HTML, plain text — one extractor each, chosen by MIME type).

---

## Factory

**Problem:** which chat provider to use is a runtime value — a config setting, or in a
multi-tenant setup a per-tenant choice. Constructor injection alone cannot express
"give me the one named in this string".

**Shape:**

```csharp
public interface IChatClientFactory
{
    IChatClient Create(string providerName);
}
```

**Keyed DI is often better.** .NET 8+ has this built in, and it removes the need for a
hand-written factory in the simple case:

```csharp
builder.Services.AddKeyedSingleton<IChatClient>("openai", …);
builder.Services.AddKeyedSingleton<IChatClient>("azure",  …);

// consumer
public sealed class ChatHandler([FromKeyedServices("openai")] IChatClient client) { }
```

Use keyed DI when the key is known at compile time; use the factory when the key is a
runtime value such as a tenant's configured provider.

---

## Decorator

**Problem:** every provider call needs logging, retry, telemetry, and caching. Putting
those inside the provider adapter mixes five concerns into one class, and duplicates them
per provider.

**Shape:** wrap the same interface, delegating inward. Microsoft.Extensions.AI is designed
for exactly this and gives it to us as a fluent builder:

```csharp
builder.Services.AddChatClient(sp => new OpenAIClient(key).AsChatClient("gpt-5"))
       .UseLogging()
       .UseDistributedCache()
       .UseOpenTelemetry()
       .UseFunctionInvocation();
```

Each `Use…` returns an `IChatClient` wrapping the previous one — a pipeline, assembled at
registration time. This is the same idea as ASP.NET Core middleware and
`DelegatingHandler` in `HttpClient`.

**Why this is the right place to learn it:** it shows that a decorator chain lets you add a
cross-cutting concern *without touching either the caller or the implementation* — the
cleanest demonstration of Open/Closed in the codebase.

---

## Builder

**Problem:** the RAG prompt in Step 10 assembles a system instruction, conversation
history, retrieved chunks, and the question — under a hard token budget, dropping the
least valuable material first. A constructor with eight parameters cannot express that.

**Shape:**

```csharp
var prompt = new RagPromptBuilder(template, tokenCounter)
    .WithSystemInstruction(template.SystemPrompt)
    .WithHistory(conversation.RecentMessages, maxTurns: 6)
    .WithRetrievedContext(chunks, maxTokens: 4000)   // truncates by score order
    .WithQuestion(question)
    .Build();                                        // validates the total budget
```

The builder owns the budget arithmetic and the drop-order policy — logic that would
otherwise smear across the handler.

---

## Chain of Responsibility

**Problem:** ingestion is a sequence of stages — validate, store, extract, normalize,
chunk, embed, index — where each can fail, must be retryable, and must report progress.

**Shape:** uniform stages over a shared context object.

```csharp
public interface IIngestionStage
{
    string Name { get; }
    Task<Result> ExecuteAsync(IngestionContext context, CancellationToken ct);
}
```

The pipeline runs them in order, persisting status after each, so a retry resumes rather
than restarting. Adding OCR later means adding a stage, not editing the pipeline.

**Alternative:** Hangfire job continuations, where each stage is a separate job. More
durable across process restarts, more moving parts. Decide in Step 7 — recorded as an open
decision in [PROGRESS.md](../PROGRESS.md).

---

## Specification

**Problem:** retrieval filters combine tenant, document status, authority threshold, and
the user's ACL. Hand-writing that boolean soup at each call site is where a leak hides.

**Shape:** small composable filter objects translated once into a Qdrant filter.

```csharp
var filter = Spec.Tenant(user.TenantId)
          .And(Spec.Status(IngestionStatus.Completed))
          .And(Spec.AccessibleBy(user.Id, user.Roles))
          .And(Spec.MinAuthority(options.MinAuthority));
```

**Why it earns its place here specifically:** `Spec.AccessibleBy` becomes a single unit
under test, and the retrieval query cannot be built without composing it. The pattern is
often over-used for ordinary queries — we restrict it to this one high-stakes path.

---

## Domain Events

**Problem:** when ingestion completes, several unrelated things should happen. Calling them
from the pipeline makes it depend on all of them.

**Shape:** the aggregate raises an event; handlers subscribe. Dispatch **after** the
transaction commits, so no handler acts on a state that then rolls back.

For reliability — the side effect must not be lost if the process dies after commit —
pair this with the **Outbox pattern**: events are written to an outbox table inside the
same transaction, and a background dispatcher publishes them. Introduce the outbox only
when a lost side effect actually matters; in-process dispatch is fine for Step 7's
notifications.

---

## Resilience

**Problem:** model provider APIs rate-limit (429), time out, and have transient outages. A
naive retry loop makes an overload worse.

**Shape:** `Microsoft.Extensions.Http.Resilience` / Polly, configured once:

- **Timeout** per attempt, so a hung call cannot block a worker thread forever.
- **Retry** with exponential backoff **and jitter** — jitter prevents a thundering herd of
  synchronized retries.
- **Circuit breaker** — after repeated failures, fail fast for a cooldown instead of
  queueing doomed calls.
- Retry only **idempotent** operations. Embedding generation is safe to retry; a chat
  completion that already charged tokens may not be worth retrying blindly.

---

## Vertical slices

Not a GoF pattern, but it decides our folder layout. Inside `EKA.Application`, organize by
**feature**, not by technical kind:

```
EKA.Application/
  Documents/
    Upload/            UploadDocumentCommand.cs, Handler.cs, Validator.cs
    GetById/           …
  Conversations/
    SendMessage/       SendMessageCommand.cs, Handler.cs, Validator.cs
  Retrieval/
    Search/            …
  Abstractions/        IDocumentRepository.cs, IVectorStore.cs, IChatCompletionService.cs
```

**Why:** `Services/`, `Managers/`, `Helpers/` folders force you to open four directories to
understand one feature, and they grow into grab-bags. With slices, everything for "upload a
document" sits together, and deleting the feature means deleting a folder.

This combines well with Clean Architecture: layers define *what may depend on what*; slices
define *how files are grouped inside a layer*.

## Further reading

- Gamma et al., *Design Patterns* (1994) — the original catalogue
- Martin Fowler, *Patterns of Enterprise Application Architecture* — Repository, Unit of
  Work, and friends
- [Microsoft.Extensions.AI pipeline documentation](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Polly resilience strategies](https://www.pollydocs.org/)
