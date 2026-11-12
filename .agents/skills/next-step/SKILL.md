---
name: next-step
description: Run the next step of the EKA learning path. Use when the user says "go", "next", "continue", "start step N", or asks to begin the next piece of the Enterprise Knowledge Assistant build. Reads docs/PROGRESS.md for the current position, teaches with the why/best/alternatives format, implements, verifies, then writes the step document and updates the tracker.
---

# Run the next learning step

This project is a teaching vehicle as much as a codebase. A step is not finished when the
code compiles — it is finished when the reasoning is written down and the tracker is
accurate.

## 1. Orient before writing anything

1. Read `docs/PROGRESS.md`. The **▶ NEXT ACTION** block is authoritative — it states the
   step, its tasks, and any decision that must be settled first.
2. Read the step's entry in `docs/00-learning-path.md` for its **exit criteria**. That is
   the definition of done; do not declare the step complete until it is met.
3. Read the concept documents that entry names (`docs/concepts/*.md`). Reuse their
   vocabulary and worked examples so the codebase and the docs stay consistent.
4. If the **Decisions still to make** table in `PROGRESS.md` has an entry for this step,
   resolve it first. Recommend one option with a reason; do not start coding around an
   unsettled decision.

## 2. Teach while building

For every non-trivial piece, cover four things in the conversation:

- **What** is being built.
- **Why this approach** — the engineering reason, not a slogan.
- **What the best option is** — recommend clearly, do not merely list.
- **What else exists** and the condition under which each alternative would win.

Be concrete and honest. Name the cost of the chosen approach, not only its benefits. If a
decision is cheap to reverse, say so — it saves the user deliberation.

## 3. Respect the architecture

Dependencies point inward only. Before adding any project reference, check the matrix in
`docs/concepts/clean-architecture.md`. The architecture tests in `tests/` will fail the
build if a boundary is crossed — that is intended; fix the design, never the test.

Other standing rules:

- `EKA.Domain` references nothing — no projects, no infrastructure packages.
- Interfaces are declared by the consumer (`EKA.Application`), implemented by the
  adapter (`EKA.Infrastructure`, `EKA.AI`), wired in `EKA.Api`.
- Package versions go in `Directory.Packages.props` only — never a `Version=` in a csproj.
- New NuGet feeds need a prefix-scoped mapping in `nuget.config`; never map `*` twice.
- Authorization is enforced in retrieval, never only in the UI.

## 4. Verify

Run the `verify` skill, or at minimum `dotnet build` and `dotnet test`. The build runs with
`TreatWarningsAsErrors`, so a warning is a failure. Report real results — if something
fails or was skipped, say so plainly with the output.

## 5. Write it down — this is part of the step, not paperwork

1. **Step document** — `docs/steps/step-NN-<slug>.md`. Record what was built, the
   reasoning, the alternatives considered, anything that went wrong and what it taught,
   and a short "what to take away" section. Mirror the structure of
   `docs/steps/step-01-solution-skeleton.md`.
2. **ADR** — if a non-trivial choice was made, run the `new-adr` skill. Decisions without
   a record get re-litigated later.
3. **PROGRESS.md** — mark the step complete, move **▶ NEXT ACTION** to the following
   step with concrete tasks, update the checklist row, and add any new open decision,
   risk, or known limitation discovered along the way.
4. **Index tables** — add the step document to `docs/README.md`.

Record known limitations honestly in `PROGRESS.md`. A caveat written down is a caveat the
user can act on; an unmentioned one becomes a surprise later.

## 6. Commit

One commit per step. Subject line: `Step N: <what changed>`. Body explains the why, not
just the what. Do not push unless asked.

## Scope

Work one step at a time. Do not jump ahead to later steps of the learning path, and do not
quietly expand scope — if something out of scope looks necessary, say so and let the user
decide.
