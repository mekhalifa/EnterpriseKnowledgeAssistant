# Architecture Decision Records

An ADR records **one decision**: what we chose, why, what it costs, and what we rejected.

## Why keep them

Six weeks from now, "why are there seven projects?" has an answer that is cheap to read
and expensive to reconstruct. More importantly, an ADR makes the *conditions* of a decision
explicit — so when those conditions change, you know the decision should be revisited
instead of defended out of habit.

An ADR is immutable once accepted. If we change our minds, we write a new ADR that
supersedes it and update the status of the old one. The history of what we believed is as
useful as the current belief.

## Status values

| Status | Meaning |
|---|---|
| Proposed | Under discussion, not yet acted on |
| Accepted | Decided and being implemented |
| Superseded by ADR-NNNN | Replaced; kept for the record |
| Deprecated | No longer relevant (the feature was dropped) |

## Index

| ADR | Title | Status | Date |
|---|---|---|---|
| [0001](0001-modular-monolith.md) | Modular monolith instead of microservices | Accepted | 2026-11-11 |
| [0002](0002-clean-architecture-layering.md) | Clean Architecture with seven projects | Accepted | 2026-11-11 |
| [0003](0003-minimal-apis.md) | Minimal APIs instead of MVC controllers | Accepted | 2026-11-11 |
| [0004](0004-separate-ingestion-worker.md) | Ingestion as a separate process | Accepted | 2026-11-11 |
| [0005](0005-central-package-management.md) | Central Package Management and a pinned SDK | Accepted | 2026-11-11 |
| [0006](0006-package-source-mapping.md) | Pin NuGet to nuget.org with package source mapping | Accepted | 2026-11-11 |
| [0007](0007-assertion-library.md) | Shouldly for test assertions | Accepted | 2026-11-11 |
| [0008](0008-deterministic-time-in-domain.md) | The domain never reads the clock | Accepted | 2026-11-11 |
| [0009](0009-repository-per-aggregate.md) | A repository per aggregate root, with tenant-scoped methods | Accepted | 2026-11-12 |
| [0010](0010-value-object-mapping.md) | Converters for single-value objects, owned types for composites | Accepted | 2026-11-12 |
| [0011](0011-result-pattern.md) | Result for expected failures, exceptions for broken rules | Accepted | 2026-11-12 |
| [0012](0012-vertical-slices-no-mediator.md) | Vertical slices with direct handler injection, no mediator | Accepted | 2026-11-12 |
| [0013](0013-validation-placement.md) | Shape validation at the edge, stateful rules in the handler | Accepted | 2026-11-12 |
| [0014](0014-development-authentication.md) | A development authentication scheme until an identity provider exists | Superseded by 0031 | 2026-11-12 |
| [0015](0015-openai-compatible-provider.md) | One OpenAI-compatible adapter, with OpenRouter as the first endpoint | Accepted | 2026-11-15 |
| [0016](0016-application-owns-ai-ports.md) | The application owns its AI ports; `IChatClient` stays in the adapter | Accepted | 2026-11-15 |
| [0017](0017-just-in-time-user-provisioning.md) | Just-in-time user provisioning, unique per tenant | Accepted | 2026-11-15 |
| [0018](0018-conservative-token-estimation.md) | A conservative token estimate for the prompt budget, exact counts where providers give them | Accepted | 2026-11-15 |
| [0019](0019-prompt-templates-seeded-by-migration.md) | Prompt template versions ship as migrations | Accepted | 2026-11-15 |
| [0020](0020-hangfire-best-effort-handoff.md) | Hangfire on SQL Server, with a best-effort hand-off and a stalled-document sweep | Accepted | 2026-11-15 |
| [0021](0021-file-storage-port-local-disk.md) | An `IFileStorage` port, with local disk as the first adapter | Accepted | 2026-11-15 |
| [0022](0022-pure-dotnet-text-extraction.md) | Pure .NET text extraction, with detection from content | Accepted | 2026-11-15 |
| [0023](0023-extracted-text-artifact.md) | Extracted text is a versioned JSON artifact in file storage | Accepted | 2026-11-15 |
| [0024](0024-section-aware-chunking-deterministic-point-ids.md) | Section-aware chunking, and vector ids derived from (document, chunk index) | Accepted | 2026-11-16 |
| [0025](0025-qdrant-official-client-single-collection.md) | Qdrant through its official client, one collection partitioned by tenant, no text in the payload | Accepted | 2026-11-16 |
| [0026](0026-one-ingestion-job-resumable-stages.md) | One ingestion job per document, with stages that resume from the document's state | Accepted | 2026-11-16 |
| [0027](0027-document-acl-canonical-keys.md) | Document access as canonical principal keys, filtered inside the vector query and re-checked in SQL | Accepted | 2026-11-16 |
| [0028](0028-retrieval-contract.md) | The retrieval contract: dense-only, configured top-K and floor, over-fetch, empty as success | Accepted | 2026-11-16 |
| [0029](0029-grounded-answers-refusal-and-citations.md) | Grounded answers: two-layer refusal, numbered citation markers, the raw question for retrieval | Accepted | 2026-11-16 |
| [0030](0030-two-tier-evaluation-with-baselines.md) | Two-tier evaluation: recorded embeddings gate every build, a calibrated LLM judge gates billed runs | Accepted | 2026-11-16 |
| [0031](0031-oidc-jwt-and-document-ownership.md) | OIDC access tokens from any issuer, Keycloak locally; document owners and tenant administrators | Accepted | 2026-11-16 |
| [0032](0032-rate-limits-budgets-and-upload-hardening.md) | Per-user rate limits, per-tenant monthly token budgets, hardened uploads | Accepted | 2026-11-16 |
| [0033](0033-angular-client-pkce-sse-generated-contract.md) | Angular client: OIDC code + PKCE, SSE answer streaming, Material, generated contract | Accepted | 2026-11-17 |
| [0034](0034-single-host-compose-deployment.md) | Single-host Compose deployment: migration job, readiness, CSP edge, ClamAV, locked restore | Accepted | 2026-11-17 |
| [0035](0035-opentelemetry-without-serilog.md) | OpenTelemetry for logs, traces and metrics, without Serilog | Accepted | 2026-11-17 |
| [0036](0036-shared-rate-limits-with-local-fallback.md) | Rate-limit buckets in Redis, per-process fallback when it is down | Accepted | 2026-11-17 |

## Template

```markdown
# ADR-NNNN: <title>

- **Status:** Proposed | Accepted | Superseded by ADR-NNNN
- **Date:** YYYY-MM-DD
- **Deciders:** <who>

## Context
What problem forced a decision? What constraints were in play?

## Decision
What we chose, stated plainly.

## Consequences
### Positive
### Negative / costs we accept

## Alternatives considered
Each option, and the condition under which it would have won.

## Revisit when
The observable trigger that should make us reopen this.
```
