# PROGRESS

Single source of truth for project state. Updated at the end of every working session.

**Last updated:** 2026-11-11
**Current step:** Step 2 complete → Step 3 next
**Overall:** Phase A, 2 of 14 steps complete
**Branch:** `step-02-domain-model` (not yet merged to `main`)

---

## ▶ NEXT ACTION

**Step 3 — persistence: EF Core + SQL Server in Docker.**

1. Add packages: `Microsoft.EntityFrameworkCore.SqlServer`, `.Design`,
   `Testcontainers.MsSql` — to `Directory.Packages.props`, versions nowhere else.
2. `EkaDbContext` in `EKA.Infrastructure` with `DbSet`s for the seven entities.
3. One `IEntityTypeConfiguration<T>` per aggregate, so **no EF attribute ever touches
   `EKA.Domain`** — the architecture tests will fail the build if one does.
4. Map the value objects: owned types or conversions for `DocumentTitle`, `TenantId`,
   `ChunkIndex`, `TokenCount`, `FileMetadata`, `PageRange`, `AuthorityScore`,
   `SimilarityScore`.
5. **The two relocated invariants from Step 2** — unique index on
   `(DocumentId, ChunkIndex)`, and filtered unique index on `(Name) WHERE IsActive = 1`
   for `PromptTemplate`.
6. `SaveChangesInterceptor` to fill `CreatedBy`/`ModifiedBy` from the ambient user, and to
   dispatch domain events **after** commit.
7. Private backing-field mapping for `_chunks`, `_messages`, `_roles`.
8. SQL Server in Docker: official image, EULA, strong password from env, named volume,
   health check, service DNS from the API. (The README flags the old compose file as P0.)
9. First migration; verify it applies to a **clean** database and creates every expected
   table, FK, and index.
10. Integration tests with Testcontainers.

**Decision needed first:** repository abstraction depth — see the table below.

Reference: [concepts/design-patterns.md](concepts/design-patterns.md#repository),
[concepts/clean-architecture.md](concepts/clean-architecture.md).

---

## Done

### Session 1 — 2026-11-11

#### Setup

- Verified toolchain: .NET SDK 10.0.403 (plus 8.0.416), Docker 29.1.5, git 2.48.1,
  Node 24.15.0. Nothing missing.
- `git init` on branch `main`; added the official .NET `.gitignore` and a `.gitattributes`
  that normalizes line endings.
- Ran `/doctor`: installation healthy, single npm-global install, settings parse clean,
  Claude Code 2.1.322 current. No changes applied.

#### Project skills (`.claude/skills/`)

- `next-step` — run the next learning step end to end: orient from PROGRESS.md, teach with
  the why/best/alternatives format, build, verify, write the step doc, update the tracker,
  commit.
- `new-adr` — create a numbered ADR from the template and update both index tables.
- `verify` — the quality gate, plus this repository's known failure modes (NU1507, NU1008,
  architecture test failures, SDK mismatch).

No `.claude/settings.json` — the useful contents would be permission allow-rules, and the
owner has chosen to approve actions individually.

#### Documentation

- `CLAUDE.md` with the scope constraint, architecture rules, and conventions.
- Documentation library: learning path, this tracker, 8 ADRs, 6 concept references, two
  step write-ups.

#### Step 1 — solution skeleton ✅

- `global.json` pinning SDK 10.0.403 (`rollForward: latestFeature`).
- `Directory.Build.props`: `net10.0`, nullable, implicit usings,
  `TreatWarningsAsErrors`, deterministic builds — inherited by all 11 projects.
- `Directory.Packages.props`: Central Package Management; no `Version=` in any `.csproj`.
- `nuget.config`: package source mapping, nuget.org only.
- Solution + 7 production projects (`src/`) + 4 test projects (`tests/`).
- 18 project references wired; the *absent* ones verified by test.
- 19 architecture tests — each test project guards its own layer boundary.

**Unplanned finding — dependency confusion.** Enabling CPM failed the build with NU1507
because this machine has three NuGet feeds (nuget.org, an internal SBC feed, VS offline)
and none were mapped. That ambiguity is the dependency-confusion attack vector: a public
package published under an internal package's ID can be pulled at restore and executed at
build time. Fixed with package source mapping rather than suppressing the warning. Full
reasoning in [ADR-0006](adr/0006-package-source-mapping.md).

#### Step 2 — domain model ✅

- **26 files, 1,640 lines** in `EKA.Domain`, still referencing nothing.
- `EntityAudit<TId>`: identity equality, audit fields, domain event collection. `Id` lives
  only here — the README's P1 shadowing bug is now covered by a test.
- 7 entities: `Document`, `DocumentChunk`, `Conversation`, `Message`, `PromptTemplate`,
  `AIProvider`, `User`. No public setters anywhere.
- 9 value objects: `TenantId`, `TokenCount`, `DocumentTitle`, `FileMetadata`, `ChunkIndex`,
  `PageRange`, `AuthorityScore`, `Citation`, `SimilarityScore`.
- `Document` lifecycle state machine with every illegal transition rejected; `Reset`
  discards chunks, which is what makes re-ingestion idempotent.
