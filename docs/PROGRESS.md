# PROGRESS

Single source of truth for project state. Updated at the end of every working session.

**Last updated:** 2026-11-12
**Current step:** Step 4 complete → Step 5 next
**Overall:** Phase A complete, 4 of 14 steps done
**Branch:** `step-04-application-api` (branched from `step-03-persistence`; none merged to `main`)

---

## ▶ NEXT ACTION

**Step 5 — AI providers through Microsoft.Extensions.AI.**

1. `IChatCompletionService` and an embedding abstraction declared in `EKA.Application`;
   implemented in `EKA.AI` over `Microsoft.Extensions.AI`. **No provider SDK type may
   appear in `EKA.Application`** — the boundary test already guards this.
2. Register `IChatClient` and `IEmbeddingGenerator` for the chosen provider; Factory or
   keyed services for provider selection, so `AIProvider` rows mean something.
3. Secrets from User Secrets locally (`Ai:OpenAi:ApiKey`), environment variables in
   containers. Never `appsettings.json`.
4. Options pattern with `ValidateOnStart()` for model names, endpoints and dimensions — a
   wrong embedding dimension must stop the app, not produce garbage vectors.
5. Resilience pipeline via `Microsoft.Extensions.Http.Resilience`: timeout, retry with
   jittered backoff, and a circuit breaker. Decorator/middleware shape.
6. `ErrorType.Provider` → 502 already exists and nothing has used it yet.
7. A smoke test proving one chat call and one embedding call succeed, skippable without a
   key configured — and a unit test of the resilience behaviour that needs no key.

**Decisions needed first — both blocking, settle before coding:**

| Decision | Recommendation |
|---|---|
| Primary provider | **Azure OpenAI** if an Azure subscription is available (private networking, data-residency commitments, enterprise agreement); **OpenAI direct** otherwise, which is faster to start and needs no Azure resource. The abstraction makes this cheap to change; the *configuration shape* is not. |
| Embedding model + dimension | **`text-embedding-3-small` at 1536 dimensions** unless retrieval quality in Step 11 says otherwise. This fixes the Qdrant collection shape in Step 8 and changing it later forces a full re-index. `AIProvider.EmbeddingDimensions` already records it per provider. |

Reference: [concepts/design-patterns.md](concepts/design-patterns.md) (Adapter, Strategy,
Decorator, Options), [adr/0002](adr/0002-clean-architecture-layering.md).

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

#### Step 4 — application layer and API surface ✅

- `Result` / `Result<T>` / `Error` / `ErrorType`; one `ErrorType` → HTTP status map in
  `ErrorResults`; `Error.Code` rides along as a ProblemDetails extension member.
- Six document use cases as vertical slices (Register, Get, List, Rename, Reset, Delete),
  each `Command/Query + Handler (+ Validator)`. Handlers registered by hand; validators
  scanned for.
- **Seven endpoints**, plus `/health/live` and `/health/ready`. `EKA.Contracts` DTOs only;
  no domain entity crosses an endpoint.
- `IDocumentRepository.ListAsync` added for paged, tenant-scoped listing returning items
  and total from one consistent read.
- **Development authentication scheme** behind three gates, so the tenant comes from an
  authenticated principal and never from request content. `NoIdentityProviderHandler`
  answers 401 when it is off, instead of 500.
- `.editorconfig` written, including deliberate `none` for RCS1194 and RCS1170 with the
  reasons recorded — the IDE annoyance tracked since Step 2 is resolved.
- **322 tests** (78 application, 63 integration of which 18 are end-to-end over HTTP).
- Decided: [ADR-0011](adr/0011-result-pattern.md) Result vs exceptions;
  [ADR-0012](adr/0012-vertical-slices-no-mediator.md) slices, no mediator;
  [ADR-0013](adr/0013-validation-placement.md) validation placement;
  [ADR-0014](adr/0014-development-authentication.md) development authentication.

**Scope deliberately narrowed, and why:** Conversation endpoints were *not* built. Step 6's
exit criterion is "a client creates a conversation, asks a question, and reads back
persisted messages", so they belong there. Document upload of actual bytes is Step 7.

**Two things went wrong, both recorded in
[step-04](steps/step-04-application-api.md):**

1. **`app.UseExceptionHandler()` silently converted a correct 400 into a 500** for malformed
   JSON, across every endpoint at once. `BadHttpRequestException` already carries a 400;
   once an exception handler is registered, the unclaimed exception falls through to the
   fallback, which writes 500. Fixed with `BadRequestExceptionHandler` registered first.
   **Only an end-to-end test could have found this.**
2. `WebApplicationFactory.ConfigureAppConfiguration` runs *after* `Program.cs` has already
   read `builder.Configuration`, so 18 tests failed on the application's own "connection
   string is not configured" guard while plainly configuring one. `UseSetting` writes into
   host configuration, which is present early enough.

