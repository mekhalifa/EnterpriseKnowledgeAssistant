# Step 4 — Application layer and the API surface

**Status:** ✅ Complete
**Date:** 2026-11-12
**Branch:** `step-04-application-api`
**Result:** 322 tests, zero warnings. The API serves routes for the first time.

Before this step the solution could model a document and store one, and could not be asked
to. `EKA.Api` had a composition root and no endpoints. This step added the layer that turns
a request into a use case, and the HTTP surface that exposes it.

---

## 1. What was built

```text
EKA.Application/
  Common/        Result, Result<T>, Error, ErrorType, PagedResult<T>, HealthTags
  Documents/
    RegisterDocument/  Command, Handler, RequestValidator
    GetDocument/       Query, Handler
    ListDocuments/     Query, Handler
    RenameDocument/    Command, Handler, RequestValidator
    ResetDocument/     Command, Handler
    DeleteDocument/    Command, Handler
    DocumentErrors.cs  DocumentMapping.cs  StorageKeys.cs
  DependencyInjection.cs

EKA.Contracts/
  Common/PagedResponse.cs
  Documents/  RegisterDocumentRequest, RenameDocumentRequest,
              DocumentResponse, DocumentSummaryResponse

EKA.Api/
  Endpoints/  DocumentEndpoints.cs, HealthEndpoints.cs
  Errors/     ErrorResults.cs, DomainExceptionHandler.cs, BadRequestExceptionHandler.cs
  Filters/    ValidationFilter.cs
  Security/   DevelopmentAuthenticationOptions/Handler, NoIdentityProviderHandler,
              TenantPolicy.cs
```

Seven endpoints:

| Method | Route | Returns |
|---|---|---|
| `POST` | `/api/documents` | `201` created, or `200` if these bytes were already registered |
| `GET` | `/api/documents/{id}` | `200` / `404` |
| `GET` | `/api/documents?status=&page=&pageSize=` | `200` paged / `400` |
| `PUT` | `/api/documents/{id}/title` | `200` / `400` / `404` |
| `POST` | `/api/documents/{id}/reset` | `200` / `404` / `409` |
| `DELETE` | `/api/documents/{id}` | `204` / `404` |
| `GET` | `/health/live`, `/health/ready` | `200` / `503`, anonymous |

Plus `.editorconfig`, deferred twice and now written against real code.

Four decisions recorded: [ADR-0011](../adr/0011-result-pattern.md),
[ADR-0012](../adr/0012-vertical-slices-no-mediator.md),
[ADR-0013](../adr/0013-validation-placement.md),
[ADR-0014](../adr/0014-development-authentication.md).

**Scope correction made at the start.** The step plan did not say which features get
endpoints. Conversations were deliberately left out: Step 6's exit criterion is "a client
creates a conversation, asks a question, and reads back persisted messages", so conversation
endpoints belong there. Document *upload* — actual bytes, storage, size limits — is Step 7.
Step 4 registers metadata, which exercises the entire request → validate → handle →
persist → respond path that everything after it is built on.

---

## 2. The problem that had to be solved before any endpoint could be written

Step 3's whole security argument is that every repository method takes a `TenantId` and
applies it inside the query, so a caller cannot forget a filter it was never able to write.

Step 4 then discovered the obvious consequence: **the endpoints had no tenant to pass.**
`ICurrentUser` reads claims from `HttpContext`, no identity provider exists until Step 12,
and it returns null for every request.

The tempting fixes are all bad. Take the tenant from a route parameter or the body, and the
hole ADR-0009 closed is reopened — in the form hardest to walk back, because clients start
depending on the URL. Ship no endpoints until Step 12, and Steps 5 through 11 have nothing
to test against.

The fix was a **development-only authentication scheme** that mints a principal from
`X-EKA-Tenant` and `X-EKA-Subject` headers, issuing the same `sub`/`tid` claims a real OIDC
provider would. Nothing downstream knows it exists. Three independent gates keep it inside
development — a default-false flag, an `IsDevelopment()` check, and a **startup throw** if
the flag is ever true anywhere else.

The alternative that almost won was a hard-coded development tenant constant: three lines
instead of a hundred and fifty. It was rejected for one reason — it makes the most valuable
test in the suite impossible to write:

```csharp
var owner    = _factory.CreateClientFor(Guid.NewGuid());
var intruder = _factory.CreateClientFor(Guid.NewGuid());

var created = await owner.PostAsJsonAsync("/api/documents", NewRequest());
var document = await created.Content.ReadFromJsonAsync<DocumentResponse>();

var response = await intruder.GetAsync($"/api/documents/{document!.Id}");

response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
```

Two tenants, one server, over real HTTP against real SQL Server. That assertion is the
reason the project exists in the shape it does, and a constant tenant would have deferred it
to Step 12 — the step where it is load-bearing.

