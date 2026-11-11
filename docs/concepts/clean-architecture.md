# Clean Architecture in EKA

## The one rule

**Source code dependencies point inward, toward policy, never outward toward detail.**

"Policy" is what your software *means* — a chunk belongs to a document, a user may only
read what their ACL permits. "Detail" is how it happens to be done today — SQL Server,
Qdrant, OpenAI, HTTP. Policy is stable for years. Details get replaced.

If policy depends on detail, replacing a detail means rewriting your business rules. If
detail depends on policy, replacing it means writing one new class.

## The layers

```
┌───────────────────────────────────────────────────────────────┐
│  EKA.Api                    EKA.Ingestion.Worker              │  Entry points
│  HTTP endpoints, auth,      Hangfire jobs, pipeline host      │  (composition roots)
│  DI composition, OpenAPI                                      │
└────────────┬──────────────────────────┬───────────────────────┘
             │                          │
             ▼                          ▼
┌───────────────────────────┐  ┌────────────────────────────────┐
│  EKA.Infrastructure       │  │  EKA.AI                        │  Adapters
│  EF Core, SQL Server,     │  │  IChatClient / embeddings,     │  (implement the
│  repositories, blob       │  │  Qdrant client, prompt render  │   interfaces above)
│  storage, Redis, email    │  │                                │
└────────────┬──────────────┘  └───────────┬────────────────────┘
             │                             │
             └──────────────┬──────────────┘
                            ▼
             ┌──────────────────────────────────┐
             │  EKA.Application                 │  Use cases
             │  One folder per feature,         │  Declares the interfaces
             │  orchestration, authorization,   │  it needs — owns no detail
             │  validation, Result types        │
             └──────────────┬───────────────────┘
                            ▼
             ┌──────────────────────────────────┐
             │  EKA.Domain                      │  Policy
             │  Entities, value objects,        │  References NOTHING
             │  invariants, domain events       │
             └──────────────────────────────────┘

   EKA.Contracts — request/response DTOs. References nothing. Shared with the
                   Angular client via generated TypeScript types.
```

## The reference matrix

Enforced by the compiler. If a reference is not listed here, adding it is a bug.

| Project | May reference |
|---|---|
| `EKA.Domain` | **nothing** |
| `EKA.Contracts` | **nothing** |
| `EKA.Application` | `EKA.Domain`, `EKA.Contracts` |
| `EKA.Infrastructure` | `EKA.Application`, `EKA.Domain` |
| `EKA.AI` | `EKA.Application`, `EKA.Domain` |
| `EKA.Api` | all of the above |
| `EKA.Ingestion.Worker` | `EKA.Application`, `EKA.Infrastructure`, `EKA.AI`, `EKA.Domain` |

Note what is **absent**: `EKA.Application` cannot see `EKA.Infrastructure` or `EKA.AI`.
That is the whole point. It is also why `EKA.Api` must reference them — somebody has to
say "`IDocumentRepository` means `SqlDocumentRepository`", and that wiring belongs in the
composition root, at the outermost edge.

## How dependency inversion actually works here

The use case needs to save a document. Naively:

```csharp
// ✗ Application now depends on EF Core and SQL Server forever
public sealed class UploadDocumentHandler(EkaDbContext db)
{
    public async Task Handle(...) => await db.Documents.AddAsync(doc);
}
```

Inverted — `EKA.Application` *declares* what it needs:

```csharp
// EKA.Application/Abstractions/IDocumentRepository.cs
public interface IDocumentRepository
{
    Task AddAsync(Document document, CancellationToken ct);
    Task<Document?> GetByIdAsync(Guid id, CancellationToken ct);
}

// EKA.Application/Documents/Upload/UploadDocumentHandler.cs
public sealed class UploadDocumentHandler(IDocumentRepository repository)
{
    public async Task<Result<Guid>> Handle(UploadDocumentCommand cmd, CancellationToken ct)
    { /* pure orchestration, no storage knowledge */ }
}
```

`EKA.Infrastructure` implements it, and `EKA.Api` connects the two:

```csharp
// EKA.Api/Program.cs — the composition root, the ONLY place that knows both sides
builder.Services.AddScoped<IDocumentRepository, SqlDocumentRepository>();
```

**What this buys you concretely in this project:**
- Use-case tests run with an in-memory fake — no SQL Server container, millisecond tests.
- Step 9 can swap Qdrant for Azure AI Search by writing one class. The RAG logic in Step
  10 does not change by a single line.
- Step 5 can switch OpenAI → Azure OpenAI → Anthropic by registering a different
  `IChatClient`. No use case mentions a provider.

## What goes where — the decision table

Use this when you are unsure which project a new file belongs in.

| The code… | Project |
|---|---|
| …expresses a rule that would be true on paper, without computers | `EKA.Domain` |
| …describes a single thing a user can do, step by step | `EKA.Application` |
| …touches a database, file system, network, clock, or random number | `EKA.Infrastructure` |
| …talks to a model provider or vector store | `EKA.AI` |
| …parses HTTP, maps routes, validates a request shape, reads config | `EKA.Api` |
| …is a DTO crossing the wire to the client | `EKA.Contracts` |
| …is long-running, CPU-heavy, or must survive an API restart | `EKA.Ingestion.Worker` |

## Common mistakes (and the one we will actually be tempted by)

1. **Leaking EF Core into the domain.** `[Key]`, `[Table]`, or `virtual` navigation
   properties on entities. Fix: configure mappings in `EKA.Infrastructure` with
   `IEntityTypeConfiguration<T>` — the domain stays clean.
2. **Returning domain entities from the API.** It couples your public contract to your
   internal model, and it leaks fields the caller must not see. Use `EKA.Contracts` DTOs.
3. **Anemic domain.** Entities with only public getters and setters, all rules living in
   "services". You have paid the full ceremony cost of layering and received none of the
   benefit. See [ddd.md](ddd.md).
4. **An interface for every class.** `IDocumentMapper` with exactly one implementation
   that will never be swapped is pure noise. Invert dependencies that cross a boundary or
   need faking in tests — not everything. See [solid.md](solid.md).
5. **The generic repository trap.** `IRepository<T>` with `GetAll()` pushes query logic
   into use cases and makes ACL filtering easy to forget. In this project forgetting an
   ACL filter is a security incident, so we prefer intention-named methods like
   `GetAccessibleChunksAsync(userId, …)`.

## Honest limitations

Clean Architecture costs real effort: more projects, more interfaces, mapping between
domain objects and DTOs. For a CRUD app with ten endpoints it is over-engineering, and
[Vertical Slice Architecture](design-patterns.md#vertical-slices) would ship faster.

It earns its keep here for three specific reasons:
1. There are genuinely swappable details — vector store, model provider, file storage —
   and we will swap at least one of them.
2. The security rule (ACL-filtered retrieval) must be testable in isolation and provably
   enforced in one place.
3. The project is a learning vehicle; the explicit boundaries are part of the lesson.

## Further reading

- Robert C. Martin, *Clean Architecture* (2017) — the dependency rule, ch. 22
- Alistair Cockburn, *Hexagonal Architecture* — the same idea, ports and adapters framing
- Microsoft, [Common web application architectures](https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/common-web-application-architectures)
