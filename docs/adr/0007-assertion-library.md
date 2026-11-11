# ADR-0007: Shouldly for test assertions

- **Status:** Accepted
- **Date:** 2026-11-11
- **Deciders:** project owner

## Context

Step 2 introduced the first substantial test suite (179 domain tests), which forced a
choice of assertion style. `CLAUDE.md` had initially named FluentAssertions — the long-time
default in .NET — before its licence was checked.

**FluentAssertions version 8 and later requires a paid commercial licence.** Version 7.x
was the last Apache-2.0 release. For a project described as an enterprise deliverable, an
assertion library that creates a per-developer licensing obligation is a procurement
question, not a technical preference, and it is a bad one to discover during a compliance
review rather than now.

The practical requirement is modest: readable failure messages, exception assertions, and
collection comparison, in a suite that will grow to several hundred tests.

## Decision

Use **Shouldly** (MIT licence) for all test projects. Declared once in
`Directory.Packages.props` and referenced by all four test projects.

Style conventions:

- `value.ShouldBe(expected)` for equality; `ShouldBeTrue()` / `ShouldBeNull()` for the
  obvious cases.
- `Should.Throw<TException>(act)` for expected failures, asserting on the message only
  where the message is the point.
- `Should.NotThrow(act)` where the test's purpose is that a legal operation is permitted.
- Floating point always with an explicit tolerance: `ShouldBe(expected, tolerance: 1e-9)`.

## Consequences

### Positive

- MIT licensed: no obligation, no audit exposure, no per-seat cost.
- Failure messages include the asserted expression, so `document.Chunks.Count.ShouldBe(3)`
  reports what was being checked rather than only "expected 3, was 0".
- Small API surface, so there is little to learn and little to argue about in review.
- Reads close to plain English, which suits a codebase doubling as teaching material.

### Negative / costs we accept

- Less widely used than FluentAssertions, so fewer answers exist for unusual assertions.
- A third-party dependency where xUnit's built-in `Assert` would have added none.
- Different idiom from FluentAssertions, so experience with the latter transfers only
  partly.

## Alternatives considered

**FluentAssertions v8+.** The most familiar option and arguably the richest API. Rejected
on licence cost alone; nothing technical disqualifies it. It would win on a project that
already holds a licence.

**FluentAssertions v7.x, pinned.** Still Apache-2.0 and functionally close to v8. Rejected
because pinning to a superseded major version means no bug fixes and no new-framework
support, with a forced migration later anyway — paying the switching cost twice.

**AwesomeAssertions.** An MIT community fork of FluentAssertions 7, preserving the
`.Should().Be()` syntax. A genuinely reasonable choice, and the best option if preserving
that exact idiom mattered. Rejected as the weaker bet on long-term maintenance: a fork's
future depends on sustained volunteer attention, where Shouldly has an independent history
predating the licence change.

**Plain xUnit `Assert`.** Zero dependencies, always available. Rejected for readability at
this scale: `Assert.Equal(3, document.Chunks.Count)` puts expected and actual in an order
that is easy to transpose, and the failure message says nothing about what was being
tested. Still the right answer for a small project that wants no dependencies.

## Revisit when

- Shouldly's licence or maintenance status changes.
- A specific assertion proves genuinely awkward to express and appears repeatedly.
- The organization standardises on a different library across repositories.
