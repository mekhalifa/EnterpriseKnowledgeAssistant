# Step 1 — Solution skeleton and the architecture decision

**Status:** ✅ complete — 11 projects, clean build with warnings-as-errors, 19 tests green
**Exit criteria:** `dotnet build` and `dotnet test` succeed on a clean clone — met

---

## 1. Environment verified

Checked before deciding anything, because the toolchain constrains the options.

| Tool | Version | Note |
|---|---|---|
| .NET SDK | **10.0.403** (also 8.0.416, 10.0.100-preview.5) | Target `net10.0`; pin with `global.json` |
| Docker | 29.1.5 | For SQL Server, Qdrant, Redis in Steps 3, 8, 14 |
| git | 2.48.1 | Repository initialized on branch `main` |
| Node.js | 24.15.0 | For the Angular 21 client in Step 13 |

Nothing needed installing.

---

## 2. The decision that shapes everything else

**How do you split the system into deployable units?** This is first because it cannot be
cheaply changed later, and every subsequent decision inherits from it.

| Option | What it is | Cost | When it is right |
|---|---|---|---|
| Single project | One `EKA.Api`, folders inside | Zero ceremony, fastest start | Prototypes, under ~2k lines, throwaway tools |
| **Modular monolith** ✅ | One deployable app, strict internal boundaries, plus one separate worker | Some ceremony, needs discipline | **This project** |
| Microservices | Ingestion, retrieval, chat, auth deployed separately | Network calls, distributed tracing, eventual consistency, ~5× ops work | Many teams, proven independent scaling need |

### Why modular monolith

One developer, 15 hours a week, 12 weeks. Microservices would consume the entire schedule
on infrastructure — service discovery, inter-service auth, retries across the network,
distributed debugging — and return nothing, because there is no independent scaling
requirement and no second team to decouple from. The monolith gives one `F5` to debug the
whole request path and one transaction boundary per use case.

The catch: **a monolith rots unless boundaries are enforced by the compiler.** Folders do
not stop you from calling SQL from the domain layer. Separate projects do, because a
reference you did not add is a build error. That is the entire reason to pay the ceremony
cost — it preserves the option of extracting a service later without paying for one now.

Recorded as [ADR-0001](../adr/0001-modular-monolith.md).

### The one exception: ingestion is a separate process

Parsing a 300-page PDF and generating thousands of embeddings takes minutes and consumes
CPU and memory. Inside the web API, one large upload makes every user's chat request slow,
and an API restart kills the job mid-document.

**The rule worth extracting from this:** split a process when the work has a *different
resource profile* and a *different failure tolerance* — not because "microservices are
modern". Interactive chat is short and latency-sensitive; ingestion is long and
throughput-sensitive. That is a real boundary.

Recorded as [ADR-0004](../adr/0004-separate-ingestion-worker.md).

---

## 3. The layering rule

Seven production projects. Dependencies point **inward only**:

```text
         EKA.Api ──────────┐          (HTTP, auth, DI composition root)
            │              │
            ▼              ▼
     EKA.Application   EKA.Infrastructure ──┐   (EF Core, SQL, blob storage)
            │              │                │
            │              ▼                ▼
            │           EKA.AI  ──────► EKA.Domain   (entities, rules — references NOTHING)
            └──────────────┴────────────────┘

       EKA.Contracts         (DTOs; source of the Angular client's generated types)
       EKA.Ingestion.Worker  → Application + Infrastructure + AI
```

**Why `EKA.Domain` references nothing at all.** Business rules — "a chunk belongs to a
document", "a user may only read what their ACL permits" — are the most valuable and
longest-lived code in the project. The moment `Domain` references EF Core, those rules are
welded to SQL Server and cannot be unit-tested without a database. Dependency-free means
its tests run in milliseconds with no containers, so they actually get run.

**Why `Application` declares interfaces that `Infrastructure` implements.** The use case
says "I need `IDocumentRepository`"; it does not know SQL exists. The practical payoff
arrives in Step 9: Qdrant can be swapped for Azure AI Search by writing one class, with
zero changes to the RAG logic built in Step 10.

Full reference matrix and the "what goes where" table:
[clean-architecture.md](../concepts/clean-architecture.md). Recorded as
[ADR-0002](../adr/0002-clean-architecture-layering.md).

### Alternatives considered

- **N-tier (UI → Business → Data).** Dependencies point *downward*, so Business depends on
  Data and the rules are coupled to the database again. Clean Architecture is N-tier with
  that one arrow inverted — same effort, strictly better.
