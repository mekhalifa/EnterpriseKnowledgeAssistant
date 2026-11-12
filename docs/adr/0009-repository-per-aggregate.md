# ADR-0009: A repository per aggregate root, with tenant-scoped methods

- **Status:** Accepted
- **Date:** 2026-11-12
- **Deciders:** project owner

## Context

Step 3 had to choose how use cases reach the database. EF Core's `DbSet<T>` is already a
repository and `DbContext` is already a unit of work, so adding another layer needs a
reason beyond habit — "repositories are good practice" is not one.

The reason here is specific. This system's worst failure mode, recorded as the top risk in
`PROGRESS.md`, is one tenant or user reading another's content. Every query against
`Documents`, `Conversations`, and `Users` must be scoped. With `DbContext` injected
directly into handlers, that scoping is a `.Where()` clause a developer writes at each call
site and can forget. There is no compiler error, no failing test, and no runtime
complaint — only a quiet data leak.

## Decision

One repository interface per aggregate root, declared in `EKA.Application` and implemented
`internal` in `EKA.Infrastructure`:

`IDocumentRepository`, `IConversationRepository`, `IUserRepository`,
`IPromptTemplateRepository`, `IAIProviderRepository`.

Rules that come with it:

1. **Every tenant-scoped method takes `TenantId` as a parameter**, and applies it inside
   the query. Conversation methods additionally take the owning `userId`.
2. **No method returns `IQueryable`.** Returning one hands query construction back to the
   caller and reopens the hole the interface closes.
3. **Methods are named for intent**, not for CRUD: `GetWithChunksAsync` is separate from
   `GetByIdAsync` because loading thousands of chunks by accident is a performance
   incident, and the name is what makes that choice deliberate.
4. **No repository calls `SaveChangesAsync`.** Writes are staged; the use case commits once
   through `IUnitOfWork`, which is what lets one transaction span two aggregates.
5. Implementations are `internal`, so nothing outside `EKA.Infrastructure` can name them.

Documented exceptions, each carrying a comment explaining why:

- `GetPendingIngestionAsync` is not tenant-scoped — the ingestion worker processes the
  queue across all tenants, and it returns work items rather than content.
- `FindByExternalIdAsync` is not tenant-scoped, and cannot be: it runs during sign-in,
  *before* the tenant is known, because the user record is what reveals the tenant. Safe
  because `ExternalId` is globally unique and arrives in a signed token.
- `IPromptTemplateRepository` and `IAIProviderRepository` are not tenant-scoped at all —
  they hold operator configuration, not tenant data.

## Consequences

### Positive

- The tenant filter is part of a method's identity. A caller cannot forget a filter it was
  never able to write.
- Use-case tests run against a fake repository with no database.
- Query shapes are visible in one file per aggregate, so a slow query has an obvious home.
- `GetActiveAsync` returning a single `PromptTemplate` encodes a database guarantee in the
  signature: callers cannot write code handling "two active templates", a case the filtered
  unique index makes impossible.

### Negative / costs we accept

- More code than injecting `DbContext`, and a new query means a new method rather than a
  LINQ expression written in place.
- Some LINQ flexibility is given up deliberately. When a use case needs a projection the
  repository does not expose, the honest answer is to add a method, not to leak
  `IQueryable`.
- The abstraction is partly a fiction: `DbUpdateException` from a unique index still
  escapes to callers, so the persistence technology is not fully hidden. That is accepted —
  the goal was query scoping, not provider independence.

## Alternatives considered

**`DbContext` injected directly into handlers.** Simpler, less code, full LINQ, and widely
recommended — Microsoft's own guidance notes that EF already implements both patterns. It
would win on a system with no cross-cutting query rule. Rejected here solely because the
ACL/tenant filter must not be optional.

**Generic `IRepository<T>` with `GetAll()` / `Find(predicate)`.** Rejected firmly. It
cannot express aggregate-specific queries, so logic migrates back into use cases, and a
`Find(predicate)` signature lets any caller pass any predicate — reintroducing the exact
problem, now with ceremony on top.

**CQRS with a separate read model.** Reads through hand-written SQL or Dapper, writes
through repositories. Worth revisiting if EF's generated SQL becomes a measured bottleneck
at Step 9 or Step 11, but premature now: there is no measurement showing a problem.

**EF global query filters** (`HasQueryFilter`) for tenant scoping. Genuinely attractive —
it applies automatically to every query. Rejected as the *primary* mechanism because the
tenant must come from ambient state, which makes it invisible at the call site and easy to
bypass with `IgnoreQueryFilters()`. Worth reconsidering at Step 12 as **defence in depth**
layered on top of explicit parameters, not as a replacement for them.

## Revisit when

- A use case repeatedly needs projections the repositories do not expose, suggesting a read
  model is warranted.
- Step 12's security review evaluates global query filters as an additional layer.
- Measurement shows EF's generated SQL is the bottleneck on the retrieval path.
