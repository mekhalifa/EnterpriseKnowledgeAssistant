# Step 3 — Persistence: EF Core + SQL Server in Docker

**Status:** ✅ complete — 233 tests (45 integration against real SQL Server), clean build
**Exit criteria:** the migration applies to a clean database; integration tests pass
against the container — met

---

## 1. What was built

```text
src/EKA.Application/Abstractions/     interfaces the domain side declares
  IUnitOfWork  ICurrentUser  IDomainEventDispatcher
  IDocumentRepository  IConversationRepository  IUserRepository
  IPromptTemplateRepository  IAIProviderRepository

src/EKA.Infrastructure/
  DependencyInjection.cs              AddInfrastructure — the only place that binds
  Persistence/
    EkaDbContext.cs                   unit of work + post-commit event dispatch
    EkaDbContextFactory.cs            design-time factory for `dotnet ef`
    ValueObjectConverters.cs          six single-value conversions
    LoggingDomainEventDispatcher.cs   placeholder until Step 7
    Configurations/                   one per aggregate, 7 files
    Interceptors/AuditInterceptor.cs  CreatedBy / ModifiedBy
    Repositories/                     5 internal implementations
    Migrations/20261112082939_InitialSchema

deploy/docker-compose.yml             SQL Server 2022, health check, named volume
deploy/.env.example                   password template; .env is gitignored
.config/dotnet-tools.json             dotnet-ef pinned to 10.0.15

tests/EKA.IntegrationTests/           45 tests against a real containerized database
```

Verified in an actual database: 8 tables plus `__EFMigrationsHistory`, 12 indexes,
`ContentHash` as `char(64)`.

---

## 2. Two version traps, within ten minutes of each other

**`dotnet add package` installed EF Core 11.0.0, which does not support `net10.0`.**

```text
error NU1202: Package Microsoft.EntityFrameworkCore.SqlServer 11.0.0 is not compatible
with net10.0. Package ... supports: net11.0
```

`dotnet add package` resolves the **latest** version, always. The .NET 11 packages exist
and target `net11.0` exclusively. The `dotnet-ef` tool installed as 11.0.0 for the same
reason, which would have produced a subtler mismatch later.

The fix: pin the whole Microsoft family to one version line, expressed as an MSBuild
property so it cannot drift:

```xml
<MicrosoftRuntimeVersion>10.0.15</MicrosoftRuntimeVersion>
...
<PackageVersion Include="Microsoft.EntityFrameworkCore.SqlServer"
                Version="$(MicrosoftRuntimeVersion)" />
```

**The generalizable lesson:** `dotnet add package` is a convenience, not a decision. On a
project pinned to an LTS framework, adding Microsoft packages needs an explicit version.

**Then I broke the build myself, and the error pointed somewhere else entirely.** Writing
a CLI flag inside an XML comment:

```xml
<!-- ... use an explicit `--version 10.0.*` ... -->
```

**An XML comment may not contain two consecutive hyphens.** MSBuild could not load
`Directory.Packages.props` at all, which silently turned Central Package Management off,
which made every project report:

```text
error NU1015: The following PackageReference item(s) do not have a version specified
```

Eleven projects reporting "no version specified" for packages whose versions are obviously
declared. The actual cause was one character, in a comment, in a file nobody was looking
at. The symptom named the consequence, never the cause.

**The lesson worth keeping:** when an error makes no sense, check whether the file that
would make it make sense is actually being read. `[xml](Get-Content file)` in PowerShell
took five seconds and answered it.

---

## 3. The two relocated invariants now exist

Step 2 moved two rules out of the aggregates because no aggregate could see enough to
enforce them. Step 3 gave them homes, and — more importantly — **tests proving they work**.

**Chunk index uniqueness** → `UX_DocumentChunks_DocumentId_ChunkIndex`, unique.

