# ADR-0001: Modular monolith instead of microservices

- **Status:** Accepted
- **Date:** 2026-11-11
- **Deciders:** project owner

## Context

EKA has components with genuinely different shapes: an interactive chat API, a
CPU-and-IO-heavy ingestion pipeline, a vector search path, and an access-control layer.
That naturally raises the question of whether they should be separate services.

Constraints that actually apply here:

- **One developer, 15 hours per week, 12 weeks.** Every hour spent on infrastructure is an
  hour not spent on retrieval quality.
- **No independent scaling requirement has been demonstrated.** There is no traffic
  measurement showing that chat and retrieval need to scale separately.
- **No second team.** The main non-technical driver of microservices — independent
  deployment by independent teams — is absent.
- The project is also a **learning vehicle**, so boundaries should be visible and
  explicit rather than implicit.

## Decision

Build a **modular monolith**: one deployable ASP.NET Core application, with internal
boundaries enforced by separate .NET projects and the compiler, plus **one** separate
process for ingestion (see [ADR-0004](0004-separate-ingestion-worker.md)).

Bounded contexts (Ingestion, Retrieval, Conversation, Identity & Access) are expressed as
folders and project boundaries, not as network boundaries.

## Consequences

### Positive

- One `F5` debugs the whole request path; a stack trace spans the entire operation.
- One database transaction can span a use case — no sagas, no eventual consistency, no
  compensating actions for a failed multi-step write.
- One deployment artifact, one configuration surface, one log stream.
- Refactoring across boundaries is a compiler-checked rename, not a coordinated release of
  two services with a versioned contract between them.
- Extraction remains possible later: because `EKA.Application` depends only on interfaces,
  a context can be lifted out when a real need appears.

### Negative / costs we accept

- **Boundaries can be violated by adding a project reference.** Discipline is required;
  the reference matrix in [clean-architecture.md](../concepts/clean-architecture.md) is the
  rule, and reviews must check it.
- The whole application scales as one unit, so a traffic spike on chat provisions capacity
  for everything.
- A memory leak or crash in one module takes down all of them.
- More projects than a single-project app, which is ceremony a 10-endpoint CRUD service
  would not need.

## Alternatives considered

**Single project with folders.** Fastest possible start, zero ceremony. Rejected because
nothing prevents `EKA.Domain` code from calling SQL directly, and in a project explicitly
intended to teach architecture, the boundary enforcement *is* part of the deliverable.
This would win for a prototype or a sub-2000-line tool.

**Microservices (ingestion, retrieval, chat, auth as separate services).** Rejected: it
front-loads service discovery, inter-service auth, distributed tracing, contract
versioning, and retry/idempotency across the network — weeks of work — in exchange for
independent scaling and deployment that nothing currently requires. This would win with
multiple teams owning different contexts, or a measured need to scale ingestion
independently by a large factor.

**Serverless functions per pipeline stage.** Rejected: cold starts hurt interactive chat
latency, long-running PDF extraction fits awkwardly in function timeouts, and local
development and debugging get substantially harder. This would win for spiky, infrequent
ingestion with no interactive component.

## Revisit when

- Ingestion load requires scaling to a degree that makes co-deployment wasteful, **and**
  the worker split in ADR-0004 is no longer sufficient.
- A second team takes ownership of a bounded context and is blocked by shared deployment.
- Retrieval latency requirements demand independent geographic or hardware placement
  (e.g. GPU hosts).
- The repository passes roughly 100k lines, where build and test times start to dominate
  the feedback loop.
