# ADR-0002: Clean Architecture with seven projects

- **Status:** Accepted
- **Date:** 2026-11-11
- **Deciders:** project owner

## Context

Given a modular monolith ([ADR-0001](0001-modular-monolith.md)), the internal structure
has to come from somewhere. Three details in this system are realistically replaceable:

- the **vector store** (Qdrant today; Azure AI Search, pgvector, or Pinecone are live
  alternatives),
- the **model provider** (OpenAI, Azure OpenAI, others),
- the **file storage** (local disk, Azure Blob, S3-compatible).

One rule is also security-critical and must be provably enforced in one place: *every
retrieved chunk is filtered by the caller's effective access rights.*

## Decision

Adopt Clean Architecture with the dependency rule — source dependencies point inward —
across seven production projects and four test projects:

```
src/EKA.Domain/              references nothing
src/EKA.Contracts/           references nothing
src/EKA.Application/         → Domain, Contracts
src/EKA.Infrastructure/      → Application, Domain
src/EKA.AI/                  → Application, Domain
src/EKA.Api/                 → all
src/EKA.Ingestion.Worker/    → Application, Infrastructure, AI, Domain

tests/EKA.Domain.Tests/  EKA.Application.Tests/  EKA.AI.Tests/  EKA.IntegrationTests/
```

Interfaces are **declared by the consumer** (`EKA.Application`) and implemented by the
detail (`EKA.Infrastructure`, `EKA.AI`). `EKA.Api` is the composition root and the only
project that knows both sides.

Inside `EKA.Application`, files are organized as **vertical slices** (one folder per use
case), not as `Services/` and `Managers/` folders.

## Consequences

### Positive

- Domain rules are unit-testable with no database, no container, no network — tests run in
  milliseconds, which keeps them run frequently.
- Swapping the vector store is one new class implementing `IVectorStore`; the RAG logic is
  untouched.
- The ACL filter has exactly one home, so "is authorization enforced?" is answerable by
  reading one file.
- Illegal dependencies are **build errors**, not review comments.
- The structure maps onto the README's planned layout, so the roadmap and the code agree.

### Negative / costs we accept

- Mapping between domain entities and `EKA.Contracts` DTOs is real, repetitive work.
- A simple feature touches several projects, which feels slow for CRUD.
- Interfaces risk proliferating; `IFooMapper` with one implementation is noise. Mitigated
  by the rule in [solid.md](../concepts/solid.md): an abstraction must name a real seam —
  a swappable detail, a process boundary, or something that must be faked to test policy.
- Eleven projects slow the build versus one.

## Alternatives considered

**N-tier (UI → Business → Data).** Rejected: dependencies point downward, so business
logic depends on the data layer and the domain is again welded to EF Core. Clean
Architecture is N-tier with that one arrow inverted — same effort, strictly better
testability.

**Vertical Slice Architecture as the top-level structure** (feature folders, each
self-contained, no layer projects). A genuinely strong option that ships CRUD faster and is
arguably the better default for feature-heavy apps. Rejected as the *top-level* structure
because this system has a substantial shared core — retrieval, AI, access policy — used by
many features, and because the explicit layering is part of the learning goal. Adopted
*within* `EKA.Application` as the file organization, which captures most of its benefit.

**Single project with folders.** Rejected for the reasons in ADR-0001: folders do not stop
the domain from importing EF Core.

**Separate `EKA.Persistence` and `EKA.Storage` instead of one `EKA.Infrastructure`.**
Deferred — not yet enough code to justify the split. Revisit if `EKA.Infrastructure`
becomes unwieldy.

## Revisit when

- `EKA.Application` acquires a reference to `EKA.Infrastructure` or `EKA.AI` — that is not
  a revisit, that is a bug to fix.
- DTO mapping overhead becomes the dominant cost in adding a feature, suggesting the
  contract and domain models have converged and could be merged for simple cases.
- `EKA.Infrastructure` grows large enough that its concerns (SQL, blob storage, cache,
  email) deserve separate projects.
