# ADR-0012: Vertical slices with direct handler injection, no mediator

- **Status:** Accepted
- **Date:** 2026-11-12
- **Deciders:** project owner

## Context

The application layer needed a shape. Two questions had to be answered together, because
the usual answer to the first drags the second along with it.

**How is the layer organised?** By technical kind — `Commands/`, `Handlers/`, `Validators/`,
`Services/` — or by feature, one folder per use case containing everything that use case
needs?

**How does an endpoint reach a handler?** By injecting the handler directly, or by sending
a message through a mediator?

The second question is usually settled by habit. MediatR is near-ubiquitous in .NET Clean
Architecture samples, and it is worth being precise about what it actually provides, which
is not "decoupling" — an endpoint that sends `RegisterDocumentCommand` is exactly as coupled
to that use case as one that injects `RegisterDocumentHandler`. What it provides is a
**pipeline**: one place to wrap every handler in logging, validation, transactions,
caching or retries. That is genuinely valuable, and this project does not have it yet.

MediatR's licensing is a second-order consideration (v12+ is commercially licensed for
organisations above a revenue threshold, and free alternatives exist), but the decision here
does not rest on it.

## Decision

**Organise by feature.** One folder per use case under `EKA.Application/Documents/`, each
containing its command or query, its handler, and its validator:

```text
Documents/
  RegisterDocument/   RegisterDocumentCommand.cs, …Handler.cs, …RequestValidator.cs
  GetDocument/        GetDocumentQuery.cs, GetDocumentHandler.cs
  ListDocuments/      …
  DocumentErrors.cs   DocumentMapping.cs   StorageKeys.cs   ← shared by the feature
```

**Inject handlers directly.** Endpoints take `RegisterDocumentHandler` as a parameter.
Handlers are registered by hand in `EKA.Application/DependencyInjection.cs` — listed, not
scanned for.

**Commands carry what only the server knows.** Every command's first parameter is
`TenantId`, supplied by the endpoint from the authenticated caller, never bound from a
request body. Request DTOs live in `EKA.Contracts`; commands are internal and may change
freely.

**Queries do not take `IUnitOfWork`.** A read-side handler cannot write, because it has
nothing to write with. That is the whole of "CQRS-lite" here — no second database, no event
bus, just two shapes with different dependencies.

## Consequences

### Positive

- A change to one use case touches one folder. The alternative — editing `Commands/`,
  `Handlers/` and `Validators/` for a single feature — is three directories and a merge
  conflict waiting for a colleague.
- Navigation is direct. "Go to definition" on a handler lands on the handler, not on
  `IRequest<T>` and a runtime registry.
- The DI registration list is a readable inventory of what the application does, and it
  fails at compile time when it goes stale. Assembly scanning would make a deleted handler
  compile and a mistyped namespace silently unregister one.
- No pipeline means no invisible behaviour: what a request does is what the endpoint and
  the handler say it does.
- Deleting a feature means deleting a folder and six lines.

### Negative / costs we accept

- **Cross-cutting behaviour has nowhere to live yet.** Adding "log every use case with its
  duration" today means editing every handler. That is the cost, stated plainly, and it is
  the trigger for revisiting this.
- Handler registration is manual. Six lines now; thirty by Step 10.
- Endpoint signatures name concrete handler types, so a handler rename is a wider edit than
  a message rename would be.
- Feature folders tempt duplication — two features each growing their own near-identical
  mapper. `DocumentMapping.cs` sitting beside the slices rather than inside one is the
  counter-habit.

## Alternatives considered

**MediatR (or a free equivalent such as Mediator or MassTransit's mediator).** Wins the
moment the same behaviour must apply to every handler without being written into every
handler — a transaction scope, structured timing, an authorisation check, an idempotency
cache. That day is plausible by Step 7, when ingestion makes several handlers transactional
and the worker starts invoking the same use cases the API does. Migration is mechanical
precisely because handlers are already `Command → Result` shaped: implement `IRequest<T>`,
change six registrations, replace a handler parameter with `ISender`.

**Organise by technical kind.** Wins for a small application where every use case touches
the same few types, and for teams whose tooling assumes the layout. Loses at this size:
locality of change matters more than symmetry of folders once there are more than a handful
of features.

**No application layer at all — logic in the endpoints.** Wins for a genuine CRUD service
over one table. Loses here immediately: the ingestion worker invokes the same use cases
with no HTTP request in sight, and logic living in an endpoint is logic only an HTTP request
can reach.

**Scan the assembly to register handlers.** Wins when handlers number in the hundreds and
the list becomes maintenance rather than documentation. Loses now, for the reason given
above: the failure mode of scanning is silence.

## Revisit when

- A third handler needs the same wrapper — logging, timing, transaction scope,
  idempotency. Two is a coincidence; three is a pipeline.
- The handler registration list passes roughly thirty entries and reads as maintenance
  rather than inventory.
- The ingestion worker and the API need the same use case wrapped differently, which a
  pipeline expresses cleanly and direct injection does not.