Full reasoning: [ADR-0014](../adr/0014-development-authentication.md).

---

## 3. `Result`: what it buys and what it costs

"Document not found" is not exceptional. It is the other half of what a lookup does. An
exception for it costs a stack capture on a routine path, is invisible in the signature, and
makes every caller guess what might come flying out.

```csharp
public async Task<Result<DocumentResponse>> HandleAsync(GetDocumentQuery query, …)
{
    var document = await documents.GetByIdAsync(query.TenantId, query.DocumentId, …);

    return document is null
        ? DocumentErrors.NotFound(query.DocumentId)
        : document.ToResponse();
}
```

Both branches convert implicitly to `Result<DocumentResponse>` — a target-typed conditional,
so the ceremony disappears while the contract stays in the signature.

### The struct trap, and why it is handled rather than hidden

`docs/concepts/design-patterns.md` specifies `readonly record struct`. C# gives every struct
an implicit parameterless constructor, so `default(Result<T>)` exists and bypasses both
factory methods. It is neither a success nor a well-formed failure.

Pretending otherwise would mean a failure carrying a null `Error`, which the API layer would
dereference. So:

```csharp
public Error Error => _error ?? throw new InvalidOperationException(
    IsSuccess
        ? "A successful result has no error."
        : "This Result was created with `default`, bypassing Success() and Failure(). "
          + "It carries no error to report.");
```

This is the same hazard `TenantId` documented in Step 2, handled the same way: **the type
cannot prevent the default, so it refuses to pretend the default is meaningful.** A test
asserts the message names the actual cause, because the person who hits it will otherwise
hunt for a missing assignment that never existed.

### The deliberate exception to "no exceptions"

`ResetDocumentHandler` does **not** check the status before calling `Document.Reset`. It
would be easy, and it would produce a tidier error message. It would also be a second copy
of the state machine Step 2 put in the aggregate — and copies drift. When Step 7 decides a
document stuck in `Extracting` for an hour may also be reset, the domain changes and the
handler's copy silently keeps rejecting it.

So the domain stays the only authority on legal transitions, throws
`InvalidStateTransitionException`, and the API maps that to `409` in exactly one place.
[ADR-0011](../adr/0011-result-pattern.md) records why that is not a retreat from the
`Result` pattern: the alternative is not "fewer exceptions", it is "the state machine
written down twice".

---

## 4. Validation in three layers, each with one job

| Layer | Checks | On failure |
|---|---|---|
| Endpoint filter (FluentValidation) | Shape: required, length, range, format | `400` + field-keyed `ValidationProblemDetails` |
| Handler (`Result`) | Rules needing state | Mapped status, stable error code |
| Domain (throw) | Invariants, always | `DomainException` → `500` |

The duplication between layer 1 and layer 3 is deliberate and bounded. `DocumentTitle`
already refuses an empty title — but it throws a sentence with no field name, so a client
form cannot highlight the box. The validator produces
`{"ContentHash": ["must be a 64-character hex SHA-256 digest"]}`, which a form can act on.

The rule that keeps the duplication honest: **the edge may repeat a domain rule; it may
never exceed one.** A rule the edge enforces and the domain does not creates states the
ingestion worker can reach and the API cannot.

And the rule that keeps validators useful: **a validator never performs I/O.** "Has this
hash been seen before?" feels like validation and is not — it needs the database, runs
before authorisation, doubles the round trips, and still races a concurrent insert. It
belongs in the handler, returning a `Result`. [ADR-0013](../adr/0013-validation-placement.md).

---

## 5. What went wrong

### 5.1 `UseExceptionHandler` turns a correct 400 into a 500

This one is worth remembering, because nothing in the diff suggests it.

Post `{ this is not json` to a minimal API and parameter binding throws
`BadHttpRequestException`, which carries `StatusCode = 400`. With no exception handler
registered, ASP.NET Core honours it and the client gets a 400.

Add `app.UseExceptionHandler()` — the thing you add in order to produce *better* errors —
and the exception now reaches the handler pipeline, no handler claims it, and the fallback
writes **500**. Every endpoint at once. The server starts taking the blame for the client's
malformed JSON, and the only visible change was switching on structured error handling.

```text
Shouldly.ShouldAssertException : response.StatusCode
    should be HttpStatusCode.BadRequest
    but was  HttpStatusCode.InternalServerError
```

The fix is `BadRequestExceptionHandler`, registered first, honouring
`badRequest.StatusCode`. The detail is replaced with "The request body could not be read."
— the exception's own message names the parameter that failed to bind and carries a
`JsonException` underneath, which describes our method signatures to whoever sent the bad
request.

**What this cost to find: one end-to-end test.** No unit test could have caught it. The bug
lived entirely in the interaction between middleware that was each individually correct.

