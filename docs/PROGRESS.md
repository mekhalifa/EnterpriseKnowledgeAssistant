# PROGRESS

Single source of truth for project state. Updated at the end of every working session.

**Last updated:** 2026-11-16
**Current step:** Step 9 complete → Step 10 next
**Overall:** Phase C in progress, 9 of 14 steps done
**Branch:** `step-09-retrieval-acl` (branched from `step-08-chunking-embeddings`; none merged to `main`)

---

## ▶ NEXT ACTION

**Step 10 — RAG answer generation with citations.**
Exit criterion: *answers carry verifiable citations; unanswerable questions are refused
rather than fabricated.*

**Decisions needed first:**

| Decision | Options |
|---|---|
| Refusal mechanism | Short-circuit with no model call when retrieval is empty (cheap, deterministic) vs always ask the model with an instruction to refuse (handles "retrieved but irrelevant") — likely both |
| Citation format | Model emits markers (`[1]`) mapped back to retrieved chunks vs citing every retrieved chunk regardless of use |
| Prompt template v2 | New seeded version via migration (ADR-0019) with a context section and the trust boundary for document text |

Then:

1. `AskQuestionHandler` calls `ChunkRetriever` with the caller's `AccessScope` (the slice
   must now build one; today it takes `CallerIdentity`), before the model.
2. `PromptBuilder` gains a bounded context section: retrieved chunks within a token budget,
   document text clearly delimited as **untrusted data**, never instructions (`MessageRole`
   trust boundary; prompt-injection test).
3. Persist `Citation`s on the assistant message (document, chunk index, pages, section, score);
   `IsUngrounded` false only when at least one citation backs the answer.
4. Expose citations in `AskQuestionResponse` / `GetConversation`.
5. Tests: answer with citations end to end; empty retrieval → refusal with no model call; a
   restricted document never reaches a non-member's prompt (assert on the recorded prompt);
   injected instructions in a document do not change behaviour.

Reference: [adr/0028](adr/0028-retrieval-contract.md) (empty = success),
[adr/0019](adr/0019-prompt-templates-seeded-by-migration.md),
[steps/step-09](steps/step-09-retrieval-acl.md) §7.

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

#### Step 7 — Document ingestion ✅

- **Exit criterion met twice:** in-process (real API + real worker composition, joined only
  by Hangfire) and on the real stack — Kestrel + a separate worker process: Markdown
  extracted to 3 heading-pathed sections, a corrupt PDF ended Failed with the parser's
  reason, a duplicate upload returned 200, a 27 MB body got 413 from Kestrel.
- `POST /api/documents` is now multipart upload, **replacing** Step 4's metadata-only JSON
  registration (a client-asserted hash was a client-asserted identity).
- Type detected from bytes; extension must agree; stored content type and hash are the
  server's. File names reduced to their last segment.
- Three writes ordered so every crash point is recoverable; best-effort enqueue plus a
  recurring stalled-document sweep; at-least-once + idempotent (`AwaitsExtraction`,
  `StartOrResumeExtraction`).
- Extraction: PdfPig (per page), Open XML (per heading), text/Markdown (per ATX heading);
  Chain of Responsibility; one normalization; permanent vs transient failures.
- `UX_Documents_TenantId_ContentHash` (created by hand — EF cannot model it) closes Step
  4's registration race; `DuplicateKeyException` lets the loser answer 200. Proved by eight
  concurrent uploads of the same bytes → one document, one file.
- **429 tests** (+11 domain, +11 application net, +13 integration).
- Decided: [ADR-0020](adr/0020-hangfire-best-effort-handoff.md) Hangfire + sweep;
  [ADR-0021](adr/0021-file-storage-port-local-disk.md) storage port;
  [ADR-0022](adr/0022-pure-dotnet-text-extraction.md) extraction;
  [ADR-0023](adr/0023-extracted-text-artifact.md) text artifact.

