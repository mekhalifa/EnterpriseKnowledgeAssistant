# ADR-0011: Result for expected failures, exceptions for broken rules

- **Status:** Accepted
- **Date:** 2026-11-12
- **Deciders:** project owner

## Context

Step 4 gave the application its first use cases, and each of them can fail in ways that are
entirely ordinary: the document is not there, the page number is zero, the status filter
names a stage that does not exist. None of those is a malfunction — they are the other half
of what the method does.

C# has one obvious mechanism for failure, and it is wrong for this case. An exception
thrown for "not found" costs a stack capture on a routine path, is invisible in the method
signature, and forces every caller to guess which types might come flying out. Catch blocks
then accumulate around calls that mostly succeed.

But the opposite extreme is also wrong. Step 2 built the document lifecycle as a state
machine that throws `InvalidStateTransitionException` on an illegal move. Turning every one
of those into a `Result` would mean each handler checks the status *before* calling the
aggregate — which is the state machine written down a second time, in a layer that is not
responsible for it. Copies drift. The one in the handler would still be refusing a
transition the domain had since begun to allow.

## Decision

**Use cases return `Result` or `Result<T>`.** Failures carry an `Error(Code, Message, Type)`
where `Type` is one of `Validation`, `NotFound`, `Conflict`, `Forbidden`, `Provider`.
`EKA.Api/Errors/ErrorResults.cs` maps `ErrorType` to an HTTP status in exactly one place.

**Exceptions remain for two things, and the API maps both:**

| Exception | Status | Meaning |
|---|---|---|
| `InvalidStateTransitionException` | 409 Conflict | A legitimate request arrived at the wrong moment. The detail names the refused transition. |
| `DomainException` | 500 | An invariant broke, so something upstream failed to validate. Our bug; the message goes to the log, not the response. |

Rules that follow:

- A handler **never pre-checks a lifecycle transition.** It calls the aggregate method and
  lets the domain decide. `ResetDocumentHandler` is the worked example.
- `Result` and `Result<T>` are `readonly record struct`, matching
  `docs/concepts/design-patterns.md`. Because a struct cannot prevent `default`, reading
  `Error` on a defaulted result throws and the message says so, rather than presenting
  itself as a failure carrying no error.
- Reading `Value` on a failure throws. Returning `default` would surface as a
  `NullReferenceException` several frames away with nothing pointing at the unchecked
  branch.
- `Error.Code` is a stable `feature.reason` string and part of the API contract; it travels
  as a ProblemDetails extension member so clients branch on it rather than on English prose.

## Consequences

### Positive

- Every handler signature states what can go wrong. `Task<Result<DocumentResponse>>` is a
  contract; `Task<DocumentResponse>` is a promise with a footnote.
- One mapping table from failure category to HTTP status. Six handlers cannot disagree
  about what "not found" returns.
- Expected failures cost no stack capture and no `catch`.
- Tenant mismatches return `NotFound` by construction, which is the information-leak-safe
  answer, and it is one line in `DocumentErrors` rather than a decision each handler makes.

### Negative / costs we accept

- Every handler body gains a branch, and every endpoint a `Match`. That is roughly three
  extra lines per use case.
- `Result<T>` does not compose. Chaining two fallible operations means checking the first
  before calling the second, because C# has no `do`-notation and the project has no
  `Bind`/`Map` combinators. If handlers start chaining four calls, revisit.
- Two mechanisms coexist, so "is this a `Result` or a throw?" is a question a reader must
  ask. The table above is the answer, and it has to stay accurate.
- A `readonly record struct` can be produced by `default`, which no amount of design
  prevents — only detects.

## Alternatives considered

**Exceptions for everything.** Fewer concepts, and every tutorial does it. Wins when the
failure genuinely cannot be handled locally and must unwind several frames — which is
exactly why the two domain exception types survive here. Loses for routine outcomes: it
hides the contract and makes control flow invisible.

**`Result` for everything, including illegal transitions.** Wins if the domain's state
machine is simple enough to be stated once in the handler without duplication — a document
with three states and no retry path, say. Loses here: the transition rules are already
written in `Document`, and the handler's copy would be a second source of truth for the
rule most likely to change in Step 7.

**`OneOf` or `LanguageExt`.** Real discriminated unions, exhaustive matching the compiler
enforces, and proper monadic composition. Wins when the team is already fluent in that
style, or when handlers routinely chain several fallible steps. Loses now: it is a
dependency and a learning curve bought for the same outcome a 120-line file provides, in a
project whose purpose is to make its own mechanics legible.

**Returning `null` for not-found.** Wins for a single private helper. Loses as a convention:
it cannot distinguish "absent" from "forbidden" from "malformed", and nullable reference
types make the compiler nag at every call site without adding information.

## Revisit when

- Handlers start chaining three or more fallible calls, which is the point where the
  absence of `Bind`/`Map` becomes real friction rather than a theoretical loss.
- A second aggregate's state machine needs mapping and the single `DomainExceptionHandler`
  branch no longer expresses the differences between them.
- `ErrorType` grows past about eight members, which would mean the enum has started
  describing failures rather than status codes.