- **Vertical Slice Architecture.** Organize by feature rather than layer, each slice
  self-contained. Genuinely excellent, often better for CRUD-heavy apps. Not chosen as the
  *top-level* structure because this system has a substantial shared core (retrieval, AI,
  access policy) used by many features — but adopted *inside* `EKA.Application` as the file
  layout, which captures most of the benefit. See
  [design-patterns.md](../concepts/design-patterns.md#vertical-slices).

---

## 4. API style: Minimal APIs

Chosen over MVC controllers. The short version: our endpoints are thin by design —
orchestration lives in `EKA.Application` handlers, so an endpoint binds the request, calls
a handler, and maps a `Result` to an HTTP status. That is a function, not a class. Add
per-endpoint dependency injection, group-level authorization, and full feature parity as of
.NET 9/10, and controllers have no remaining advantage for this shape of code.

The discipline it demands: one static class per feature in `EKA.Api/Endpoints/`, and no
business logic in a lambda. Minimal APIs do not enforce organization — you do.

Comparison table, code for both styles, and the cases where controllers still win:
[minimal-apis-vs-controllers.md](../concepts/minimal-apis-vs-controllers.md). Recorded as
[ADR-0003](../adr/0003-minimal-apis.md).

---

## 5. Build infrastructure — the files most tutorials skip

Three root files, created before any project, because retrofitting them across eleven
projects is tedious.

### `global.json` — pin the SDK

This machine has 10.0.403, 10.0.100-preview.5, and 8.0.416 installed. Without pinning,
build behaviour depends on which SDK happens to be selected, and CI may differ from local.
`rollForward: latestFeature` means "any 10.0.x is fine, .NET 11 is not".

### `Directory.Packages.props` — Central Package Management

By default every `.csproj` declares its own package versions. Across eleven projects,
versions drift: `EKA.Api` on Serilog 4.1, `EKA.Infrastructure` on 4.3. The symptom is a
confusing binding error or a behaviour difference that costs hours.

With CPM, versions live in exactly one file and projects write
`<PackageReference Include="Serilog" />` with no version attribute. A dependency upgrade
becomes a one-line diff.

### `Directory.Build.props` — shared properties

`net10.0`, nullable reference types, implicit usings, warnings-as-errors, deterministic
builds — inherited by every project automatically, so nobody forgets to enable nullable on
project nine.

Recorded as [ADR-0005](../adr/0005-central-package-management.md).

---

## 6. What was built

```text
EnterpriseKnowledgeAssistant/
  global.json                  SDK pinned to 10.0.403
  Directory.Build.props        net10.0, nullable, warnings-as-errors — all 11 projects
  Directory.Packages.props     every NuGet version, declared once
  nuget.config                 package source mapping (see section 7)
  src/
    EKA.Domain/                references nothing
    EKA.Contracts/             references nothing
    EKA.Application/           → Domain, Contracts
    EKA.Infrastructure/        → Application, Domain
    EKA.AI/                    → Application, Domain
    EKA.Api/                   → Application, Infrastructure, AI, Contracts
    EKA.Ingestion.Worker/      → Application, Infrastructure, AI
  tests/
    EKA.Domain.Tests/          7 tests — domain isolation
    EKA.Application.Tests/     7 tests — application boundary
    EKA.AI.Tests/              2 tests — adapter boundary
    EKA.IntegrationTests/      3 tests — contracts + infrastructure boundaries
```

18 project references wired. Build clean with `TreatWarningsAsErrors`; **19 tests pass**.

**The tests are the interesting part.** Rather than placeholder `Assert.True(true)`, every
test asserts a layer boundary by reflecting over `GetReferencedAssemblies()` — so the
dependency rule is enforced by CI, not by reviewer memory. Each test project guards *its
own* boundary, which is why `EKA.Application.Tests` references only `EKA.Application`: it
cannot accidentally observe an outer layer even if it wanted to.

Known caveat, documented in the test file itself: the compiler omits references that
compiled code never uses, so with near-empty projects these assertions pass easily. They
gain teeth as real code lands from Step 2 — which is exactly when crossing a boundary
starts being tempting.

Each layer also got an `…AssemblyMarker` type: a stable anchor for tests and for the
assembly scanning that DI registration and validator discovery will need later, so no
code ever hard-codes an assembly name as a string that breaks silently on rename.

---

## 7. The unplanned lesson: dependency confusion

Enabling Central Package Management failed the first build:

```text
error NU1507: There are 2 package sources defined in your configuration. When using
central package management, map your package sources with package source mapping or
specify a single package source. The following sources are defined: nuget.org, SBC Nuget
```

This machine has three NuGet feeds configured globally: nuget.org, an internal SBC feed,
and the Visual Studio offline package folder.

**Why NuGet treats this as an error rather than a preference.** With several feeds and no
mapping, NuGet may resolve a package ID from *any* of them. That is the
**dependency-confusion** attack: an attacker publishes a package to a public feed using
the same ID as your internal package, often with a higher version number, and your restore
pulls theirs instead. The payload then executes at **build time**, on a developer machine
or a CI agent, with whatever credentials that machine holds. It has been used successfully
against many large organizations.

**The wrong fix** — and the tempting one — is `<NoWarn>NU1507</NoWarn>`. The build goes
green and the vulnerability remains, which is worse than before because the warning that
would have flagged it is now suppressed.

**The fix applied:** a committed `nuget.config` that clears inherited feeds, declares
nuget.org only, and maps `*` to it explicitly. Every package ID now has exactly one
permitted source. A private feed added later gets a mapping limited to its own ID prefix
(`Sbc.*`) — `*` is never mapped to two sources, because that reintroduces the ambiguity.

The secondary benefit is reproducibility: a teammate or CI agent without the internal feed
configured now gets an identical restore, because the repository declares its own sources
instead of inheriting whatever the machine happens to have.

Recorded as [ADR-0006](../adr/0006-package-source-mapping.md).

**The generalizable lesson:** when a tool escalates something to an error, find out what
it is protecting you from before silencing it. The error cost ten minutes; the thing it
prevents costs a breach.

---

## 8. What to take away from Step 1

- **The process split is the expensive decision.** Make it on resource profile and failure
  tolerance, not fashion. Everything else in this step is reversible; this is not.
- **Boundaries need an enforcer.** A convention nobody can violate beats a convention
  everyone agrees with. Projects over folders, database constraints over in-memory checks.
- **Abstractions must name a real seam.** We inverted the vector store, the model provider,
  and storage because they are genuinely swappable. We will *not* create `IDocumentMapper`.
- **Decide once, write it down.** The ADRs cost ten minutes each and answer "why is it like
  this?" for the rest of the project — including the conditions under which the answer
  should change.

## Next

**Step 2 — the domain model.** Entities, value objects, invariants enforced in
constructors, and the chunk-ordering rule. Reference: [ddd.md](../concepts/ddd.md).