**Found and fixed, all recorded in [step-07](steps/step-07-document-ingestion.md):**

1. **NU1903:** Hangfire's `Newtonsoft.Json >= 11.0.1` floor resolved to a vulnerable version.
   Fixed with CPM transitive pinning (13.0.4). Caught only because warnings are errors.
2. **The real worker process could not start** (`ValidateOnBuild`: `AskQuestionHandler` without
   a chat client), while the in-process test passed with validation disabled. Handlers are
   now registered per host; the test host validates like production — proved by
   reintroducing the bug.
3. `[FromForm]` record bound to null, so the validation filter refused every upload.
4. The shell collapsed backslashes in scripted edits; one file gained literal control
   characters. Backslash-bearing text now goes through the editor tools only.
5. Test fixtures shared one content hash; persistence tests now assert `DuplicateKeyException`
   and the index name.

### Session 4 — 2026-11-16

#### Step 8 — Chunking and embeddings ✅

- **Exit criterion met twice:** in-process (real API + worker composition + real Qdrant,
  embedding faked) and on the real stack with **real OpenRouter embeddings**: a 6 KB handbook
  went Pending → Completed in 5 s, 7 chunks (≈470-token windows starting at clause
  boundaries), 7 points at 1536 dims, cosine; `DELETE` emptied Qdrant and storage.
- `SectionAwareChunker` behind `IChunkingStrategy` (Strategy): sections are hard
  boundaries, small same-heading neighbours merge (page ranges), long sections window at
  paragraph → sentence → word with whole-unit overlap; never over the token target.
- Embedding input = title + heading trail + chunk; stored chunk stays plain text.
- `IndexDocumentHandler`: delete old vectors → chunks → waves of bounded batches (64 × 2 in
  flight) → record per wave → `CompleteIngestion`. Refusals fail the document; everything
  else retries with progress kept.
- Domain: `AwaitsChunking`, `AwaitsEmbedding`, `ChunksAwaitingEmbedding`,
  `RecordEmbeddings`; `CompleteIngestion` now refuses unless every loaded chunk has a vector.
- `QdrantVectorStore`: derived UUIDv8 point ids from (document, index); one collection,
  `tenant_id` (is_tenant) + `document_id` indexes; no text in payload; collection created on
  first write and refused on a dimension mismatch.
- One job `IngestAsync` for all stages; `ExtractTextAsync` kept as a forwarder for stored
  jobs. Reset now enqueues. Delete reaches all three stores (best-effort clean-up after the
  row commits).
- Qdrant in compose (v1.20.0, loopback-only) and in the test fixture. No migration needed.
- **477 tests** (+13 domain, +29 application, +6 integration).
- Decided: [ADR-0024](adr/0024-section-aware-chunking-deterministic-point-ids.md) chunking +
  point ids; [ADR-0025](adr/0025-qdrant-official-client-single-collection.md) Qdrant layout;
  [ADR-0026](adr/0026-one-ingestion-job-resumable-stages.md) one resumable job.

**Found and fixed, all recorded in [step-08](steps/step-08-chunking-embeddings.md):**

1. **A computed property became a relationship.** `ChunksAwaitingEmbedding`
   (`IReadOnlyList<DocumentChunk>`, getter only) was mapped by convention as a second
   navigation; `MigrateAsync` refused with `PendingModelChangesWarning`. Ignored explicitly.
2. **The chunk budget leaked separators.** Per-piece token sums ignored the spaces and breaks
   between pieces; a "50-token" piece was 63. Pieces are now counted with their separator.
3. A stale "handlers arrive in Step 7" log message.

#### Step 9 — Vector retrieval with access control ✅

- **Exit criterion met twice:** in-process (real API + worker + SQL + Qdrant, embedding faked:
  identical documents in two tenants, group-restricted documents, stale payloads, mid-ingestion
  and orphan vectors) and on the real stack with **real embeddings**: relevant sections ranked
  first (0.74–0.80), a `group:finance` document invisible to non-members (`0 vector hits` —
  filtered inside Qdrant), tenant B's identical document resolved to its own id, an off-topic
  question returned nothing.
