---
name: new-adr
description: Record an architecture decision as a numbered ADR in docs/adr/. Use when a non-trivial technical choice is made or revisited — a library, pattern, tool, structure, or anything with alternatives worth documenting. Writes the file from the project template and updates both index tables.
---

# Write an Architecture Decision Record

An ADR records **one decision**: what was chosen, why, what it costs, and what was
rejected. The point is not ceremony — it is that six weeks from now "why is it like this?"
has an answer that is cheap to read and expensive to reconstruct.

## When an ADR is warranted

Write one when the choice has **plausible alternatives** and **consequences that outlive
the commit**: a library, a persistence or hosting approach, a structural rule, a security
control, a protocol.

Do not write one for routine implementation detail with no real alternative. If you cannot
name a credible option that was rejected, it is not a decision — it is just code.

## Steps

1. **Pick the number.** List `docs/adr/` and take the next free integer, zero-padded to
   four digits. Never reuse or renumber.
2. **Name the file** `docs/adr/NNNN-<kebab-case-title>.md`.
3. **Write it** using the template below.
4. **Update both indexes** — the table in `docs/adr/README.md` and the ADR table in
   `docs/README.md`.
5. **Cross-link** — if a concept document or step write-up covers the same ground, link to
   the ADR from there, and link back.

## Template

```markdown
# ADR-NNNN: <title>

- **Status:** Accepted
- **Date:** YYYY-MM-DD
- **Deciders:** project owner

## Context

What problem forced a decision? What constraints actually applied — team size, schedule,
existing stack, a failing build, a security property that must hold? Include the concrete
trigger (an error code, a measurement) when there was one.

## Decision

What was chosen, stated plainly. Include the rules that come with it.

## Consequences

### Positive

### Negative / costs we accept

Be honest here. An ADR with no costs listed is marketing, not a record.

## Alternatives considered

Each real option, and **the condition under which it would have won**. That condition is
the most valuable line in the document — it is what makes the decision reviewable later.

## Revisit when

The observable trigger that should reopen this: a measurement, a team change, a new
requirement, a version bump.
```

## Rules

- An accepted ADR is **immutable**. Changed your mind? Write a new ADR and set the old
  one's status to `Superseded by ADR-NNNN`. The history of what was believed is as useful
  as the current belief.
- Status values: `Proposed`, `Accepted`, `Superseded by ADR-NNNN`, `Deprecated`.
- Write the **condition** under which each alternative wins, not just "rejected".
- Keep it short — one page. Detail belongs in `docs/concepts/`.
