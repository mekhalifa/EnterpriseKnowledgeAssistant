# ADR-0013: Shape validation at the edge, stateful rules in the handler

- **Status:** Accepted
- **Date:** 2026-11-12
- **Deciders:** project owner

## Context

By Step 4 the same input is checked in up to three places, and it was not obvious that this
is correct rather than sloppy:

1. `DocumentTitle` and `FileMetadata` throw `DomainException` on bad input. They have done
   since Step 2, and they must keep doing it — the ingestion worker constructs them with no
   HTTP request anywhere in sight.
2. The API needs to answer a bad request with `400` and a list of offending **fields**, so a
   client form can highlight the right box. A `DomainException` carries a sentence and no
   field name.
3. Some rules cannot be checked without the database at all: "has this content hash already
   been registered for this tenant?"

The failure modes at either extreme are both real. Validate only in the domain and every
bad request becomes a 500 with an unhelpful body. Validate only at the edge and the worker
can create states the API would have refused.

## Decision

**Three layers, each with one job:**

| Layer | Checks | On failure | Where |
|---|---|---|---|
| Endpoint filter (FluentValidation) | Shape: required, length, range, format | `400` + `ValidationProblemDetails` keyed by property | `ValidationFilter<TRequest>` on the route |
| Handler (`Result`) | Rules needing state or configuration | `Result` failure → mapped status | The use case |
| Domain (throw) | Invariants, always | `DomainException` → `500` | Entity constructors and methods |

Rules that come with it:

- **A validator never performs I/O.** No repository, no `DbContext`, no HTTP call. A
  validator that queries cannot be unit-tested, runs before authorisation has been applied,
  silently doubles the request's round trips, and still races a concurrent insert — so it
  buys a worse version of a check the handler has to do properly anyway.
- **Validators validate the request DTO, not the command.** The command carries `TenantId`,
  which comes from the authenticated caller and is never client input; validating it would
  imply it is something a client could get wrong.
- **The edge may duplicate a domain rule. It may never exceed one.** A length limit stated
  in both places is fine: the domain keeps the authority, the edge keeps the ergonomics, and
  if they disagree the domain throws and the bug is loud. A rule the edge enforces and the
  domain does not is forbidden — it creates states the worker can reach and the API cannot.
- Validators live in the feature slice beside the command they serve, and are registered by
  assembly scan (unlike handlers, which are listed — see ADR-0012). The asymmetry is
  deliberate: an unregistered handler fails to resolve and the request errors, while an
  unregistered validator silently validates nothing.

## Consequences

### Positive

- Clients get field-level errors in the standard RFC 9457 `errors` shape, which binds
  straight to a form.
- Invalid requests never reach a database connection — the filter runs before the handler
  is constructed.
- Validation is visible in the route definition (`.WithValidation<RegisterDocumentRequest>()`),
  not buried in a handler where its absence looks identical to a handler with nothing to
  check.
- Validators are pure, so they test in microseconds with no fixtures.
- The domain stays usable from the worker, from tests, and from anything Step 7 adds.

### Negative / costs we accept

- **Length and range limits are written twice**, in `DocumentTitle` and in
  `RegisterDocumentRequestValidator`. They can drift. The drift is bounded — the domain
  wins, loudly — but it is real, and the only honest mitigation is that both reference the
  same `MaxLength` constant where one exists.
- A new DTO needs a new validator, and forgetting one means no edge validation for that
  endpoint. The domain still refuses the input; the client just gets a 500 instead of a 400.
- `FluentValidation` is a dependency in `EKA.Application`. It is a vocabulary package with
  no infrastructure attached, and the boundary test still forbids EF Core, ASP.NET Core,
  Qdrant and Hangfire there.

## Alternatives considered

**Data annotations on the DTOs.** No package, and .NET 10 validates minimal API parameters
natively. Wins for simple required/length/range rules and a team that wants zero
dependencies. Loses on conditional rules — "range-check `Authority` only when it was
supplied" — which attributes express poorly, and on testability: a validator is an object a
test can call, an attribute set is not.

**Validate inside the handler.** One fewer concept and no filter. Wins if the application
had exactly one entry point. Loses because an omitted check looks exactly like a handler
with nothing to validate, and because the handler would have to translate its own failures
into field-keyed errors, which is the filter's job done worse.

**Domain validation only.** Wins for an internal service whose only client is another piece
of our own code. Loses the moment a human-facing form needs to know which field was wrong,
which is Step 13.

**Validators that query the database.** Tempting, because "this hash already exists" feels
like validation. Loses on four counts listed above — and it does not even close the race it
appears to close. The real fix is the unique index Step 7 adds.

## Revisit when

- A rule genuinely needs state *and* field-level reporting. The answer is probably a
  `Result` failure that the API maps to a `ValidationProblemDetails` with a field key,
  which would extend `Error` rather than move the rule into a validator.
- .NET's built-in minimal API validation grows conditional rules and cross-property
  support, at which point the FluentValidation dependency may stop earning its place.
- The duplicated limits drift twice. Once is an accident; twice means the constants should
  be the only statement of the rule.
