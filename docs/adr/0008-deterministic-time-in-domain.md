# ADR-0008: The domain never reads the clock

- **Status:** Accepted
- **Date:** 2026-11-11
- **Deciders:** project owner

## Context

Domain entities record when things happened: `Document.ExtractedAt`, `IngestedAt`,
`ModifiedAt`, and every `IDomainEvent.OccurredAt`. Something has to supply those instants.

The obvious approach — `DateTimeOffset.UtcNow` inside the entity — makes every
time-dependent rule untestable. A test can only assert "roughly now", which means
assertions become tolerance comparisons that pass whether the logic is right or wrong. It
also makes certain rules impossible to test at all: "a document that has failed three
times in an hour is escalated" needs control over the clock, not patience.

`EKA.Domain` must also reference nothing infrastructural
([ADR-0002](0002-clean-architecture-layering.md)), so a custom `IClock` abstraction would
have to live in the domain itself and be implemented outside it.

## Decision

**No domain type reads the system clock.** Every method that records an instant takes it as
a parameter:

```csharp
document.BeginExtraction(now);
document.Fail("PDF is password protected", now);
chunk.MarkEmbedded("text-embedding-3-large", now);
```

The caller supplies the value. In Steps 4 onward the application layer obtains it from
`TimeProvider.System` (part of the BCL since .NET 8, so no new dependency), and tests pass
a fixed `DateTimeOffset`.

`CreatedBy` and `ModifiedBy` follow the same principle for a related reason: the domain has
no notion of "who is logged in", so those are populated by an EF Core interceptor in
`EKA.Infrastructure` at Step 3.

## Consequences

### Positive

- Tests assert exact timestamps — `document.ModifiedAt.ShouldBe(TestData.Later)` — rather
  than tolerance windows.
- Time-dependent rules become testable without waiting or mocking statics.
- One operation gets one timestamp. Reading the clock three times within a single state
  change produces three slightly different instants, and the resulting ordering anomalies
  are maddening to debug in audit trails.
- No clock abstraction to define, implement, register, or fake.
- The domain stays dependency-free, satisfying ADR-0002 without special pleading.

### Negative / costs we accept

- Every lifecycle method carries an extra parameter, which is visible noise in signatures.
- Callers can pass a wrong or inconsistent instant; the domain cannot detect a lie about
  the current time. Mitigated by the application layer being the only caller in practice.
- A reader new to the codebase may expect `UtcNow` and wonder where it went — hence the
  explanatory remarks on `EntityAudit<TId>`.

## Alternatives considered

**`DateTimeOffset.UtcNow` inside entities.** Simplest to write and the most common pattern
in the wild. Rejected: it trades away testability of exactly the rules most worth testing.

**An `IClock` / `ITimeProvider` abstraction injected into entities.** Rejected on two
counts. Entities constructed by EF Core materialization cannot easily receive injected
services, and giving an entity a service dependency pushes it toward being a service with
state — the opposite of what the domain model is for.

**`TimeProvider` passed into each domain method instead of a `DateTimeOffset`.** Nearly
equivalent and standard in modern .NET. Rejected as strictly weaker here: it hands the
entity the ability to read the clock several times within one operation, reintroducing the
inconsistent-timestamp problem. Passing the instant makes "one operation, one timestamp"
structural. `TimeProvider` is still the right thing for the *application* layer to hold.

**Set timestamps exclusively in an EF interceptor.** Correct for `CreatedAt`/`ModifiedAt`
and we do exactly that for the `By` fields. Insufficient for domain events and for
business-meaningful instants like `IngestedAt`, which participate in rules the domain
enforces and therefore cannot be a persistence-time concern.

## Revisit when

- A domain rule needs duration or scheduling logic complex enough that threading instants
  through becomes genuinely unwieldy.
- The signature noise measurably hurts readability once there are many more methods.