### 5.2 `WebApplicationFactory` configuration arrives too late under minimal hosting

The obvious way to point the test host at the container's SQL Server:

```csharp
builder.ConfigureAppConfiguration((_, configuration) =>
    configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:EkaDatabase"] = connectionString,
    }));
```

Eighteen tests failed with the application's own guard:

```text
System.InvalidOperationException : Connection string 'EkaDatabase' is not configured.
```

A test that plainly configures a connection string, failing because no connection string is
configured. The cause is ordering. Under the minimal hosting model, `Program.cs` reads
`builder.Configuration` **while its top-level statements run** — `AddInfrastructure` demands
the connection string on line 119 — and that happens before `ConfigureAppConfiguration`
callbacks are applied.

`UseSetting` writes into *host* configuration, which is already present the first time
`builder.Configuration` is read:

```csharp
builder.UseSetting($"ConnectionStrings:{DependencyInjection.ConnectionStringName}", connectionString);
builder.UseSetting("Authentication:Development:Enabled", "true");
```

The general lesson: with minimal hosting, anything `Program.cs` reads *during startup* must
come from host configuration, not from a callback that runs later. The error message will
always be the application's own guard, which is to say it will always point at the wrong
thing.

### 5.3 A static class cannot be a generic type argument

`AddValidatorsFromAssemblyContaining<ApplicationAssemblyMarker>()` does not compile — CS0718
— because the marker is `static`. `AddValidatorsFromAssembly(typeof(…).Assembly)` is the
form that works. Trivial, and worth a line here because the marker-class idiom appears
throughout this codebase and the overload that takes a type argument is the one everybody
reaches for first.

---

## 6. Decisions inside the code worth knowing about

**Handlers are listed, validators are scanned for.** The asymmetry is deliberate. An
unregistered handler fails to resolve and the request errors loudly. An unregistered
validator silently validates nothing. So handler registration is a hand-written inventory
that fails at compile time when it goes stale, and validators are discovered so that writing
one is enough to make it run.

**The storage key is built from the tenant and a server-generated id, never the filename.**
A client-supplied path is a directory-traversal and overwrite vector —
`../../other-tenant/policy.pdf` — and no amount of sanitising beats never accepting it.
There is a test:

```csharp
var command = Command() with { FileName = "../../other-tenant/secret.pdf" };
…
stored.StorageKey.ShouldStartWith($"{TestData.Tenant.Value:N}/");
stored.StorageKey.ShouldNotContain("..");
```

**`Guid.CreateVersion7()`, not `NewGuid()`.** A v7 GUID leads with a millisecond timestamp,
so consecutive inserts land next to each other in SQL Server's clustered index instead of
scattering across it. Random v4 keys fragment the index and turn an append-only table into
a page-splitting one.

**Cross-tenant access returns `404`, never `403`.** A 403 confirms the id exists, which lets
someone map another tenant's document ids without ever reading one. The metadata is the
leak.

**Deleting something that is not there returns `404`, not `204`.** HTTP permits either, and
"already gone is fine" is defensible — but then a wrong-tenant delete gets the same cheerful
204 as a legitimate one, and nothing in any log ever shows it happened.

**`Enum.TryParse`'s return value is checked, and `Enum.IsDefined` as well.** `TryParse`
accepts `"999"` and hands back a value no member declares. Without the second check the
status filter would match nothing while reporting a perfectly healthy empty page. There is
a test named after exactly that.

**Reject or clamp, and the line between them.** `page=0` cannot be what anyone meant, so it
is a 400 — silently serving page 1 hides the client's bug until someone notices their
pagination skips rows. `pageSize=100000` is a reasonable request meeting a server policy, so
it is capped at 100 and the response states the size actually applied.

---

## 7. Liveness and readiness are not the same probe

```text
/health/live   → checks nothing but its own ability to answer   → failure means RESTART
/health/ready  → checks SQL Server (Qdrant, Redis later)        → failure means DRAIN
```

Put the database check on the liveness probe and a brief database outage becomes a restart
loop across every instance: each one is killed for a fault it did not cause and cannot fix,
and the stampede of reconnecting instances then keeps the database down. Readiness sheds
traffic and waits. Liveness pulls the trigger. Conflating them turns a recoverable incident
into an outage.

The readiness writer reports each check **by name**, because an empty check list also
reports "Healthy" — which is precisely what a mistyped tag produces. The tag itself lives in
`EKA.Application.Common.HealthTags`, reachable from both the layer that registers the check
and the layer that filters on it; two private copies would compile perfectly and produce a
probe that runs nothing.

Both probes are anonymous. A probe that needs a token reports "unhealthy" the day the token
expires.

---

## 8. Testing: what each level is actually for

