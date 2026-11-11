# The Learning Path — 14 Steps

The [README](../README.md) is a 12-week *calendar*. This is the *execution order*: what we
build, in what sequence, and how we know a step is finished. One step at a time, each one
ending in something that builds and runs.

The rule behind the ordering: **nothing is built before the thing it depends on is
trustworthy.** We do not build the UI before the API contract is stable, and we do not
tune RAG before we can measure it.

---

## Phase A — Foundation (Steps 1–4)

### Step 1 — Solution skeleton and the architecture decision

Create the solution, 7 production projects, 4 test projects, the dependency wiring, and
the build infrastructure (`global.json`, `Directory.Build.props`,
`Directory.Packages.props`).
**Exit criteria:** `dotnet build` and `dotnet test` succeed on a clean clone.
**Concepts:** modular monolith, Clean Architecture, the dependency rule, Central Package
Management.
**Write-up:** [steps/step-01-solution-skeleton.md](steps/step-01-solution-skeleton.md)

### Step 2 — The domain model

`Document`, `DocumentChunk`, `Conversation`, `Message`, `PromptTemplate`, `AIProvider`,
`User`. Invariants enforced in constructors, value objects for the things that are not
just strings, an audit base class.
**Exit criteria:** domain unit tests cover every invariant; the project references nothing.
**Concepts:** DDD tactical patterns, entity vs value object, aggregate roots, rich vs
anemic models, guard clauses.

### Step 3 — Persistence: EF Core + SQL Server in Docker

`DbContext`, entity configurations, the first migration, SQL Server running in a
container, a unique index on `(DocumentId, ChunkIndex)`.
**Exit criteria:** the migration applies to a clean database; integration tests pass
against the container.
**Concepts:** Repository and Unit of Work, EF Core change tracking, migrations,
Testcontainers.

### Step 4 — Application layer and the API surface

Use cases as one folder per feature, the Result pattern, FluentValidation, minimal API
endpoint groups, health checks, ProblemDetails error handling.
**Exit criteria:** CRUD endpoints work end to end with validation and consistent errors.
**Concepts:** use cases, CQRS-lite, Options pattern, minimal API organization.

## Phase B — AI core (Steps 5–8)

### Step 5 — AI providers through Microsoft.Extensions.AI

`IChatClient` and `IEmbeddingGenerator` registered for OpenAI and Azure OpenAI, secrets
from User Secrets, and the resilience pipeline (timeout, retry, logging).
**Exit criteria:** one chat call and one embedding call succeed without any
provider-specific type leaking into `EKA.Application`.
**Concepts:** Adapter, Strategy, Decorator/middleware pipeline, Factory for provider
selection.

### Step 6 — Prompt templates and the chat API

Versioned prompt templates, system instructions, conversation history windowing, message
persistence with token metadata.
**Exit criteria:** a client creates a conversation, asks a question, and reads back
persisted messages.
**Concepts:** Builder for prompt assembly, context window budgeting.

### Step 7 — Document ingestion

Upload with validation and limits, a storage abstraction, text extraction with page and
section metadata, status transitions, idempotency, Hangfire hand-off to the worker.
**Exit criteria:** uploading a file creates a tracked document and extracts text
asynchronously.
**Concepts:** Chain of Responsibility for the pipeline, idempotency keys, background jobs.

### Step 8 — Chunking and embeddings

Configurable chunk size and overlap, metadata preservation (document, index, page,
tenant, ACL), batched embedding generation with bounded concurrency, vectors into Qdrant,
safe re-indexing.
**Exit criteria:** an ingested document yields ordered chunks and matching vectors;
re-ingesting the same document is safe.
**Concepts:** Strategy for chunking algorithms, batching and backpressure.

## Phase C — Retrieval quality and trust (Steps 9–12)

### Step 9 — Vector retrieval with ACL filtering

Qdrant collection setup, question embedding, top-K search, metadata filters applied
**before** content is returned, score thresholds, optional hybrid search.
**Exit criteria:** queries return relevant chunks and *never* content outside the
caller's access scope.
**Concepts:** Specification pattern for filters, ANN search, cosine distance, hybrid
retrieval.

### Step 10 — RAG answer generation with citations

Bounded context assembly, instructions to refuse when evidence is insufficient, citations
down to page/section/chunk, diagnostics metadata.
**Exit criteria:** answers carry verifiable citations; unanswerable questions are refused
rather than fabricated.
**Concepts:** grounding, prompt injection defence, context stuffing limits.

### Step 11 — Evaluation

A representative question set with expected sources, hit-rate at K, groundedness and
citation correctness scoring, repeatable comparison runs.
**Exit criteria:** a baseline report exists and regressions fail the build.
**Concepts:** offline evaluation, LLM-as-judge and its limits, regression gates.

### Step 12 — Security and enterprise controls

Identity provider integration with issuer/audience/scope validation, document-level
authorization tests, upload scanning and limits, rate limiting, cost controls, secret and
log hygiene, threat model.
**Exit criteria:** security tests prove unauthorized content cannot be retrieved.
**Concepts:** OWASP Top 10 for LLM Applications, defence in depth, least privilege.

## Phase D — Delivery (Steps 13–14)

### Step 13 — Angular 21 client

App structure, typed API client, sign-in, conversation list, chat with streaming, citation
rendering, upload progress and ingestion status, accessibility.
**Exit criteria:** a user uploads a document, waits for indexing, asks questions, and
inspects citations.
**Concepts:** standalone components, signals, SSE/streaming consumption.

### Step 14 — Deployment and release readiness

Full Docker Compose (API, worker, SQL Server, Qdrant, Redis), readiness/liveness probes,
Serilog + OpenTelemetry, environment configuration, CI pipeline, operational docs, load
and failure-recovery tests.
**Exit criteria:** a reproducible deployment and a completed release checklist.
**Concepts:** the twelve-factor app, observability's three pillars, graceful degradation.

---

## Deliberately out of scope for the MVP

Recorded here so the temptation is resisted on purpose rather than by accident:

- Splitting into microservices.
- Agents or autonomous tool-calling workflows.
- A second vector database, or complex hybrid search before the baseline is measured.
- Reranking, GraphRAG, or multi-agent orchestration before evaluation shows a need.
- Treating frontend visibility as authorization.
