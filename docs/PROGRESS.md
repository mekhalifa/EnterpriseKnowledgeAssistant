# PROGRESS

Single source of truth for project state. Updated at the end of every working session.

**Last updated:** 2026-11-15
**Current step:** Step 6 complete → Step 7 next
**Overall:** Phase B, 6 of 14 steps done
**Branch:** `step-06-chat-api` (branched from `step-05-ai-providers`; none merged to `main`)

---

## ▶ NEXT ACTION

**Step 7 — Document ingestion.**
Exit criterion: *uploading a file creates a tracked document and extracts text
asynchronously.*

**Decisions needed first — all three shape the code:**

| Decision | Options |
|---|---|
| Background job engine | Hangfire on SQL Server (dashboard, retries, no new infrastructure) vs a queue (Azure Storage Queue / RabbitMQ) consumed by `EKA.Ingestion.Worker` |
| File storage | Local disk behind an `IFileStorage` port (simplest) vs Azure Blob vs S3-compatible MinIO in Docker |
| Text extraction | PdfPig for PDF + OpenXML for DOCX (pure .NET) vs Azure AI Document Intelligence (layout-aware, paid) |

Then:

1. `POST /api/documents/upload` (multipart): size and type limits enforced **before** reading
   the whole body, content hash computed while streaming, stored through `IFileStorage`.
2. Unique index `(TenantId, ContentHash)` — closes the registration race left in Step 4;
   a collision becomes "already registered", not a 500.
3. Hand-off to the worker; the worker drives `BeginExtraction` → `CompleteExtraction` /
   `Fail` and stores extracted text with page and section metadata.
4. Domain-event handlers: `LoggingDomainEventDispatcher` gets its first real subscriber.
5. Chain of Responsibility for the extraction pipeline (detect type → extract → normalize).
6. **Load → change → save tests** for every new aggregate operation (step-06 §5.1 taught why).

Reference: [concepts/design-patterns.md](concepts/design-patterns.md) (Chain of
Responsibility), [adr/0004](adr/0004-separate-ingestion-worker.md).

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

### Session 3 — 2026-11-15

#### Step 5 — AI providers through Microsoft.Extensions.AI ✅

- **Exit criterion met against a real provider:** one chat call (`openai/gpt-4o-mini`) and
  one embedding call (1536 dims) through OpenRouter, via the application's own ports.
- `IChatCompletionService` / `IEmbeddingService` declared in `EKA.Application` in domain
  vocabulary, returning `Result<T>`; eight `AiErrors` codes, all → 502.
- `EKA.AI`: options with `ValidateOnStart()`, a Factory over `AIProviderKind`,
  `Microsoft.Extensions.AI` middleware (`UseLogging`), and the standard HTTP resilience
  handler (total timeout → jittered exponential retry → circuit breaker → attempt timeout).
- **SDK retries disabled** so there is one retry layer, not 4 × 4 = 16 requests per outage.
- Runtime guards: vector length = configured dimensions, vector count = input count, empty
  answer = failure.
- `AIProviderKind.OpenAICompatible` added; the domain requires an endpoint for it.
- `ApplicationBoundaryTests` now forbids OpenAI, Azure.AI, System.ClientModel,
  Microsoft.Extensions.AI, Microsoft.Extensions.Http and Polly.
- **362 tests** (35 in `EKA.AI.Tests`: resilience through the real SDK with a scripted
  network, no key; 2 smoke tests that skip without one).
- Decided: [ADR-0015](adr/0015-openai-compatible-provider.md) OpenAI-compatible adapter,
  OpenRouter first, `text-embedding-3-small` @ 1536;
  [ADR-0016](adr/0016-application-owns-ai-ports.md) the application owns its AI ports.

**Two things went wrong, both recorded in [step-05](steps/step-05-ai-providers.md):**

1. Every configuration error was reported **twice** in one `AggregateException`: the
   resilience package's own `ValidateOnStart` ran my `Configure` callback, which resolved
   the validated `AiOptions` again. Fixed by binding the resilience section directly.
