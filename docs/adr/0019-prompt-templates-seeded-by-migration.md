# ADR-0019: Prompt template versions ship as migrations

- **Status:** Accepted
- **Date:** 2026-11-15
- **Deciders:** project owner

## Context

Answering a question needs an active `PromptTemplate`. The domain already versions
templates (`Name` + `Version`, at most one active per name, enforced by a filtered unique
index). Each template carries a required foreign key to an `AIProviders` row, although since
Step 5 the live provider comes from configuration, not that table. Something has to put
template v1, and the provider row its key requires, into every database: developer
machines, the test container, production.

## Decision

- Migration `ConversationsAndDefaultPrompt` inserts **one `AIProviders` row** (`openrouter`)
  and **template `default` v1, active**, using `migrationBuilder.InsertData`, not `HasData`.
- v1 is deliberately **ungrounded**: its user template is just `{{question}}`. Its system
  instruction says that document search is not connected and forbids inventing policies,
  figures or citations.
- **A new prompt version is a new migration** that inserts v(n+1) and moves `IsActive`.
- `AskQuestionResponse.PromptTemplateVersion` reports which version answered.

## Consequences

### Positive

- Every environment, including the integration-test container, has the same prompt with no
  startup code writing to the database. The application never boots into "no template".
- Prompt changes are reviewed, versioned and rolled back exactly like schema changes.
- `InsertData` keeps the row out of the model snapshot, so an operator flipping `IsActive`
  does not fight later migrations.

### Negative / costs we accept

- Changing a prompt needs a migration and a deploy. That is slow for prompt iteration, which
  is often fast-paced work.
- The seeded provider row duplicates values that configuration owns. It records what the
  prompt was written for. It does not choose what answers, which a reader could misread.
- The template version is reported but **not persisted on the message**. Step 11's
  evaluation, which compares versions, will need it stored.

## Alternatives considered

- **An idempotent startup seeder.** It wins when prompts are edited by operators without a
  release. It costs database writes at boot, a race between instances, and must cope with an
  unreachable database.
- **`HasData` in the model.** It wins for true reference data that never changes at runtime
  (country codes). It is wrong here because activation state is changed at runtime.
- **Prompts as files or configuration.** It wins if prompts are never versioned in the
  database. It would leave the domain's versioning and "one active" invariant without a home.

## Revisit when

- Prompt iteration is frequent enough that a deploy per change hurts. At that point, an
  admin endpoint to create and activate versions.
- Step 11 needs answers attributable to a template version: persist it on `Message`.
