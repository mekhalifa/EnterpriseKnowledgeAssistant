# EKA Documentation

Reference library for the Enterprise Knowledge Assistant. Everything explained during
development is written down here so it can be re-read instead of re-asked.

## Start here

| Document | What it answers |
|---|---|
| [PROGRESS.md](PROGRESS.md) | What is done, what I am doing next |
| [00-learning-path.md](00-learning-path.md) | The 14 steps from empty folder to deployed MVP |

## Steps — the teaching write-ups

Each step document records what we built, why, the best option, and the alternatives.

| Step | Document | Status |
|---|---|---|
| 1 | [step-01-solution-skeleton.md](steps/step-01-solution-skeleton.md) | ✅ Complete |
| 2 | [step-02-domain-model.md](steps/step-02-domain-model.md) | ✅ Complete |
| 3 | [step-03-persistence.md](steps/step-03-persistence.md) | ✅ Complete |
| 4 | [step-04-application-api.md](steps/step-04-application-api.md) | ✅ Complete |
| 5–14 | *created as we reach them* | — |

## ADRs — the decisions

Short records of each architectural decision, its consequences, and what we rejected.

| ADR | Decision | Status |
|---|---|---|
| [0001](adr/0001-modular-monolith.md) | Modular monolith, not microservices | Accepted |
| [0002](adr/0002-clean-architecture-layering.md) | Clean Architecture layering, 7 projects | Accepted |
| [0003](adr/0003-minimal-apis.md) | Minimal APIs, not MVC controllers | Accepted |
| [0004](adr/0004-separate-ingestion-worker.md) | Ingestion runs as a separate process | Accepted |
| [0005](adr/0005-central-package-management.md) | Central Package Management + pinned SDK | Accepted |
| [0006](adr/0006-package-source-mapping.md) | NuGet pinned to nuget.org with source mapping | Accepted |
| [0007](adr/0007-assertion-library.md) | Shouldly for test assertions | Accepted |
| [0008](adr/0008-deterministic-time-in-domain.md) | The domain never reads the clock | Accepted |
| [0009](adr/0009-repository-per-aggregate.md) | Repository per aggregate, tenant-scoped methods | Accepted |
| [0010](adr/0010-value-object-mapping.md) | Converters for single values, owned types for composites | Accepted |
| [0011](adr/0011-result-pattern.md) | Result for expected failures, exceptions for broken rules | Accepted |
| [0012](adr/0012-vertical-slices-no-mediator.md) | Vertical slices, direct handler injection, no mediator | Accepted |
| [0013](adr/0013-validation-placement.md) | Shape validation at the edge, stateful rules in the handler | Accepted |
| [0014](adr/0014-development-authentication.md) | A development authentication scheme until Step 12 | Accepted |

## Project skills

Repeatable workflows, checked in at [`.claude/skills/`](../.claude/skills/) so everyone
working in this repository gets them.

| Skill | Purpose |
|---|---|
| [`next-step`](../.claude/skills/next-step/SKILL.md) | Run the next step of the learning path end to end |
| [`new-adr`](../.claude/skills/new-adr/SKILL.md) | Record an architecture decision, with index updates |
| [`verify`](../.claude/skills/verify/SKILL.md) | The quality gate, plus this repo's known failure modes |

## Concepts — the reference material

| Document | Topic |
|---|---|
| [clean-architecture.md](concepts/clean-architecture.md) | Layers, the dependency rule, what goes where |
| [ddd.md](concepts/ddd.md) | Domain-Driven Design: bounded contexts, entities, value objects, aggregates |
| [solid.md](concepts/solid.md) | The five SOLID principles, with examples from this codebase |
| [design-patterns.md](concepts/design-patterns.md) | Every pattern this project uses, and why |
| [minimal-apis-vs-controllers.md](concepts/minimal-apis-vs-controllers.md) | The API style comparison |
| [glossary.md](concepts/glossary.md) | RAG, embedding, chunk, top-K, ACL, idempotency, and friends |