- 2 domain events, 2 exception types (`DomainException` → a bug;
  `InvalidStateTransitionException` → HTTP 409 later).
- **179 domain tests; 191 total; clean build with warnings-as-errors.**

**Decided:** Shouldly for assertions ([ADR-0007](adr/0007-assertion-library.md)) —
FluentAssertions v8+ needs a paid commercial licence. Domain never reads the clock
([ADR-0008](adr/0008-deterministic-time-in-domain.md)).

**Corrected assumption.** A test of mine failed and was right to:
`List<T>.AsReadOnly()` returns `ReadOnlyCollection<T>`, which **does** implement
`ICollection<T>` explicitly, with write members throwing. So `IReadOnlyCollection<T>` is a
design signal enforced at runtime, not a type-system impossibility — worth knowing before
treating it as a security boundary. Full account in
[step-02](steps/step-02-domain-model.md#9-what-went-wrong-and-what-it-taught).

---

## Step checklist

| # | Step | Status |
|---|---|---|
| 1 | Solution skeleton + architecture decision | ✅ Complete — 19 tests |
| 2 | Domain model | ✅ Complete — 191 tests |
| 3 | Persistence: EF Core + SQL Server in Docker | ⬜ Next |
| 4 | Application layer + API surface | ⬜ Not started |
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
| **Step 3** | Repository abstraction depth | `DbContext` directly in use cases (simplest) / **repository per aggregate root** (recommended — makes the ACL filter part of a method's identity rather than something to remember) / generic `IRepository<T>` (avoid) |
| Step 3 | Value object mapping style | EF owned types vs `ValueConverter` per type — affects query shape and index support |
| Step 5 | Primary AI provider | Azure OpenAI (enterprise, private networking) vs OpenAI direct (simpler) |
| Step 5 | Embedding model + dimension | Fixes the Qdrant collection shape; changing it later means re-indexing everything. `AIProvider.EmbeddingDimensions` already records it per provider |
| Step 7 | Background job engine | Hangfire (SQL-backed, dashboard) vs a queue (Azure Service Bus / RabbitMQ) |
| Step 7 | File storage | Local disk / Azure Blob / S3-compatible (MinIO in Docker) |
| Step 9 | Hybrid search | Dense-only first; add BM25/keyword only if Step 11's evaluation shows a gap |
| Step 9 | ACL model | `DocumentAcl` entity is deliberately **not** built yet — designed alongside access-filtered retrieval |
| Step 12 | Identity provider | Microsoft Entra ID vs Keycloak vs ASP.NET Core Identity |

---

## Risks being tracked

| Risk | Why it matters | Mitigation |
|---|---|---|
| ACL leak through retrieval | The single worst failure mode — one user reading another's documents | Filter in the vector query, not after; dedicated security tests in Step 12 |
| Embedding dimension change | Forces a full re-index of every document | Recorded per provider and per chunk (`DocumentChunk.EmbeddingModel`) so stale vectors are identifiable |
| Prompt injection via document content | Uploaded documents are untrusted input to the model | `MessageRole` is a trust boundary — retrieved text is never system-role; tested in Step 10 |
| Provider cost runaway | Embedding large corpora and long contexts get expensive fast | Token budgets, caching, rate limits in Step 12 |
| Scope creep into agents/GraphRAG | Burns the 12 weeks before the basics are trustworthy | The out-of-scope list in the learning path |
| Supply chain via NuGet | A malicious package executes at build time with developer credentials | Source mapping (ADR-0006); consider `packages.lock.json` at Step 14 |

---

## Invariants enforced outside the aggregate

Relocated deliberately in Step 2 because an aggregate cannot see the state they span.
Tracked here so they are not lost.

| Invariant | New home | Status |
|---|---|---|
| Chunk indexes unique per document | Unique DB index `(DocumentId, ChunkIndex)` | ⬜ Step 3 |
| At most one active prompt template per name | Filtered unique index `(Name) WHERE IsActive = 1` | ⬜ Step 3 |
| `default(TenantId)` must never persist | `IsEmpty` check in every aggregate constructor + non-nullable column | ✅ Domain half done; column in Step 3 |

---

## Known limitations of the current code

- The architecture tests use `Assembly.GetReferencedAssemblies()`, which the compiler trims
  to references that compiled code actually uses. They gain full force as more code lands.
- `EKA.Api` still contains the template's weather-forecast sample endpoint. Replaced in
  Step 4.
- No `.editorconfig` yet. Now worth adding in Step 3, since there is real code to write
  rules against.
- `DocumentAcl` / document-level permissions are not modelled — Step 9.
- Nothing is persisted yet: the domain has no database, no repository, no migration.
- The IDE's Roslynator analyzer reports RCS1194 ("implement exception constructors") on
  `InvalidStateTransitionException` and RCS1170 ("use read-only auto-property") on entity
  properties. Both are **deliberately not followed**: a sealed domain exception does not
  want the three standard constructors, and the private setters are required for EF Core
  materialization. The `dotnet build` is clean; these are editor-only suggestions.
