# PROGRESS

Single source of truth for project state. Updated at the end of every working session.

**Last updated:** 2026-11-11
**Current step:** Step 1 complete → Step 2 next
**Overall:** Phase A, 1 of 14 steps complete

---

## ▶ NEXT ACTION

**Step 2 — the domain model.** Build `EKA.Domain` for real:

1. `EntityAudit<TId>` base class (id, created/modified audit fields, domain event list).
2. Entities with invariants enforced in constructors and intention-named state changes:
   `Document`, `DocumentChunk`, `Conversation`, `Message`, `PromptTemplate`,
   `AIProvider`, `User`.
3. Value objects: `DocumentTitle`, `ChunkIndex`, `FileMetadata`, `TokenCount`,
   `SimilarityScore`, `TenantId`.
4. `IngestionStatus` state machine: `Pending → Extracting → Chunking → Embedding →
   Completed | Failed`, with illegal transitions rejected.
5. `DomainException` and guard clauses — **validate before assigning**, which is the
   bug the original README flagged as P0.
6. Domain events: `DocumentIngestedEvent` and friends.
7. Unit tests in `EKA.Domain.Tests` covering every invariant.

**Decision needed before writing tests:** assertion library — see the table below.

Reference: [concepts/ddd.md](concepts/ddd.md), [concepts/solid.md](concepts/solid.md).

---

## Done

### Session 1 — 2026-11-11

#### Setup

- Verified toolchain: .NET SDK 10.0.403 (plus 8.0.416), Docker 29.1.5, git 2.48.1,
  Node 24.15.0. Nothing missing.
- `git init` on branch `main`; added the official .NET `.gitignore`.
- Ran `/doctor`: installation healthy, single npm-global install, settings parse clean,
  Claude Code 2.1.322 is current. No changes applied.

#### Documentation

- `CLAUDE.md` with the scope constraint, architecture rules, and conventions.
- Documentation library: learning path, this tracker, 6 ADRs, 6 concept references,
  Step 1 write-up.

#### Step 1 — solution skeleton ✅

- `global.json` pinning SDK 10.0.403 (`rollForward: latestFeature`).
- `Directory.Build.props`: `net10.0`, nullable, implicit usings,
  `TreatWarningsAsErrors`, deterministic builds — inherited by all 11 projects.
- `Directory.Packages.props`: Central Package Management; no `Version=` in any `.csproj`.
- `nuget.config`: package source mapping, nuget.org only — see the dependency-confusion
  finding below.
- Solution + 7 production projects (`src/`) + 4 test projects (`tests/`).
- 18 project references wired; the *absent* ones verified by test.
- Assembly marker types in each layer for test and DI assembly scanning.
- **19 tests passing**, all architectural guards — each test project enforces its own
  layer boundary.

**Decisions recorded:** modular monolith (ADR-0001), Clean Architecture with 7 projects
(ADR-0002), Minimal APIs (ADR-0003), separate ingestion worker (ADR-0004), Central
Package Management (ADR-0005), NuGet source mapping (ADR-0006).

**Unplanned finding — dependency confusion.** Enabling CPM failed the build with NU1507
because this machine has three NuGet feeds (nuget.org, an internal SBC feed, VS offline)
and none were mapped. That ambiguity is the dependency-confusion attack vector: a public
package published under an internal package's ID can be pulled at restore and executed at
build time. Fixed properly with package source mapping rather than suppressing the
warning. Full reasoning in [ADR-0006](adr/0006-package-source-mapping.md).

---

## Step checklist

| # | Step | Status |
|---|---|---|
| 1 | Solution skeleton + architecture decision | ✅ Complete — builds clean, 19 tests green |
| 2 | Domain model | ⬜ Next |
| 3 | Persistence: EF Core + SQL Server in Docker | ⬜ Not started |
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

Listed early so they are not made by accident under time pressure.

| When | Decision | Options |
|---|---|---|
| **Step 2** | **Assertion library** | Plain xUnit `Assert` (no dependency) / **Shouldly** (MIT, recommended) / AwesomeAssertions (MIT fork of FluentAssertions 7). **Not** FluentAssertions v8+ — it requires a paid commercial licence |
| Step 3 | Repository abstraction depth | `DbContext` directly in use cases / repository per aggregate / generic repository |
| Step 5 | Primary AI provider | Azure OpenAI (enterprise, private networking) vs OpenAI direct (simpler) |
| Step 5 | Embedding model + dimension | Fixes the Qdrant collection shape — changing it later means re-indexing everything |
| Step 7 | Background job engine | Hangfire (SQL-backed, has a dashboard) vs a queue (Azure Service Bus / RabbitMQ) |
| Step 7 | File storage | Local disk / Azure Blob / S3-compatible (MinIO in Docker) |
| Step 9 | Hybrid search | Dense-only first; add BM25/keyword only if evaluation in Step 11 shows a gap |
| Step 12 | Identity provider | Microsoft Entra ID vs Keycloak vs ASP.NET Core Identity |

---

## Risks being tracked

| Risk | Why it matters | Mitigation |
|---|---|---|
| ACL leak through retrieval | The single worst failure mode — one user reading another's documents | Filter in the vector query, not after; dedicated security tests in Step 12 |
| Embedding dimension change | Forces a full re-index of every document | Decide in Step 5, record in an ADR, store the model name on every chunk |
| Prompt injection via document content | Uploaded documents are untrusted input to the model | Treat retrieved text as data, not instructions; tested in Step 10 |
| Provider cost runaway | Embedding large corpora and long contexts get expensive fast | Token budgets, caching, rate limits in Step 12 |
| Scope creep into agents/GraphRAG | Burns the 12 weeks before the basics are trustworthy | The out-of-scope list in the learning path |
| Supply chain via NuGet | A malicious package executes at build time with developer credentials | Source mapping (ADR-0006); consider `packages.lock.json` at Step 14 |

---

## Known limitations of the current code

Honest notes so these are not mistaken for finished work.

- The architecture tests use `Assembly.GetReferencedAssemblies()`, which the compiler
  trims to references that compiled code actually uses. With nearly empty projects they
  pass trivially; they gain real teeth as code lands from Step 2 onward.
- `EKA.Api` still contains the template's weather-forecast sample endpoint. It is
  replaced in Step 4.
- No `.editorconfig` yet — added in Step 2 alongside the first real code, so the style
  rules are written against actual code rather than guessed.