```csharp
// Added straight to the DbSet, deliberately bypassing Document.AddChunks — which is what
// Step 8's bulk path will do, and what a retried job racing the original does by accident.
context.DocumentChunks.Add(TestFixtures.NewChunk(document.Id, index: 0));
context.DocumentChunks.Add(TestFixtures.NewChunk(document.Id, index: 0));

await Should.ThrowAsync<DbUpdateException>(async () => await context.SaveChangesAsync());
```

**One active prompt template per name** → `UX_PromptTemplates_Name_Active`, unique with
filter `([IsActive]=(1))`.

The filter is the whole trick. A plain unique index on `Name` would permit only one version
ever, destroying versioning. Filtered, it permits any number of inactive versions and at
most one active — a rule `Activate()` cannot enforce, because an aggregate only ever sees
itself.

```sql
UX_PromptTemplates_Name_Active | unique=1 | filter=([IsActive]=(1))
```

**Why the tests matter more than the indexes.** An invariant that was relocated but never
verified in its new home is an invariant that silently stopped existing — and the
documentation saying it moved makes that *worse*, because it reads as reassurance.

---

## 4. Why a real database, not the in-memory provider

Every integration test runs against SQL Server 2022 in a Testcontainers container.

The in-memory provider implements none of what this step is about: unique indexes, filtered
indexes, cascade deletes, column types, FK restrictions. The duplicate-chunk-index test
would **pass in memory and fail in production** — the worst possible outcome, because it
converts a test suite from a safety net into a source of false confidence.

Cost: about 33 seconds per run, nearly all of it SQL Server starting. One container per
run, not per test; tests isolate with fresh tenant ids.

---

## 5. The clean-database test

The exit criterion is not "the migration applies" but "the migration applies **to a fresh
database**". Those differ: a developer's database accumulated its schema one migration at a
time and carries state no new environment will have.

```csharp
var databaseName = $"eka_clean_{Guid.NewGuid():N}";
var connectionString = await _fixture.CreateEmptyDatabaseAsync(databaseName);
await using var context = _fixture.CreateContext(connectionStringOverride: connectionString);

await context.Database.MigrateAsync();

(await context.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
```

Plus parameterized tests asserting every expected table and every index with its correct
uniqueness, read from `sys.indexes` — so a dropped index fails the build rather than
surfacing as a mysterious duplicate row six months later.

---

## 6. Encapsulation survived contact with the ORM

`Document.Chunks` returns `_chunks.AsReadOnly()`, which EF cannot add to. The usual
compromise is to weaken the domain — expose a settable `List<T>` and accept that any caller
can mutate it.

Not necessary:

```csharp
builder.Navigation(document => document.Chunks)
    .UsePropertyAccessMode(PropertyAccessMode.Field);
```

EF writes `_chunks` directly and never calls the getter. The domain keeps its
encapsulation; EF gets what it needs.

The same applies to audit stamping. `CreatedBy` has a `protected` setter, and the
interceptor never calls it — it writes through the change tracker, which reaches the
backing field via model metadata:

```csharp
entry.Property(nameof(EntityAudit<Guid>.CreatedBy)).CurrentValue = principal;
```

**`CreatedBy` is deliberately not touched on update.** An audit record that can be rewritten
is not an audit record.

---

## 7. Events dispatch after the commit, never before

```csharp
var domainEvents = aggregates.SelectMany(a => a.DomainEvents).ToList();   // collect first

var written = await base.SaveChangesAsync(cancellationToken);             // then commit

foreach (var aggregate in aggregates) aggregate.ClearDomainEvents();      // clear
await _domainEventDispatcher.DispatchAsync(domainEvents, cancellationToken);
```

Three details, each with a failure behind it:

- **Collect before, dispatch after.** Dispatching first lets a handler notify a user that
  their document is searchable, and then have the transaction roll back. Tested: a commit
  failing on the unique index dispatches nothing.
- **Clear before dispatching.** A handler that itself calls `SaveChangesAsync` would
  otherwise re-dispatch the same events forever. Tested.
- **`LoggingDomainEventDispatcher` is a placeholder and says so loudly.** Real handlers
  arrive in Step 7. A silent no-op was rejected: events would vanish with no trace, and the
  first real handler would be debugged against a dispatcher that never ran.