2. A comment of mine claimed the resilience package validates lazily; the tests showed it
   validates at startup. Corrected.

#### Step 6 — Prompt templates and the chat API ✅

- **Exit criterion met twice:** in the integration suite with a stand-in model, and against
  the real API + SQL Server + OpenRouter — three questions, windowed history, the seeded
  prompt declining to invent a leave policy.
- Four slices: `StartConversation`, `AskQuestion`, `GetConversation`, `ListConversations`;
  four endpoints under `/api/conversations`, scoped to tenant **and** user in the query.
- `UserProvisioner`: just-in-time local users; reads never provision.
- `PromptBuilder`: one System turn, history trimmed oldest-first to a token budget, orphaned
  answers dropped, single-pass placeholder substitution.
- A turn is atomic: model called before the aggregate is touched; provider failure → 502
  (`ai.*`) with nothing persisted. First real use of `ErrorType.Provider`.
- Migration `ConversationsAndDefaultPrompt`: `UX_Users_TenantId_ExternalId`, seeded
  provider row and template `default` v1.
- **394 tests** (+21 application, +9 conversation end-to-end, +2 persistence regression).
- Decided: [ADR-0017](adr/0017-just-in-time-user-provisioning.md) JIT provisioning;
  [ADR-0018](adr/0018-conservative-token-estimation.md) token estimate;
  [ADR-0019](adr/0019-prompt-templates-seeded-by-migration.md) prompts as migrations.

**Found and fixed, all recorded in [step-06](steps/step-06-chat-api.md):**

1. **A cross-tenant identity bug, found by reading:** a global `ExternalId` index and an
   untenanted lookup would have resolved tenant B's `dev-user` to tenant A's row.
2. **UPDATE instead of INSERT:** EF Core treats Guid keys as store-generated, so a child
   added to a *loaded* aggregate was tracked as Modified → `DbUpdateConcurrencyException`.
   Fixed model-wide with `ValueGenerated.Never`; **Step 8's chunks would have hit it too**,
   and a regression test now proves it for both.
3. A test passed while its request was a 500 — it asserted a side effect, not the status.
4. A stale incremental build (restored file with an old timestamp) reached the running API;
   caught only by running the real stack.
5. `localhost` resolves to `::1` here; the SQL container listens on `127.0.0.1` only.

---

## Step checklist

| # | Step | Status |
|---|---|---|
| 1 | Solution skeleton + architecture decision | ✅ Complete — 19 tests |
| 2 | Domain model | ✅ Complete — 191 tests |
| 3 | Persistence: EF Core + SQL Server in Docker | ✅ Complete — 233 tests |
| 4 | Application layer + API surface | ✅ Complete — 322 tests |
| 5 | AI providers via Microsoft.Extensions.AI | ✅ Complete — 362 tests |
| 6 | Prompt templates + chat API | ✅ Complete — 394 tests |
| 7 | Document ingestion | ⬜ Next |
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
| Step 7 | **Background job engine** — blocking | Hangfire (SQL-backed, dashboard) vs a queue |
| Step 7 | **File storage** — blocking | Local disk / Azure Blob / S3-compatible (MinIO) |
| Step 7 | **Text extraction** — blocking | PdfPig + OpenXML (pure .NET) vs Azure AI Document Intelligence |
| Before real documents | **Data processor approval** | OpenRouter sees prompts and retrieved text ([ADR-0015](adr/0015-openai-compatible-provider.md)). Fine for learning data; decide before anything sensitive is ingested |
| Step 7 | Unique index on `(TenantId, ContentHash)` | Closes the registration race left open in Step 4. Natural to add with the real upload path |
| Step 9 | Hybrid search | Dense-only first; add BM25 only if Step 11 shows a gap |
| Step 9 | ACL model | `DocumentAcl` deliberately not built — designed with access-filtered retrieval |
| Step 11 | Persist the prompt template version on each answer | Reported in `AskQuestionResponse` today, not stored; evaluation will need it ([ADR-0019](adr/0019-prompt-templates-seeded-by-migration.md)) |
| Step 12 | Identity provider | Entra ID vs Keycloak vs ASP.NET Core Identity |
| Step 12 | EF global query filters | As *defence in depth* on top of explicit tenant parameters, not instead of them — see ADR-0009 |
| Step 12 | `AIProvider` rows vs `Ai` configuration | The seeded row records what a prompt was written for; configuration chooses what answers. Reconcile or drop the table |