**322 tests, up from 233.**

| Project | Tests | What only this level can prove |
|---|---|---|
| `EKA.Domain.Tests` | 179 | Invariants, with no infrastructure in sight |
| `EKA.Application.Tests` | 78 | Use-case behaviour against in-memory fakes, in milliseconds |
| `EKA.IntegrationTests` | 63 | Schema, indexes, **and now the wired application** |
| `EKA.AI.Tests` | 2 | Boundary |

The 18 new endpoint tests boot the real `Program.cs` through `WebApplicationFactory`. That
matters because the bugs this step can produce are *wiring* bugs: a filter on the wrong
endpoint, a policy registered but never applied, an exception handler ordered after the
thing it should catch, a route that quietly allows anonymous access. None is visible to a
test that assembles its own pipeline — such a test proves the handlers work, which the unit
tests already proved. §5.1 is the worked example: a 500 where a 400 belonged, invisible to
every other level of testing.

The fakes are hand-written, not mocks. A mock is configured per test — "when `GetByIdAsync`
is called with this id, return that" — so every test restates the repository's behaviour and
can accidentally describe a repository that could not exist. `FakeDocumentRepository`
behaves like the real one once, and **enforces the tenant filter**: a fake that ignored the
tenant would let a handler that forgot to pass one pass its tests.

`FakeUnitOfWork` counts commits, and several tests assert `SaveCount.ShouldBe(0)`. "The
handler returned the right error and also committed" is a bug that loose mock verification
routinely misses.

`FixedTimeProvider` is ten lines rather than a NuGet package.
`Microsoft.Extensions.TimeProvider.Testing` sits on a different version line from the rest
of the Microsoft family, which is a standing upgrade hazard in exchange for ten lines. It is
the test-side half of [ADR-0008](../adr/0008-deterministic-time-in-domain.md), and it is why
this assertion is possible at all:

```csharp
result.Value.Document.CreatedAt.ShouldBe(FixedTimeProvider.DefaultNow);
```

Exact equality, not "within a second of now" — the kind of assertion that passes on a laptop
and fails on a loaded CI agent.

---

## 9. `.editorconfig`, and why it does not fail the build

Deferred from Steps 1 and 3; written now that there is real code to describe.

`EnforceCodeStyleInBuild` is **not** enabled. With `TreatWarningsAsErrors` already on, style
enforcement in the build means any rule set to `warning` fails the build — including the day
a new analyzer version tightens a heuristic. A formatting disagreement should not stop
someone from running a test. The gate arrives in Step 14 as
`dotnet format --verify-no-changes` in CI, where it cannot block local work.

Analyzer severities *do* apply whenever the analyzer runs, which resolved a standing
annoyance: the two Roslynator rules this codebase deliberately does not follow are now
silenced in one place, with the reason written down, instead of being re-argued every time
someone opens a domain file.

- **RCS1194** ("implement exception constructors") — a sealed domain exception exists to be
  thrown one way, with the message its constructor composes.
- **RCS1170** ("use read-only auto-implemented property") — private setters on entities are
  how EF Core materializes them.

---

## 10. What to take away from Step 4

1. **A security property has to reach the boundary to be real.** Step 3's tenant filter was
   correct and unproven across HTTP. The step that mattered was not writing the filter; it
   was making two tenants hit one server in a test.
2. **Put each rule in exactly one place, then decide which place.** Lifecycle transitions
   live in the aggregate — so the handler lets it throw. Status-code mapping lives in
   `ErrorResults` — so six handlers cannot disagree. Error codes live in `DocumentErrors` —
   so two of them cannot spell it differently.
3. **Duplication is acceptable when one copy is authoritative and the other is ergonomic.**
   The title length limit is in the domain and the validator. The domain wins; the validator
   exists so a form can highlight the right box.
4. **Middleware that is individually correct can be collectively wrong.** `UseExceptionHandler`
   broke malformed-JSON handling across every endpoint. End-to-end tests are not a luxury
   tier above unit tests; they are the only tier that can see the wiring.
5. **The default value of a struct is a design problem, not a detail.** `Result<T>`,
   `TenantId` and `AuthorityScore` each answer it differently — throw, flag, or accept —
   and each answer is written down next to the type.
6. **Prefer the status code that tells the truth over the one that is convenient.** 200 for
   a duplicate registration rather than 201 or 409. 404 for another tenant's document rather
   than 403. 404 for deleting what is not there rather than 204.

---

## Next

**Step 5 — AI providers through Microsoft.Extensions.AI.** `IChatClient` and
`IEmbeddingGenerator` behind the abstractions, secrets from User Secrets, and a resilience
pipeline. Two decisions open before coding: which provider is primary, and which embedding
model — the second fixes the Qdrant collection shape in Step 8, so it is expensive to
change later.