---

## 8. What the failing test taught (again)

One test failed, and it was my assertion that was wrong:

```text
System.InvalidOperationException : The association between entity types 'AIProvider' and
'PromptTemplate' has been severed, but the relationship is ... implicitly required
```

I expected `DbUpdateException` from `SaveChangesAsync`. EF threw `InvalidOperationException`
from **`Remove()`** — before generating any SQL. When the dependent is *tracked*, EF's
change tracker notices a required foreign key would be severed and refuses immediately.

The protection exists at two layers, and they are not the same:

| Layer | When it fires | Protects against |
|---|---|---|
| EF change tracker | Dependent is tracked | This application's own code paths |
| SQL Server foreign key | Always | Migration scripts, a DBA, any other client |

The test now asserts **both**, because only the second is a real guarantee — EF's check
works solely when the dependent happens to be loaded.

---

## 9. An EF constraint worth knowing

Model building failed outright:

```text
No suitable constructor was found for the type 'Citation'.
Cannot bind 'pageRange' in 'Citation(..., PageRange pageRange, ...)'
Note that only mapped properties can be bound to constructor parameters. Navigations to
related entities, including references to owned types, cannot be bound.
```

**Owned types cannot be bound to constructor parameters.** `Citation` takes a `PageRange`,
which is owned, so EF had nothing it could call.

`DocumentChunk` has the same shape and was fine — because it already had a private
parameterless constructor for EF. `Citation`, a record, did not. Adding one fixed it, using
the identical pattern every entity in the model already relies on.

---

## 10. Docker, done properly

The README flagged the previous compose file as P0: it built SQL Server from a local
Dockerfile (impossible) and used LocalDB from a Linux container (Windows-only, unreachable).

```yaml
image: mcr.microsoft.com/mssql/server:2022-latest
environment:
  ACCEPT_EULA: "Y"
  MSSQL_SA_PASSWORD: ${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD must be set in deploy/.env}
ports:
  - "127.0.0.1:1433:1433"
healthcheck:
  test: [... sqlcmd ... "SELECT 1" ...]
  start_period: 40s
```

Four decisions worth copying:

- **No default password and no fallback.** `${VAR:?message}` fails the command with an
  explanation. A compose file with a committed password is a compose file that reaches
  production with that password.
- **`127.0.0.1:` prefix.** Without it Docker publishes on *every* interface, so a laptop
  database is reachable from the coffee-shop wifi.
- **Named volume, not a bind mount.** SQL Server needs specific ownership on its data
  directory; a host bind mount produces permission errors that look like corruption.
- **A health check that runs a query**, not one that checks the process exists. SQL Server
  takes tens of seconds to accept logins, and "container started" is not "database
  available". `start_period` keeps a slow first boot from counting as failure.

Even so, the first `database update` against the container timed out: the health check
passed on `SELECT 1` against `master` while `msdb` was still running upgrade steps. Healthy
is not always *finished*.

---

## 11. What to take away from Step 3

- **Pin versions deliberately on a pinned framework.** `dotnet add package` always takes
  the newest thing, which is the wrong thing on an LTS target.
- **When an error makes no sense, check the file it depends on is being read at all.** One
  invalid XML comment produced eleven misleading errors in other files.
- **A relocated invariant needs a test in its new home**, or it has quietly ceased to exist
  while the documentation claims otherwise.
- **Test against the real engine when the thing you are testing is the engine.** Indexes,
  constraints, and column types are not implemented by in-memory providers.
- **The ORM does not require weakening the domain.** `PropertyAccessMode.Field` and change-
  tracker writes preserve private collections and protected setters.
- **Know which layer a guarantee lives in.** EF's client-side FK check is a convenience;
  the database constraint is the guarantee.

## Next

**Step 4 — the application layer and API surface.** Use cases as vertical slices, the
Result pattern, FluentValidation, minimal API endpoint groups, health checks, and
ProblemDetails error handling — including mapping `InvalidStateTransitionException` to
HTTP 409, which Step 2 designed for and nothing has used yet.