---

## Risks being tracked

| Risk | Why it matters | Mitigation |
|---|---|---|
| ACL leak through retrieval | The worst failure mode — one user reading another's documents | Tenant filter inside every repository method (ADR-0009); **proved across HTTP in Step 4** by two-tenant end-to-end tests; vector-query filtering in Step 9; security tests in Step 12 |
| Embedding dimension change | Forces a full re-index | Recorded per provider and per chunk, so stale vectors are identifiable |
| Prompt injection via document content | Uploaded documents are untrusted input | `MessageRole` is a trust boundary; tested in Step 10 |
| Provider cost runaway | Embedding large corpora gets expensive fast | One retry layer, client errors never retried (Step 5, tested); token budgets, caching, rate limits in Step 12 |
| Third-party data path | OpenRouter sits between us and the model vendor | Learning data only until the decision above is made; switching to OpenAI direct or Azure is configuration (ADR-0015) |
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
| One local user per identity per tenant | `UX_Users_TenantId_ExternalId` | ✅ Created and tested (Step 6; replaced the global index) |

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
- **First-contact provisioning races itself.** Two concurrent *first* requests by one person
  both miss the user lookup; the unique index rejects the second as a 500. Safe, not
  invisible; a retry succeeds ([ADR-0017](adr/0017-just-in-time-user-provisioning.md)).
- **Concurrent asks in one conversation are not serialized.** Two simultaneous questions both
  load the thread, both append, both save — interleaving Q1 Q2 A1 A2. No concurrency token
  on `Conversation` yet; a `rowversion` would turn the second into a 409.
- **Profile claims are copied at first contact and never refreshed** (email, display name).
- **The prompt template version is reported, not persisted** on the answer.
- **Archiving has no endpoint**, so the domain's "archived conversations accept no messages"
  rule is unreachable over HTTP. It throws `DomainException` (500), not
  `InvalidStateTransitionException` (409) — fix before an archive endpoint ships.
- **No answer citations yet.** Every answer is `IsUngrounded`; v1's instruction says so to
  the model. Retrieval is Steps 9–10.
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
- **`AIProvider` rows are not read yet.** The active provider comes from the `Ai`
  configuration section; the `AIProviders` table and `IAIProviderRepository` are unused.
  Reconciling the two (or dropping the table) is a decision for Step 12's admin surface.
- **One provider at a time.** The Factory switches on `Ai:Kind`; two simultaneously live
  providers would need keyed services. `AzureOpenAI` is rejected at startup until it has an
  adapter.
- **The circuit breaker is configured but not covered by a test.** Its default minimum
  throughput (100 requests in the sampling window) makes a unit test either slow or a test
  of overridden settings rather than ours. Retry and timeout behaviour are tested.
- **No provider health check.** A readiness probe that calls the model would cost money on
  every probe and take instances out of rotation for an upstream outage they cannot fix.
  Deliberately absent; revisit with Step 14's monitoring.
- **No streaming.** `IChatCompletionService` returns the whole answer. Step 10 decides
  whether the UI needs token streaming, which would widen the port (ADR-0016).
- **Smoke tests make real, billed calls** whenever a key is in User Secrets — fractions of
  a cent per run, but `dotnet test` is no longer free on a configured machine. Exclude with
  `--filter Category!=Smoke`.
- Integration tests require Docker. Without it they fail rather than skip, which is
  deliberate: a silently skipped test suite is worse than a failing one.
- The IDE's Roslynator analyzer reports RCS1194 and RCS1170 on domain types. Both are
  **deliberately not followed** — a sealed domain exception does not want the three
  standard constructors, and private setters are required for EF materialization.
  `dotnet build` is clean; these are editor-only suggestions.
