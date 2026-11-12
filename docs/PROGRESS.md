# PROGRESS

Single source of truth for project state. Updated at the end of every working session.

**Last updated:** 2026-11-12
**Current step:** Step 3 complete → Step 4 next
**Overall:** Phase A, 3 of 14 steps complete
**Branch:** `step-03-persistence` (branched from `step-02-domain-model`; neither merged to `main`)

---

## ▶ NEXT ACTION

**Step 4 — application layer and the API surface.**

1. `Result<T>` and `Error` in `EKA.Application`, with an `ErrorType` enum
   (Validation / NotFound / Conflict / Forbidden / Provider).
2. Use cases as vertical slices — one folder per feature under
   `EKA.Application/Documents/…`, `Conversations/…`, each with Command, Handler, Validator.
3. FluentValidation wired as a minimal-API endpoint filter.
4. Endpoint groups in `EKA.Api/Endpoints/`, one static class per feature, registered with a
   single `Map…Endpoints()` call each.
5. **ProblemDetails mapping** — including `InvalidStateTransitionException` → HTTP 409,
   which Step 2 designed for and nothing has exercised yet.
6. Health checks: liveness (`/health/live`) and readiness (`/health/ready`, checks SQL
   Server). The API currently serves **no routes at all**.
7. `EKA.Contracts` DTOs; never return domain entities from an endpoint.
8. `.editorconfig` — deferred twice, and there is now real code to write rules against.
9. Tests in `EKA.Application.Tests` using fake repositories (no database).

**Decision needed first:** none blocking. Open: whether to adopt a mediator — recommend
**not** yet; inject handlers directly until cross-cutting pipeline behaviour justifies one.