- **Document ACL** (decided with the owner: now, not Step 12): `AccessPrincipal` (`tenant`,
  `user:<sub>`, `group:<id>`), `DocumentAccess` (non-empty, ≤50, `tenant` alone),
  `AccessScope` from token claims. Access = same tenant ∧ keys intersect — in the domain, in
  SQL (JSON primitive collection) and in Qdrant (`access` keyword array, indexed).
- **Every document use case takes an `AccessScope`**: get/list/rename/reset/delete hide
  unreadable documents (404, list count excludes them); upload of a restricted document's
  bytes → 409 naming nothing. `PUT /api/documents/{id}/access` (payload first, then SQL).
- **Retrieval:** `ChunkFilter` Specification (tenant ∧ access ∧ embedding model, `And` only,
  tenant structural, exhaustive Qdrant translation); `ChunkRetriever` embeds → searches with
  the filter inside → re-checks in SQL (`IChunkReader`: tenant, current access, Completed,
  exact keys) → ranks deterministically. Over-fetch ×2; K 5 (max 20); floor 0.6 normalized.
  `POST /api/search`. Qdrant Query API.
- `groups` claim; `X-EKA-Groups` header in the development scheme.
- Migration `DocumentAccess`: existing documents backfilled to `["tenant"]`, no default
  constraint left.
- **578 tests** (+28 domain, +50 application, +23 integration incl. 10 real-Qdrant search
  tests and 11 end-to-end retrieval tests).
- Decided: [ADR-0027](adr/0027-document-acl-canonical-keys.md) access model;
  [ADR-0028](adr/0028-retrieval-contract.md) retrieval contract (dense-only, configured K and
  floor, empty = success).

**Found and fixed, all recorded in [step-09](steps/step-09-retrieval-acl.md):**

1. **The generated migration would have locked every existing document**: EF defaulted the
   new non-nullable column to `"[]"` — readable by nobody, and unloadable by the domain.
   Rewritten as nullable → backfill → NOT NULL; a test migrates an old-schema row forward.
2. An "unauthenticated" test was authenticated: the development scheme supplies a default
   identity when headers are absent. 401 is tested with a malformed identity, as before.
3. Single-array `InlineData` does not compile; a zero score floor no longer sends a threshold.

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
| 7 | Document ingestion | ✅ Complete — 429 tests |
| 8 | Chunking + embeddings | ✅ Complete — 477 tests |
| 9 | Vector retrieval with ACL filtering | ✅ Complete — 578 tests |
| 10 | RAG answer generation with citations | ⬜ Next |
| 11 | Evaluation | ⬜ Not started |
| 12 | Security and enterprise controls | ⬜ Not started |
| 13 | Angular 21 client | ⬜ Not started |
| 14 | Deployment and release readiness | ⬜ Not started |

---

## Decisions still to make

