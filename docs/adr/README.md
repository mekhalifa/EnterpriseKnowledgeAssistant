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