Reference: [concepts/minimal-apis-vs-controllers.md](concepts/minimal-apis-vs-controllers.md),
[concepts/design-patterns.md](concepts/design-patterns.md#result).

---

## Done

### Session 1 — 2026-11-11

#### Setup

- Verified toolchain: .NET SDK 10.0.403, Docker 29.1.5, git 2.48.1, Node 24.15.0.
- `git init` on `main`; official .NET `.gitignore` plus a `.gitattributes` normalizing
  line endings.
- Ran `/doctor`: installation healthy, Claude Code 2.1.322 current. No changes applied.

#### Project skills (`.claude/skills/`)

- `next-step`, `new-adr`, `verify` — the working rhythm, invocable, checked in.
- No `.claude/settings.json`: its useful contents would be permission allow-rules, and the
  owner approves actions individually.

#### Step 1 — solution skeleton ✅

- `global.json` (SDK 10.0.403), `Directory.Build.props` (net10.0, nullable,
  warnings-as-errors), `Directory.Packages.props` (CPM), `nuget.config` (source mapping).
- 7 production projects, 4 test projects, 18 references wired inward-only.
- 19 architecture tests; each test project guards its own boundary.
- **Found: dependency confusion.** Three unmapped NuGet feeds → NU1507. Fixed with package
  source mapping, not suppression. [ADR-0006](adr/0006-package-source-mapping.md).

#### Step 2 — domain model ✅

- 26 files, 1,640 lines; `EKA.Domain` still references nothing.
- 7 entities with no public setters, 9 value objects, 2 domain events, 2 exception types.
- `Document` lifecycle state machine; `Reset` discards chunks, making re-ingestion
  idempotent.
- **179 domain tests.**
- Decided: Shouldly ([ADR-0007](adr/0007-assertion-library.md)); the domain never reads the
  clock ([ADR-0008](adr/0008-deterministic-time-in-domain.md)).
- **Corrected assumption:** `List<T>.AsReadOnly()` returns `ReadOnlyCollection<T>`, which
  *does* implement `ICollection<T>` with throwing write members — a runtime guard, not a
  type-system impossibility.

### Session 2 — 2026-11-12

#### Step 3 — persistence ✅

- `EkaDbContext` implementing `IUnitOfWork`; 7 entity configurations; 6 value converters
  registered once by CLR type; `FileMetadata`, `PageRange`, `Citation` as owned types.
- 5 repositories, `internal`, every tenant-scoped method taking `TenantId` and applying it
  inside the query. No method returns `IQueryable`.
- `AuditInterceptor` fills `CreatedBy`/`ModifiedBy`; never rewrites `CreatedBy` on update;
  never touches domain timestamps.
- Domain events collected before commit, dispatched after, cleared in between.
- `InitialSchema` migration: 8 tables, 12 indexes. Applied to a clean database **and** to
  the containerized one.
- `deploy/docker-compose.yml`: SQL Server 2022, no default password, localhost-only port
  binding, named volume, query-based health check.
- `dotnet-ef` pinned to 10.0.15 in `.config/dotnet-tools.json`.
- **45 integration tests against real SQL Server; 233 tests total.**
- Removed the weather-forecast template from `EKA.Api`.
- Decided: [ADR-0009](adr/0009-repository-per-aggregate.md) repository per aggregate;
  [ADR-0010](adr/0010-value-object-mapping.md) converters vs owned types.

**Three things went wrong, all recorded in
[step-03](steps/step-03-persistence.md):**

1. `dotnet add package` installed EF Core 11.0.0, which targets `net11.0` only (NU1202).
   Fixed by pinning the Microsoft family to `$(MicrosoftRuntimeVersion)` = 10.0.15.
2. An XML comment containing `--` made `Directory.Packages.props` unloadable, which
   silently disabled CPM and produced NU1015 on all eleven projects — an error naming the
   consequence, never the cause.
3. A test of mine asserted the wrong exception: EF rejects severing a required FK
   **client-side at `Remove()`** (`InvalidOperationException`) when the dependent is
   tracked, and only reaches the database constraint when it is not. The test now asserts
   both layers.

---

## Step checklist

| # | Step | Status |
|---|---|---|
| 1 | Solution skeleton + architecture decision | ✅ Complete — 19 tests |
| 2 | Domain model | ✅ Complete — 191 tests |
| 3 | Persistence: EF Core + SQL Server in Docker | ✅ Complete — 233 tests |
| 4 | Application layer + API surface | ⬜ Next |
| 5 | AI providers via Microsoft.Extensions.AI | ⬜ Not started |
| 6 | Prompt templates + chat API | ⬜ Not started |
| 7 | Document ingestion | ⬜ Not started |
| 8 | Chunking + embeddings | ⬜ Not started |
| 9 | Vector retrieval with ACL filtering | ⬜ Not started |
| 10 | RAG answer generation with citations | ⬜ Not started |
| 11 | Evaluation | ⬜ Not started |
| 12 | Security and enterprise controls | ⬜ Not started |
| 13 | Angular 21 client | ⬜ Not started |
| 14 | Deployment and release readiness | ⬜ Not started |

---

## Decisions still to make

| When | Decision | Options |
|---|---|---|
| Step 4 | Mediator or direct handler injection | **Direct injection** recommended; add MediatR only when cross-cutting pipeline behaviour justifies it |
| Step 5 | Primary AI provider | Azure OpenAI (enterprise, private networking) vs OpenAI direct |
| Step 5 | Embedding model + dimension | Fixes the Qdrant collection shape. `AIProvider.EmbeddingDimensions` already records it per provider |
| Step 7 | Background job engine | Hangfire (SQL-backed, dashboard) vs a queue |
| Step 7 | File storage | Local disk / Azure Blob / S3-compatible (MinIO) |
| Step 9 | Hybrid search | Dense-only first; add BM25 only if Step 11 shows a gap |
| Step 9 | ACL model | `DocumentAcl` deliberately not built — designed with access-filtered retrieval |
| Step 12 | Identity provider | Entra ID vs Keycloak vs ASP.NET Core Identity |
| Step 12 | EF global query filters | As *defence in depth* on top of explicit tenant parameters, not instead of them — see ADR-0009 |

---

## Risks being tracked

| Risk | Why it matters | Mitigation |
|---|---|---|
| ACL leak through retrieval | The worst failure mode — one user reading another's documents | Tenant filter inside every repository method (ADR-0009); vector-query filtering in Step 9; security tests in Step 12 |
| Embedding dimension change | Forces a full re-index | Recorded per provider and per chunk, so stale vectors are identifiable |
| Prompt injection via document content | Uploaded documents are untrusted input | `MessageRole` is a trust boundary; tested in Step 10 |
| Provider cost runaway | Embedding large corpora gets expensive fast | Token budgets, caching, rate limits in Step 12 |
| Scope creep into agents/GraphRAG | Burns the 12 weeks before the basics work | The out-of-scope list in the learning path |
| Supply chain via NuGet | A malicious package executes at build time | Source mapping (ADR-0006); `packages.lock.json` at Step 14 |

---

## Invariants enforced outside the aggregate

| Invariant | New home | Status |
|---|---|---|
| Chunk indexes unique per document | `UX_DocumentChunks_DocumentId_ChunkIndex` | ✅ Created and tested |
| At most one active prompt template per name | `UX_PromptTemplates_Name_Active`, filtered `[IsActive] = 1` | ✅ Created and tested |
| `default(TenantId)` must never persist | `IsEmpty` check in every aggregate constructor + non-nullable column | ✅ Both halves done |

---

## Known limitations of the current code

- **The API serves no routes.** Step 3 wired persistence only; endpoints, validation,
  ProblemDetails and health checks arrive in Step 4.
- **`LoggingDomainEventDispatcher` invokes no handlers.** It logs each event so the wiring
  is visibly working and visibly incomplete. Real handlers arrive in Step 7.
- **Audit columns carry no real attribution yet.** No identity provider is configured until
  Step 12, so `ICurrentUser` returns null and every write is stamped `"system"`.
- **No `.editorconfig`.** Deferred twice; scheduled for Step 4.
- Architecture tests use `Assembly.GetReferencedAssemblies()`, which the compiler trims to
  references compiled code actually uses.
- `DocumentAcl` / document-level permissions are not modelled — Step 9.
- Integration tests require Docker. Without it they fail rather than skip, which is
  deliberate: a silently skipped test suite is worse than a failing one.
- The IDE's Roslynator analyzer reports RCS1194 and RCS1170 on domain types. Both are
  **deliberately not followed** — a sealed domain exception does not want the three
  standard constructors, and private setters are required for EF materialization.
  `dotnet build` is clean; these are editor-only suggestions.
