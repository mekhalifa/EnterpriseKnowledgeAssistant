# PROGRESS

Single source of truth for project state. Updated at the end of every working session.

**Last updated:** 2026-11-18
**Current step:** Learning path finished (14 of 14); post-MVP work under way (post-01 done)
**Overall:** 14 of 14 steps done
**Branch:** `post-mvp-resilience` (branched from `step-14-deployment`; none merged to `main`)

---

## ▶ NEXT ACTION

The 14 steps are done. What follows is post-MVP work, in priority order. Pick it with the owner;
none of it is a learning-path step.

1. **Tier-2 evaluation (≈ 5 cents, when the key has credit).** The three-vote judge calibration
   has not yet passed against the real model (step-11 §5.4), and it is release-checklist item
   A1.8: `$env:EKA_EVAL_ANSWERS="1"; dotnet test tests/EKA.Evaluation --filter Category=Evaluation`.
2. **Merge strategy.** Fifteen stacked branches now (this one included), none merged to `main`.
   Decide: merge the stack into `main` as is (history kept), or squash per step.
3. **Re-drill A4.6 on the Compose stack** (optional, minutes): stop `eka-sqlserver`, expect reads
   `503 database.unavailable` and readiness `503` within 5 s. Proved by integration tests only.
4. **Before anyone but the owner uses it:** [release checklist](operations/release-checklist.md)
   Part B (TLS, a real identity provider, no password grant, least-privilege SQL, Qdrant key,
   secret store, data-processor approval, durable telemetry, backups). Each item needs its own
   decision; several need an ADR.

Reference: [steps/step-14](steps/step-14-deployment.md), [operations/release-checklist](operations/release-checklist.md),
[adr/0034](adr/0034-single-host-compose-deployment.md).

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

#### Step 10 — Grounded answers with citations ✅

