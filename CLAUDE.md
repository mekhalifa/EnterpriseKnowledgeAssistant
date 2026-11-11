# CLAUDE.md — Enterprise Knowledge Assistant (EKA)

Instructions for Claude Code when working in this repository.

## What this project is

An enterprise knowledge assistant: it ingests organizational documents, indexes their
content as vectors, answers questions with grounded citations, and enforces
document-level access rules.

Stack: **ASP.NET Core (.NET 10) + Angular 21 + SQL Server + Qdrant + Redis**, with AI
providers reached through **Microsoft.Extensions.AI** abstractions.

The full 12-week delivery plan lives in [README.md](README.md). The learning path we are
actually executing lives in [docs/00-learning-path.md](docs/00-learning-path.md).

## Scope constraint

**Work only inside `D:\Repos\AI\EnterpriseKnowledgeAssistant`.** Do not read, write, or
extract anything from the parent `D:\Repos\AI` folder (it contains an unrelated archive).
This repository is being built from scratch — treat it as greenfield.

## Current state

See [docs/PROGRESS.md](docs/PROGRESS.md) — it is the single source of truth for what is
done and what the next action is. Read it at the start of every session.

## How to work in this repo (teaching mode)

The owner is using this project to learn enterprise .NET and RAG architecture. Therefore,
for every non-trivial change:

1. **Explain what** we are building.
2. **Explain why** this approach — the actual engineering reason, not a slogan.
3. **State the best option** and recommend it clearly.
4. **List the alternatives** and the condition under which each would win.
5. **Save the explanation** as a step document in `docs/steps/` so it becomes a permanent
   reference. Record the decision itself as an ADR in `docs/adr/`.
6. **Update `docs/PROGRESS.md`** — mark what is done, set the next action.

Work one step at a time. Do not jump ahead to later steps of the learning path.

## Architecture rules (enforced, not advisory)

Dependencies point **inward only**:

```text
EKA.Api ──► EKA.Application ──► EKA.Domain
EKA.Api ──► EKA.Infrastructure ──► EKA.Application, EKA.Domain
EKA.Api ──► EKA.AI ──► EKA.Application, EKA.Domain
EKA.Ingestion.Worker ──► EKA.Application, EKA.Infrastructure, EKA.AI
EKA.Contracts ──► (nothing)
```

- `EKA.Domain` **must reference no other project and no infrastructure package**
  (no EF Core, no HTTP clients, no Microsoft.Extensions.AI).
- `EKA.Application` defines interfaces (`IDocumentRepository`, `IChatCompletionService`);
  `EKA.Infrastructure` and `EKA.AI` implement them.
- Never reference `EKA.Infrastructure` from `EKA.Application`.
- Authorization is enforced in **retrieval**, never only in the UI.

Details: [docs/concepts/clean-architecture.md](docs/concepts/clean-architecture.md).

## Code conventions

- `net10.0`, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- File-scoped namespaces, primary constructors where they read well, `sealed` by default.
- Package versions live **only** in `Directory.Packages.props` (Central Package Management).
  Never put a `Version=` attribute in a `.csproj`.
- Domain entities protect their invariants in the constructor; no public setters.
- Prefer the **Result pattern** over exceptions for expected failures (validation, not found).
- Tests: xUnit; one test project per production layer. Each layer's test project
  guards its own architectural boundary. **Assertion library is an open decision** —
  FluentAssertions v8+ requires a paid commercial licence, so the candidates are plain
  `Assert`, Shouldly (MIT), or AwesomeAssertions (MIT fork of FluentAssertions 7).
  Decide at Step 2; see [docs/PROGRESS.md](docs/PROGRESS.md).
- NuGet: packages resolve from nuget.org only, enforced by package source mapping in
  `nuget.config`. Adding a private feed means mapping it to its own ID prefix —
  never map `*` to two sources. See [ADR-0006](docs/adr/0006-package-source-mapping.md).
- Secrets: User Secrets locally, environment variables in containers, Key Vault in
  production. Never a plaintext key in `appsettings.json` or a database column.

## Common commands

```powershell
dotnet build                        # build the solution
dotnet test                         # run all tests
dotnet format                       # apply style rules
docker compose -f deploy/docker-compose.yml up -d    # local dependencies
dotnet ef migrations add <Name> --project src/EKA.Infrastructure --startup-project src/EKA.Api
dotnet ef database update --project src/EKA.Infrastructure --startup-project src/EKA.Api
```

## Documentation map

| Document | Purpose |
|---|---|
| [docs/PROGRESS.md](docs/PROGRESS.md) | **Read first.** Done / in progress / next action |
| [docs/00-learning-path.md](docs/00-learning-path.md) | The 14 steps and their exit criteria |
| [docs/steps/](docs/steps/) | Full teaching write-up for each completed step |
| [docs/adr/](docs/adr/) | Architecture Decision Records — what we chose and why |
| [docs/concepts/](docs/concepts/) | Reference material: Clean Architecture, DDD, SOLID, patterns |
| [.claude/skills/](.claude/skills/) | Project skills — the repeatable workflows below |

## Project skills

Checked in under `.claude/skills/`, so they are shared by anyone working in this repo.
Each one's body loads only when invoked, which keeps the always-loaded context small.

| Skill | Use it when |
|---|---|
| `next-step` | Starting the next step of the learning path — "go", "next", "start step N" |
| `new-adr` | A non-trivial technical choice was made and needs recording |
| `verify` | Checking the build, or before committing a step |

The `next-step` skill carries the full working rhythm (orient → teach → build → verify →
document → commit), so that detail does not need to sit in this file.