---

## Step checklist

| # | Step | Status |
|---|---|---|
| 1 | Solution skeleton + architecture decision | ✅ Complete — 19 tests |
| 2 | Domain model | ✅ Complete — 191 tests |
| 3 | Persistence: EF Core + SQL Server in Docker | ✅ Complete — 233 tests |
| 4 | Application layer + API surface | ✅ Complete — 322 tests |
| 5 | AI providers via Microsoft.Extensions.AI | ⬜ Next |
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
| Step 5 | **Primary AI provider** — blocking | Azure OpenAI (enterprise, private networking) vs OpenAI direct. Recommendation in the next-action block above |
| Step 5 | **Embedding model + dimension** — blocking | Fixes the Qdrant collection shape in Step 8; changing it later forces a full re-index. `AIProvider.EmbeddingDimensions` records it per provider |
| Step 6 | Conversation endpoints | Deferred from Step 4 on purpose; they need a local user id, which needs just-in-time user provisioning from the identity provider's subject claim |
| Step 7 | Background job engine | Hangfire (SQL-backed, dashboard) vs a queue |
| Step 7 | Unique index on `(TenantId, ContentHash)` | Closes the registration race left open in Step 4. Natural to add with the real upload path |
| Step 7 | File storage | Local disk / Azure Blob / S3-compatible (MinIO) |
| Step 9 | Hybrid search | Dense-only first; add BM25 only if Step 11 shows a gap |
| Step 9 | ACL model | `DocumentAcl` deliberately not built — designed with access-filtered retrieval |
| Step 12 | Identity provider | Entra ID vs Keycloak vs ASP.NET Core Identity |
| Step 12 | EF global query filters | As *defence in depth* on top of explicit tenant parameters, not instead of them — see ADR-0009 |

---

## Risks being tracked

| Risk | Why it matters | Mitigation |
|---|---|---|
| ACL leak through retrieval | The worst failure mode — one user reading another's documents | Tenant filter inside every repository method (ADR-0009); **proved across HTTP in Step 4** by two-tenant end-to-end tests; vector-query filtering in Step 9; security tests in Step 12 |
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
| Same bytes registered once per tenant | Content-hash lookup in `RegisterDocumentHandler` | ⚠️ Handler half done; the unique index that closes the race is Step 7 |

---

## Known limitations of the current code

- **Authentication is a development stand-in.** `DevelopmentHeader` trusts request headers
  and authenticates nobody. Three gates keep it in Development (default-false flag,
  `IsDevelopment()` check, startup throw elsewhere), and the third is asserted by reading
  the code rather than by a test. Deleted, not disabled, in Step 12
  ([ADR-0014](adr/0014-development-authentication.md)).
- **`appsettings.Development.json` ships with the scheme enabled**, so a developer who
  renames their environment hits the startup throw. That is the designed behaviour, but it
  is a surprise worth knowing about in advance.
- **Registering a document races itself.** Two concurrent registrations of identical bytes
  can both miss the content-hash check and both insert. Cost today is a wasted row, not a
  correctness failure. Closed by a unique index on `(TenantId, ContentHash)` in Step 7.
- **Deleting a document removes the row and nothing else.** The stored file and the vectors
  do not exist yet; Step 8 owns making deletion complete.
- **No conversation endpoints.** Deferred to Step 6 deliberately — see the decision table.
- **`ListAsync` uses offset paging**, so a deep page scans and discards, and an insert
  between two requests can shift a row across a page boundary. Correct for a page-numbered
  UI; revisit if a tenant's document count reaches the thousands.
- **`.editorconfig` does not gate the build.** `EnforceCodeStyleInBuild` is off on purpose
  (see step-04 §9); `dotnet format --verify-no-changes` becomes a CI step in Step 14.
- **`LoggingDomainEventDispatcher` invokes no handlers.** It logs each event so the wiring
  is visibly working and visibly incomplete. Real handlers arrive in Step 7.
- **Audit columns carry no real attribution yet.** The development scheme supplies a
  subject id, so writes are stamped `"dev-user"` rather than `"system"` — still not a real
  identity until Step 12.
- Architecture tests use `Assembly.GetReferencedAssemblies()`, which the compiler trims to
  references compiled code actually uses.
- `DocumentAcl` / document-level permissions are not modelled — Step 9.
- Integration tests require Docker. Without it they fail rather than skip, which is
  deliberate: a silently skipped test suite is worse than a failing one.
- The IDE's Roslynator analyzer reports RCS1194 and RCS1170 on domain types. Both are
  **deliberately not followed** — a sealed domain exception does not want the three
  standard constructors, and private setters are required for EF materialization.
  `dotnet build` is clean; these are editor-only suggestions.