| When | Decision | Options |
|---|---|---|
| Before real documents | **Data processor approval** | OpenRouter sees prompts and retrieved text ([ADR-0015](adr/0015-openai-compatible-provider.md)). Fine for learning data; decide before anything sensitive is ingested |
| Step 11 | Hybrid search | Dense-only decided for now ([ADR-0028](adr/0028-retrieval-contract.md)); add BM25 + RRF only if the evaluation shows misses on exact terms |
| Step 11 | Top-K and score floor | 5 / 0.6 normalized are starting values; real-stack scores of related passages were 0.62–0.80 |
| Step 12 | Who may change a document's access | Today anyone who can read it ([ADR-0027](adr/0027-document-acl-canonical-keys.md)); roles (owner / admin) with the identity provider |
| Step 12 | Grants table vs JSON column | JSON column suffices until an admin report needs "everything group X can read" |
| Step 11 | Chunk size and algorithm | 500/75 section-aware is a starting point; compare against semantic or parent/child with evidence ([ADR-0024](adr/0024-section-aware-chunking-deterministic-point-ids.md)) |
| Step 11 | Persist the prompt template version on each answer | Reported in `AskQuestionResponse` today, not stored; evaluation will need it ([ADR-0019](adr/0019-prompt-templates-seeded-by-migration.md)) |
| Step 12 | Identity provider | Entra ID vs Keycloak vs ASP.NET Core Identity |
| Step 12 | EF global query filters | As *defence in depth* on top of explicit tenant parameters, not instead of them — see ADR-0009 |
| Step 12 | `AIProvider` rows vs `Ai` configuration | The seeded row records what a prompt was written for; configuration chooses what answers. Reconcile or drop the table |
| Step 14 | Hangfire dashboard | Not wired: mapping it resolves job storage at API startup. Belongs with operations/monitoring, behind authorization |
| Step 14 | Second storage adapter | Azure Blob vs S3, once the deployment target is chosen ([ADR-0021](adr/0021-file-storage-port-local-disk.md)) |

---

## Risks being tracked

| Risk | Why it matters | Mitigation |
|---|---|---|
| ACL leak through retrieval | The worst failure mode — one user reading another's documents | Tenant + access filter inside the vector query and inside SQL, independently written; SQL re-check decides (Step 9, proved end to end incl. stale payloads); security tests in Step 12 |
| Embedding dimension change | Forces a full re-index | Model recorded per chunk; the adapter refuses vectors of another size (Step 8); searches filter on `embedding_model`, so a same-size model swap cannot mix spaces (Step 9) |
| Cross-tenant vector query | Qdrant isolation is a payload filter, not a boundary | A search filter without a tenant is refused before I/O; every hit re-checked in SQL; proved with identical documents in two tenants (Step 9) |
| Access-payload drift | The Qdrant copy of access keys can lag SQL | Fails closed (SQL re-check); "dropped by the SQL re-check" logged per search; reconciliation job if it stays above zero |
| Prompt injection via document content | Uploaded documents are untrusted input | `MessageRole` is a trust boundary; tested in Step 10 |
| Provider cost runaway | Embedding large corpora gets expensive fast | One retry layer, client errors never retried (Step 5, tested); token budgets, caching, rate limits in Step 12 |
| Third-party data path | OpenRouter sits between us and the model vendor | Learning data only until the decision above is made; switching to OpenAI direct or Azure is configuration (ADR-0015) |
| Scope creep into agents/GraphRAG | Burns the 12 weeks before the basics work | The out-of-scope list in the learning path |
| Supply chain via NuGet | A malicious package executes at build time | Source mapping (ADR-0006); NuGet audit as errors + transitive pinning (Step 7, caught NU1903); `packages.lock.json` at Step 14 |
| Untrusted uploads | Parsers are attack surface; files may lie about their type | Type from bytes, extension must agree, size limits in two layers; parsing runs in the worker, not the API (Step 7) |

---

## Invariants enforced outside the aggregate

| Invariant | New home | Status |
|---|---|---|
| Chunk indexes unique per document | `UX_DocumentChunks_DocumentId_ChunkIndex` | ✅ Created and tested |
| At most one active prompt template per name | `UX_PromptTemplates_Name_Active`, filtered `[IsActive] = 1` | ✅ Created and tested |
| `default(TenantId)` must never persist | `IsEmpty` check in every aggregate constructor + non-nullable column | ✅ Both halves done |
| Same bytes registered once per tenant | `UX_Documents_TenantId_ContentHash` + lookup in `UploadDocumentHandler` | ✅ Both halves; race proved closed by 8 concurrent uploads (Step 7) |
| One local user per identity per tenant | `UX_Users_TenantId_ExternalId` | ✅ Created and tested (Step 6; replaced the global index) |
| One vector per chunk, however often written | Point id derived from (DocumentId, ChunkIndex) in `QdrantVectorStore` | ✅ Tested against real Qdrant (Step 8) |
| A document's access list is never missing | `Documents.Access` NOT NULL, **no default**; pre-Step-9 rows backfilled to `["tenant"]` | ✅ Migration tested from the old schema (Step 9) |

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
- **Exhausted retries leave a document in an intermediate status** (`Extracting`, `Chunking`
  or `Embedding`). After five transient failures the job sits in Hangfire's Failed list; the
  sweep only re-enqueues `Pending`. Needs a terminal handler that marks the document Failed,
  or a sweep over stalled intermediate statuses (ADR-0026).