- **Exit criterion met twice:** in-process (upload → real worker → ask → cited answer whose
  citation is the uploaded document; refusal without a model call; restricted text absent from
  a non-member's prompt; forged delimiters defused) and on the real stack with **gpt-4o-mini +
  real embeddings**: cited answers for covered questions, the model's own refusal for a
  related-but-uncovered question (`uncited`), `no_evidence` with no model call for off-topic and
  restricted questions, and a planted "reply PWNED" instruction **ignored**.
- Decided with the owner ([ADR-0029](adr/0029-grounded-answers-refusal-and-citations.md)):
  two-layer refusal; numbered `[n]` markers parsed, validated and renumbered by the app; the raw
  question for retrieval.
- `AskQuestionHandler`: retrieve (same `ChunkRetriever`, caller's `AccessScope`) → sources
  before history within `Chat:MaxContextTokens` (3,000) → refuse or ask → extract → persist the
  turn atomically. Response: `grounding` (`cited`/`uncited`/`no_evidence`), sources
  retrieved/provided, citations on every message.
- `SourceRenderer` (delimited `<source>` blocks, tag forgery defused) and `CitationMarkers`
  (extract/renumber; strip markers from history).
- Domain: `Citation.Number` (stored: row order is not promised), marker-ordered citations,
  `Message.NoEvidenceRefusal`.
- Migration `GroundedAnswers`: `Number` backfilled per message, no default; prompt template
  **v2** seeded active, v1 kept inactive.
- **611 tests** (+3 domain, +24 application, +6 integration).

**Found and fixed, all recorded in [step-10](steps/step-10-grounded-answers.md):**

1. **The test suite was about to make billed calls:** the API test factory replaced the chat
   model but not the embedding service, and every question now embeds. It now always replaces
   both.
2. The generated migration defaulted `Number` to 0 against a domain rule of ≥ 1 — rewritten as
   nullable → backfill → NOT NULL, as in Step 9.
3. Four Step 6 tests assumed an ungrounded world; they now seed evidence, and "no documents"
   has its own no-model-call test.

#### Step 11 — Evaluation ✅

- **Exit criterion met:** baselines checked in ([retrieval](evaluation/retrieval.md),
  [answers](evaluation/answers.md)) and the gate proved: raising the score floor to 0.75 by
  configuration alone failed the ordinary `dotnet test` on four metrics.
- Decided with the owner ([ADR-0030](adr/0030-two-tier-evaluation-with-baselines.md)): two
  tiers; LLM judge + exact checks; small fixed corpus; hard rules + tolerance.
- `tests/EKA.Evaluation`: 5-policy corpus (2 restricted, 1 with a planted instruction), 30
  questions in six kinds, the real handlers over in-memory stores, real embeddings recorded in
  `Cache/embeddings.json`.
- **Tier 1** (every build, free, tolerance 0): hit@1 0.947, hit@3/@5 1.0, MRR 0.974, off-topic
  refused 1.0. **Floor 0.6 confirmed by data** (off-topic ≤ 0.563, correct top hits ≥ 0.717).
- **Tier 2** (`EKA_EVAL_ANSWERS=1`, ≈ 5 cents, tolerance 0.10): answer rate, citation correctness,
  refusal correctness, groundedness all 1.0 under judge v2 (gpt-4o); no canary leaked, no
  injection obeyed.
- Production: `Ai:Temperature` (0), `Ai:MaxOutputTokens` (1,000), `AiErrors.QuotaExceeded` (402),
  `Message.PromptTemplateVersion` (migration `AnswerPromptVersion`), `AddTextExtraction()`.
- **632 tests** (630 on every run; +1 domain, +2 application, +5 AI, +13 evaluation of which 2 billed).

**Found and fixed, all recorded in [step-11](steps/step-11-evaluation.md):**

1. **A false perfect score:** every judge call failed, and the mean of nothing was reported as
   groundedness 1.000. Empty now scores 0; an ungraded answer is a hard violation.
2. **No output cap:** every request reserved 16k tokens of credit, so a low budget refused cheap
   calls (402), and answers had no ceiling. `Ai:MaxOutputTokens`.
3. **Running out of credit would have failed documents permanently** (402 → `ai.rejected` →
   permanent in indexing). Now `ai.quota_exceeded`, transient; validated live when the budget ran out.
4. **The judge passed the over-reach it exists to catch** (prompt v1). Calibration test added;
   prompt v2 grades it PARTIAL; single votes still flip ~1 in 5, so calibration takes 3 votes.
5. Cache inputs differed between tiers (history questions); a recording run compared before saving.

#### Step 12 — Security and enterprise controls ✅

- **Exit criterion met twice:** 34 `SecurityTests` over the real token path (every content route
  × other tenant / same tenant no group / other tenant same group / admin not in group, with a
  canary and a positive control; 6 kinds of invalid token reach no handler; scope and tenant
  required; retired headers ignored), and the real stack with **Keycloak 26.6.4 tokens**, real
  embeddings and gpt-4o-mini (step-12 §6).
- Decided with the owner: generic OIDC + Keycloak locally ([ADR-0031](adr/0031-oidc-jwt-and-document-ownership.md),
  supersedes ADR-0014 — the development scheme is **deleted**); owner + tenant admin manage,
  admins do not read; per-user rate limits **and** per-tenant monthly budgets; hardened parsing
  behind a scanning port ([ADR-0032](adr/0032-rate-limits-budgets-and-upload-hardening.md)).
- JWT validation: signature (JWKS), issuer, audience, lifetime (1 min skew), scope `eka.access`,
  `tid`; claim names as issued; startup refuses no authority/audience or HTTP metadata outside
  Development. Tests sign real tokens through the production path.
- Documents: `OwnerSubject` (backfilled from `CreatedBy`); read / see / manage separated;
  403 for readers who may not manage, 404 for callers who cannot see.
- Rate limits (ask/search/upload, per tenant+user, 429 + Retry-After); budgets (2M tokens/month
  default, per-tenant overrides, 0 suspends; checked before provider calls; chat, question and
  ingestion embeddings recorded in an atomic `MERGE … HOLDLOCK` ledger); `GET /api/usage` (admins).
- Uploads: measured DOCX decompression cap (zip bombs), PDF page cap, extracted-text cap;
  `IMalwareScanner` before any parser (no-op adapter that warns).
- Log hygiene: proved no question, answer or document text in logs down to Debug;
  `Microsoft.Extensions.AI` pinned to Information.
- Threat model: [docs/security/threat-model.md](security/threat-model.md) (OWASP LLM Top 10).
- Migrations `DocumentOwnership`, `TenantTokenUsage`. Keycloak in compose with a checked-in realm.
- **687 tests** (+6 domain, +13 application, +36 integration of which 34 are `SecurityTests` cases).

**Found and fixed, all recorded in [step-12](steps/step-12-security.md):**

1. **A tenant suspension that bound to nothing:** the configuration binder silently drops
   `Dictionary<Guid, long>` entries — `Budgets:Tenants:{guid}=0` had no effect. String keys,
   validated as GUIDs at startup.
2. A test named "anonymous is rejected" had asserted 200 since Step 4 (development scheme); its
   intended assertion is now true.
3. The security suite's canary was not unique across tenants; "no handler ran" ignored seeding
   calls. Both fixed in the tests.
4. The ledger was designed off the request `DbContext` before parallel indexing could break it.

### Session 5 — 2026-11-17

#### Step 13 — Angular 21 client ✅

- **Exit criterion met on the real stack** by Playwright through a real browser: Keycloak sign-in
  (code + PKCE) → upload → polling to Ready → streamed, cited answer → marker focuses its
  citation → follow-up → reload keeps both → delete; and a `group:finance` document never
  reaches Carol's answer or Sources panel.
- Decided with the owner ([ADR-0033](adr/0033-angular-client-pkce-sse-generated-contract.md)):
  `angular-auth-oidc-client`, SSE streaming, Angular Material, `src/EKA.Web` in this repo.
- Backend: `IChatCompletionService.StreamAsync` (same `Result`, deltas as a side channel);
  `POST …/messages/stream` with a lazily started SSE writer (pre-stream failures keep their
  status); CORS from `Cors:AllowedOrigins`; `CanManage` on document responses;
  `JsonNumberHandling.Strict`; `IOException` → `ai.unavailable`; realm logout redirect.
- Client: zoneless, signals, Material 3; types generated by openapi-typescript from the
  checked-in `openapi/eka-api.json` (snapshot test on the server, `check:api` on the client);
  answers rendered as text and marker buttons only; live-region announcements once per outcome.
- **704 .NET tests** (+3 AI, +4 application, +12 integration incl. 7 streaming, 4 CORS and the
  OpenAPI snapshot), **27 client unit tests**, **2 e2e tests**.

**Found and fixed, all recorded in [step-13](steps/step-13-angular-client.md):**

1. The local database was still at Step 3's schema; six migrations applied cleanly.
2. Two chat routes would have destroyed the page mid-stream; one route with `?c=`.
3. A signal effect tracked `current()` through a called method and aborted its own stream.
4. Template whitespace would have padded every run of answer text; pinned by a test.
5. Every generated integer was `number | string`; fixed at the API (strict numbers).
6. Two e2e assertions were wrong (locator; `no_evidence` vs "no leak"), not the code.

**Observed, not resolved:** one 30 s `TenantTokenUsage` timeout during concurrent first-run
indexing (not reproduced: 10 concurrent MERGEs < 100 ms); API cold start ~20 s. Both to Step 14.

#### Step 14 — Deployment and release readiness ✅

- **Exit criterion met.** One command brings up the whole system, migrated, warm, observable,
  with two API replicas behind a CSP edge:
  `docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml up -d --build --wait`.
  The [release checklist](operations/release-checklist.md) is complete, with evidence for every
  gate, smoke check and drill, and lists 12 blocking items for any exposure beyond loopback.
- Decided with the owner, each on the recommended option: Compose on one host, CSP-hardened
  SPA, ClamAV ([ADR-0034](adr/0034-single-host-compose-deployment.md)). Also recorded:
  OpenTelemetry without Serilog, because Serilog replaces the log filters that keep document
  text out of logs ([ADR-0035](adr/0035-opentelemetry-without-serilog.md)); Redis-shared rate
  limits with a per-process fallback ([ADR-0036](adr/0036-shared-rate-limits-with-local-fallback.md)).
- Built: three .NET image targets plus web; migration bundle as the release step; readiness
  (SQL, Qdrant, warm-up, Redis as Degraded) and liveness; startup warm-up (Step 13's 20 s first
  request gone); nginx edge (CSP, no SSE buffering, same origin, runtime `/config.json`);
  JSON logs + OTLP traces and metrics; ClamAV scanner; exhausted retries end in `Failed`;
  narrow plain-HTTP metadata rule for the container network (tested); lock files; CI workflow;
  k6 load test.
- **Drills on the deployed stack:**
  - Reads: 0 % errors at 40 req/s, p95 24 ms.
  - Shared limiter: 10 of 40 searches admitted across two replicas.
  - Each dependency stopped in turn, all recovered without restarts:
    - an API replica under load: 0 failed reads;
    - Redis: Degraded, limits per instance;
    - the worker: upload queued, then processed;
    - SQL Server: not ready, still live.
  - EICAR rejected by ClamAV before parsing.
  - Streamed, cited answer through nginx.
  - 8 concurrent uploads from one tenant: all completed, no retries. Step 13's ledger timeout
    did not reproduce.
- **726 .NET tests** (+1 AI: the connect-timeout rule; Step 14's earlier additions counted in),
  **31 client unit tests**.

**Found and fixed by running the release, all recorded in [step-14 §6](steps/step-14-deployment.md):**

1. Six lock files carried a `win-x64` RID from a RID-specific restore; locked restore (CI, images)
   failed with NU1004. Regenerated.
2. The format gate would have failed CI (import order, an unused using).
3. The web tier crash-looped (`.envsh` copied without the execute bit, so the CSP variable was
   never set) while `up --wait` said Healthy, because `web` had no healthcheck. Both fixed.
4. The load test read an administrators-only endpoint: 21 % "errors" that were correct 403s.
5. An unreachable provider held users 94 s (OS connect timeout under a 60 s attempt timeout).
   `ConnectTimeout` = 10 s, tested against the real handler chain.
6. Readiness probes flooded the API's logs with `SELECT 1`; EF commands at Warning outside
   Development.

### Session 6 — 2026-11-18 (post-MVP)

#### Post-01 — Honest signals during a database outage ✅

- Chosen with the owner: item 3 of the post-MVP list (step-14 §6.7).
- **Measured first** (Windows and a Linux container): the delay is SqlClient's 15 s connect
  timeout, not EF retries. A cancelled token stops a hung connect. A refused connection is
  `SqlException` 1225 on Windows but **0 on Linux**, and `IsTransient` is false everywhere.
- `SqlServerFailures.IsUnavailable` (Infrastructure): structural rule. A `SqlException` wrapping
  `SocketException`/`Win32Exception` counts, as do `RetryLimitExceededException` and `IsTransient`.
  A failed login and a failing query stay 500.
- `DatabaseUnavailableExceptionHandler` → `503 database.unavailable`. The client says
  "temporarily unavailable", no longer "the AI service".
- `sql-server` readiness check bounded at `ReadinessCheckTimeout` (5 s), shared with Qdrant. The
  probe answers in 5 s against a hung connect that used to take 30 s.
- Decided: [ADR-0037](adr/0037-database-unavailable-is-503-bounded-readiness.md).
- **735 .NET tests run green** (+6 classifier against real failed connections, +2 end-to-end 503,
  +1 time-bound guard; one readiness test now asserts time), **32 client tests**. All four guarding
  tests were shown to fail with the fixes removed. Write-up: [post-01](steps/post-01-database-outage-signals.md).

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
| 10 | RAG answer generation with citations | ✅ Complete — 611 tests |
| 11 | Evaluation | ✅ Complete — 632 tests |
| 12 | Security and enterprise controls | ✅ Complete — 687 tests |
| 13 | Angular 21 client | ✅ Complete — 704 .NET + 27 client + 2 e2e |
| 14 | Deployment and release readiness | ✅ Complete — 726 .NET + 31 client; deployment drills in the release checklist |

---

## Decisions still to make

| When | Decision | Options |
|---|---|---|
| Before real documents | **Data processor approval** | OpenRouter sees prompts and retrieved text ([ADR-0015](adr/0015-openai-compatible-provider.md)). Fine for learning data; decide before anything sensitive is ingested |
| Later | Hybrid search | Dense-only; the evaluation shows no exact-term misses on the current set ([ADR-0028](adr/0028-retrieval-contract.md)). Add identifier-heavy questions before deciding |
| When budget allows | Three judge votes in the full answer run | Single votes flip toward leniency ~1 in 5 on borderline answers; re-baseline when switching ([ADR-0030](adr/0030-two-tier-evaluation-with-baselines.md)) |
| Before relying on tier 2 | Harder evaluation questions | Every tier-2 metric is 1.0: no headroom. Add multi-step, numeric and conflicting-source questions |
| With an admin report | Grants table vs JSON column | JSON column suffices until an admin report needs "everything group X can read" |
| With a real corpus | Chunk size and algorithm | 500/75 section-aware is unchallenged by the synthetic set (sections are short). Compare semantic or parent/child on long real documents; the evaluation can now measure it ([ADR-0024](adr/0024-section-aware-chunking-deterministic-point-ids.md)) |
| Later | Follow-up retrieval | One pronoun follow-up ranks 2nd (q29). Add more follow-ups to the set before paying for LLM query rewriting ([ADR-0029](adr/0029-grounded-answers-refusal-and-citations.md)) |
| If data becomes sensitive | BFF vs CSP-hardened SPA | CSP-hardened SPA chosen for Step 14 (ADR-0034); a BFF removes tokens from the browser entirely (ADR-0033) |
| When indexing waits grow | Push instead of polling | The documents page polls every 3 s while anything is in progress; push needs a worker → API channel |
| Before exposure (checklist B2, B3) | Production identity configuration | Entra ID app registration (audience, `eka.access` scope, `groups` + `roles` claims) or managed Keycloak; disable the password grant ([ADR-0031](adr/0031-oidc-jwt-and-document-ownership.md)) |
| Deferred from Step 12 | EF global query filters | *Defence in depth* on top of explicit tenant parameters (ADR-0009). Not done: every path is already tenant-scoped and covered by `SecurityTests`; revisit if a raw `DbContext` query is ever added |
| Later | `AIProvider` rows vs `Ai` configuration | The seeded row records what a prompt was written for; configuration chooses what answers. Reconcile or drop the table |
| With operations tooling (checklist B11) | Hangfire dashboard | Not wired: mapping it resolves job storage at API startup. Behind authorization, or not exposed |
| When leaving one host | Second storage adapter | Compose keeps local disk on a shared volume (ADR-0034); Azure Blob or S3 with Container Apps or Kubernetes ([ADR-0021](adr/0021-file-storage-port-local-disk.md)) |
| When telemetry must persist (checklist B8) | Telemetry backend | Collector + Grafana/Tempo/Loki vs a vendor vs Azure Monitor; the Aspire dashboard is memory only (ADR-0035) |

---

## Risks being tracked

| Risk | Why it matters | Mitigation |
|---|---|---|
| ACL leak through retrieval | The worst failure mode — one user reading another's documents | Tenant + access filter inside the vector query and inside SQL, independently written; SQL re-check decides (Step 9); `SecurityTests` over the real token path with canaries (Step 12) |
| Identity misconfiguration | A wrong authority, HTTP metadata, or a self-editable tenant attribute undoes everything | Startup refuses missing authority/audience and HTTP metadata outside Development; `tid` admin-only in the realm; [threat model](security/threat-model.md) |
| Embedding dimension change | Forces a full re-index | Model recorded per chunk; the adapter refuses vectors of another size (Step 8); searches filter on `embedding_model`, so a same-size model swap cannot mix spaces (Step 9) |
| Cross-tenant vector query | Qdrant isolation is a payload filter, not a boundary | A search filter without a tenant is refused before I/O; every hit re-checked in SQL; proved with identical documents in two tenants (Step 9) |
| Access-payload drift | The Qdrant copy of access keys can lag SQL | Fails closed (SQL re-check); "dropped by the SQL re-check" logged per search; reconciliation job if it stays above zero |
| Prompt injection via document content | Uploaded documents are untrusted input | Document text only in the user turn, inside `<source>` delimiters it cannot forge (tested); v2 instruction treats sources as data; a planted instruction was ignored by gpt-4o-mini on the real stack (Step 10). Model compliance is not guaranteed — add injection cases to Step 11's set |
| Cited but unsupported answers | A correct citation can sit on an over-reaching claim | Measured as groundedness by a calibrated judge (Step 11); temperature 0 removed the observed case; single-vote judge can miss a borderline one |
| Evaluation spend | Billed evaluation and smoke runs draw on a personal key's weekly limit | Tier 2 is opt-in (`EKA_EVAL_ANSWERS=1`); output capped; this step exhausted the limit twice — fund a separate evaluation key before CI runs tier 2 |
| Provider cost runaway | Embedding large corpora gets expensive fast | One retry layer (Step 5); output cap (Step 11); per-user rate limits and per-tenant monthly budgets checked before provider calls (Step 12). Budgets are soft (concurrent overshoot) |
| Third-party data path | OpenRouter sits between us and the model vendor | Learning data only until the decision above is made; switching to OpenAI direct or Azure is configuration (ADR-0015) |
| Scope creep into agents/GraphRAG | Burns the 12 weeks before the basics work | The out-of-scope list in the learning path |
| Supply chain via NuGet | A malicious package executes at build time | Source mapping (ADR-0006); NuGet audit as errors + transitive pinning (Step 7, caught NU1903); `packages.lock.json` restored in locked mode in CI and images (Step 14). Never commit lock files after a RID-specific restore (step-14 §6.1) |
| Untrusted uploads | Parsers are attack surface; files may lie about their type | Type from bytes (Step 7); measured zip-bomb, page and text caps; scanning before parsing (Step 12); ClamAV in the deployment, EICAR rejected end to end (Step 14). Local `dotnet run` still uses the no-op scanner |
| Exposure before Part B | Every shortcut of the single-host deployment is safe only on loopback | Ports bound to 127.0.0.1; the [release checklist](operations/release-checklist.md) Part B lists the 12 blocking items |

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

- **Local development needs Keycloak running** (`docker compose … up -d keycloak`, about a minute
  on first start) and a token per request (`deploy/keycloak/get-token.ps1`). The local realm
  enables the password grant for scripts; a deployed realm must not ([ADR-0031](adr/0031-oidc-jwt-and-document-ownership.md)).
- **The local realm's test users share a committed password** (`local-dev-only`). Acceptable
  only because Keycloak is bound to loopback and holds no real identities.
- **Tokens stay valid until they expire** (5 minutes in the local realm) after a user is
  disabled; no introspection or revocation check.
- **Groups overage** (Entra ID, more than 200 groups) gives the caller no groups: fails closed.
- **Administrators see the titles of documents they cannot read** (to manage them).
- **Budgets are soft and per month.** Concurrent requests can overshoot a budget by what they
  cost together.
- **Rate limits loosen while Redis is down:** each API instance then allows the full rate
  (by design, ADR-0036), and readiness reports Degraded. Without `ConnectionStrings:Redis`
  (local `dotnet run`) limits are always per process.
- **Local `dotnet run` has no malware scanner.** The no-op adapter logs a warning once per
  worker start; the Compose deployment uses ClamAV (`MalwareScanning:Scanner=ClamAv`).
- **Budgets are configuration, not data:** changing one is a configuration change and restart.
- **The deployment is one host, plain HTTP, development-mode Keycloak, `sa` for SQL.**
  Acceptable only on loopback; the [release checklist](operations/release-checklist.md)
  Part B lists everything that blocks exposure.
- **The first request of a database outage waits for the connect timeout** (up to 15 s) before
  its 503; SqlClient's pool then fails new opens fast for about 5 s. A circuit breaker would
  remove the wait (ADR-0037).
- **"Unavailable" includes a mistyped database hostname** (any OS network error is classified
  as unreachable), so that misconfiguration reads as 503. Readiness fails too, so it stays visible.
- **Every `docker compose` command over the app file needs `EKA_AI_API_KEY` set,** even `ps`
  and `logs` (runbook).
- **Telemetry lives in memory** (Aspire dashboard): gone when the container restarts.
- **No backups** of SQL Server, Qdrant or the file volume.
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
- **File storage is single-machine** (local disk). API and worker must share a disk or volume
  (`eka-files` in the deployment).
- **Hangfire polls SQL Server once per second** per worker process.
- **First-contact provisioning races itself.** Two concurrent *first* requests by one person
  both miss the user lookup; the unique index rejects the second as a 500. Safe, not
  invisible; a retry succeeds ([ADR-0017](adr/0017-just-in-time-user-provisioning.md)).
- **Concurrent asks in one conversation are not serialized.** Two simultaneous questions both
  load the thread, both append, both save — interleaving Q1 Q2 A1 A2. No concurrency token
  on `Conversation` yet; a `rowversion` would turn the second into a 409.
- **Profile claims are copied at first contact and never refreshed** (email, display name).
- **Archiving has no endpoint**, so the domain's "archived conversations accept no messages"
  rule is unreachable over HTTP. It throws `DomainException` (500), not
  `InvalidStateTransitionException` (409) — fix before an archive endpoint ships.
- **A citation proves provenance, not support.** Groundedness is measured offline by the
  evaluation's judge (Step 11), not checked per answer in production.
- **The evaluation set is small, synthetic and Markdown-only,** with every tier-2 metric at 1.0:
  it detects breakage, not fine differences. No PDF or DOCX in the corpus.
- **The three-vote judge calibration has not yet passed against the real model** (the budget ran
  out while measuring the single-vote flip rate). First item when the key has credit.
- **Tier 1 searches exactly** (in memory), while production uses Qdrant's approximate HNSW. They
  agree at this size.
- **The embedding cache must be re-recorded** (`EKA_EVAL_RECORD=1`, needs a key) after changing
  the corpus, the questions, the chunker, the embedding input format or the model.
- **A model's own refusal is recognised only by citing nothing** (`grounding = uncited`); a
  hedged cited answer and a correct uncited one are not told apart.
- **Follow-ups without meaning of their own retrieve poorly** ("can I carry those over?"):
  retrieval embeds the latest question only ([ADR-0029](adr/0029-grounded-answers-refusal-and-citations.md)).
- **An out-of-range 1–3 digit bracketed number is removed from an answer** (`[7]` when five
  sources were given), even if it was a footnote rather than a citation.
- **The no-evidence answer is configuration, not a versioned template field,** so its wording
  is not evaluated alongside the prompt.
- **The test factory always fakes the embedding model.** Real embeddings in the API are covered
  only by the real-stack runs and EKA.AI's smoke tests.
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
  are unaffected. The database got its 503 in post-01; Qdrant would follow the same pattern
  (`RpcException` with `StatusCode.Unavailable`).
- **`ListAsync` uses offset paging**, so a deep page scans and discards, and an insert
  between two requests can shift a row across a page boundary. Correct for a page-numbered
  UI; revisit if a tenant's document count reaches the thousands.
- **`.editorconfig` does not gate the local build.** `EnforceCodeStyleInBuild` is off on purpose
  (see step-04 §9); CI runs `dotnet format --verify-no-changes` (Step 14). Run it before pushing:
  the build passing locally does not mean the format gate will.
- **`LoggingDomainEventDispatcher` invokes no handlers.** It logs each event so the wiring
  is visibly working and visibly incomplete. Step 7 did not need one — ingestion is driven by
  the job, not by events — so the first subscriber waits for a real consumer (notifications,
  Step 12/13) rather than being invented.
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
  Deliberately absent, still (Step 14): provider failures show as `ai.unavailable` problems
  and in the traces, not as instances leaving rotation.
- **Access tokens live in the browser's sessionStorage** (Step 13): readable by any script that
  runs in the page. The deployment's CSP (`script-src 'self'`, no inline script) is the
  mitigation (ADR-0034); `ng serve` in development serves no CSP.
- **The e2e tests need the real stack and make billed calls**; they are not part of `npm test`.
  A test that fails before its clean-up leaves its documents in tenant A (delete as `root`).
- **The client's dev tooling has moderate npm advisories** (`glob` via the Angular CLI's
  `pacote`); production dependencies audit clean. The fix is Angular CLI 22.
- **The streamed text is not the stored text.** Deltas are raw model output; the stored answer
  is renumbered and may differ (invented markers removed). The client replaces one with the
  other on `completed`.
- **A proxy that buffers responses turns streaming into one chunk.** Nothing detects it.
- **The API's first authenticated request after start takes ~20 s** (observed once, Step 13).
- **Unexplained: one 30 s `TenantTokenUsage` command timeout** during concurrent indexing on a
  freshly migrated database; the budget under-counted 99 tokens. Not reproduced.
- **Smoke tests make real, billed calls** whenever a key is in User Secrets — fractions of
  a cent per run, but `dotnet test` is no longer free on a configured machine. Exclude with
  `--filter Category!=Smoke`.
- Integration tests require Docker. Without it they fail rather than skip, which is
  deliberate: a silently skipped test suite is worse than a failing one.
- The IDE's Roslynator analyzer reports RCS1194 and RCS1170 on domain types. Both are
  **deliberately not followed** — a sealed domain exception does not want the three
  standard constructors, and private setters are required for EF materialization.
  `dotnet build` is clean; these are editor-only suggestions.