- **Orphaned files and vectors accumulate after crashes** or a store outage during delete.
  Harmless to readers (no text in the payload, hits resolve against SQL), but no
  reconciliation job reclaims them yet.
- **The worker needs its own AI key.** `dotnet user-secrets --project src/EKA.Ingestion.Worker
  set "Ai:ApiKey" "<key>"`; without it the worker refuses to start (by design).
- **A failed document keeps its chunk rows** (and their partial embedded marks) until reset.
  Its vectors are deleted on failure, so nothing is searchable.
- **No embedding cache.** Re-ingesting a document re-embeds every chunk, even unchanged ones.
- **No Qdrant API key locally**; ports are loopback-only. Any shared deployment must set
  `VectorStore:ApiKey`.
- **No concurrency token on `Document`.** Two simultaneous deliveries both extract and both
  save (same result — correct, wasteful).
- **No OCR and no layout analysis.** Scanned PDFs fail clearly; multi-column pages may
  extract out of order ([ADR-0022](adr/0022-pure-dotnet-text-extraction.md)).
- **File storage is single-machine** (local disk). API and worker must share a disk or volume.
- **Hangfire polls SQL Server once per second** per worker process.
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
  the model. Retrieval exists (Step 9); the chat flow does not call it until Step 10.
- **Anyone who can read a document can change its access** — including restricting it so
  they lose access themselves. Roles arrive in Step 12 ([ADR-0027](adr/0027-document-acl-canonical-keys.md)).
- **An access change during ingestion can leave later batches with the old keys.** Narrowing
  is caught by the SQL re-check; widening leaves the document partly hidden until access is
  set again (the PUT is idempotent).
- **Vectors written before Step 9 have no `access` field** and are excluded from search (fails
  closed) until the document is re-ingested or its access is set. The `access` and
  `embedding_model` payload indexes are created on the next write by a freshly started worker.
- **Uploading the bytes of a document you cannot read returns 409**, which confirms the bytes
  exist in the tenant (names nothing). The cost of the per-tenant idempotency index.
- **Groups overage** (Entra sends a Graph pointer instead of >200 groups) yields no groups:
  less access, never more. Resolved with the identity provider in Step 12.
- **A Qdrant outage during search is a 500**, not a mapped 503; uploads and the rest of the API
  are unaffected.
- **`ListAsync` uses offset paging**, so a deep page scans and discards, and an insert
  between two requests can shift a row across a page boundary. Correct for a page-numbered
  UI; revisit if a tenant's document count reaches the thousands.
- **`.editorconfig` does not gate the build.** `EnforceCodeStyleInBuild` is off on purpose
  (see step-04 §9); `dotnet format --verify-no-changes` becomes a CI step in Step 14.
- **`LoggingDomainEventDispatcher` invokes no handlers.** It logs each event so the wiring
  is visibly working and visibly incomplete. Step 7 did not need one — ingestion is driven by
  the job, not by events — so the first subscriber waits for a real consumer (notifications,
  Step 12/13) rather than being invented.
- **Audit columns carry no real attribution yet.** The development scheme supplies a
  subject id, so writes are stamped `"dev-user"` rather than `"system"` — still not a real
  identity until Step 12.
- Architecture tests use `Assembly.GetReferencedAssemblies()`, which the compiler trims to
  references compiled code actually uses.
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
