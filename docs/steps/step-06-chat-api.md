# Step 6: Prompt templates and the chat API

**Status:** ✅ Complete
**Date:** 2026-11-15
**Branch:** `step-06-chat-api` (branched from `step-05-ai-providers`)
**Result:** 394 tests, zero warnings. **The exit criterion was met twice:** once in the
integration suite with a stand-in model, and once against the real API, a real SQL Server
and the real OpenRouter model (§6).

Before this step the system could call a model and had nowhere to put the answer. This
step added conversations as something a client can use. A signed-in caller becomes a local
user; a question is assembled into a prompt that fits a budget; the question and the answer
are persisted together; and a provider outage surfaces as a 502 that leaves no trace in the
thread.

---

## 1. What was built

| Piece | Where | Purpose |
|---|---|---|
| `UserProvisioner`, `CallerIdentity` | `EKA.Application/Identity` | Just-in-time local user, tenant-scoped ([ADR-0017](../adr/0017-just-in-time-user-provisioning.md)) |
| `PromptBuilder`, `AssembledPrompt` | `EKA.Application/Conversations` | The Builder: template + history + question, fitted to a token budget |
| `ITokenCounter`, `EstimatingTokenCounter` | `EKA.Application/Abstractions/AI` | `ceil(chars / 4)`; exact provider counts preferred ([ADR-0018](../adr/0018-conservative-token-estimation.md)) |
| `ChatOptions` + validator | `EKA.Application/Conversations` | `Chat:PromptTemplateName`, `MaxPromptTokens` (6000), `MaxHistoryMessages` (20) |
| Four slices | `Conversations/StartConversation`, `AskQuestion`, `GetConversation`, `ListConversations` | Command or query, plus handler; validator for `AskQuestionRequest` |
| `ConversationEndpoints` | `EKA.Api/Endpoints` | `POST /api/conversations`, `GET /api/conversations`, `GET /{id}`, `POST /{id}/messages` |
| Migration `ConversationsAndDefaultPrompt` | `EKA.Infrastructure` | `UX_Users_TenantId_ExternalId`; seeds provider row and template `default` v1 ([ADR-0019](../adr/0019-prompt-templates-seeded-by-migration.md)) |
| Domain-generated keys, model-wide | `EkaDbContext.OnModelCreating` | Fixes the UPDATE-instead-of-INSERT bug (§5.1) |
| Ordered include | `ConversationRepository` | Messages load in conversation order |
| `ChatCompletion.Provider` | `EKA.Application` / `EKA.AI` | The domain requires provider + model on every answer |
| 32 new tests | | 21 application, 9 conversation end-to-end, 2 persistence regression |

---

## 2. A cross-tenant bug found by reading, before it could happen

**What:** `Users` had a *global* unique index on `ExternalId`, and
`FindByExternalIdAsync(externalId)` took no tenant.

**Why it mattered:** a subject id is unique within one identity provider's issuer, not
across the system. Two tenants can share an issuer. Under the development scheme every
tenant's caller is `dev-user`. The first just-in-time lookup from tenant B would have
**returned tenant A's user row**, and every conversation B created would have been written
against A's user id. No test existed, because nothing had ever provisioned a user.

**Fix:** the unique index is now `(TenantId, ExternalId)`, and the lookup takes a tenant.
This is the same discipline ADR-0009 applies to every repository: the tenant is a
parameter, applied inside the query. The integration test
`The_same_subject_in_two_tenants_is_two_users_who_cannot_see_each_other` pins it.

**The lesson:** "every tenant-scoped query takes a tenant" has to include the queries that
*find who the caller is*. Identity lookups are the easiest to forget, because they run
before there is any data to leak.

---

## 3. The prompt Builder

**What:** `PromptBuilder` produces one `System` turn (the template's instruction, never
anything else), then the history from oldest to newest, then the question rendered through
the template.

**Why a Builder:** the parts come from three places (database, aggregate, configuration),
and Step 10 adds a fourth (retrieved context). Each `With…` names one part. `Build()` is the
only place that knows the order and the trimming rule.

**Three rules worth knowing:**

1. **Single-pass placeholder substitution.** `Replace("{{question}}", q).Replace("{{context}}", c)`
   rescans what the first call inserted. A user typing `{{context}}` would get document text
   substituted *into their own words*. From Step 10, a document containing `{{question}}`
   would be rewritten the same way. One regex pass over the template replaces only
   placeholders the template contained. A test pins it.
2. **Trim oldest-first, then drop an orphaned answer.** History is added newest-first while
   it fits the budget and the message cap. If the oldest kept message is an answer whose
   question was trimmed, it goes too. Otherwise the model sees an unprompted assertion and
   treats it as established fact.
3. **Exact beats estimated.** An assistant message stores the provider's output count, and
   the builder uses it. Only user messages are estimated, and they are usually the short
   part of a conversation.

A question that does not fit even with no history (instruction + question > budget) is a
400, `conversation.question_too_long`, checked in the handler. It depends on the active
template, so the edge validator cannot know it. The edge validator still rejects anything
over 8000 characters before a query is spent on it (ADR-0013's split).

---

## 4. A turn is atomic

`AskQuestionHandler` builds the prompt and calls the model **before touching the
aggregate**. Only then does it add the question and the answer, and save once.

- **Provider fails:** the 502 goes back with nothing written. The user retries the same
  question, and the thread is unchanged.
- **The alternative,** saving the question and then calling the model, leaves an
  unanswered question behind every outage. The next prompt would replay it, and the model
  would answer two questions at once.
- **The cost:** a question is not durable until it is answered. In an outage the question
  is lost, but it is still on the user's screen. That is the lesser harm.
- **No locks during the model call:** EF Core opens a connection per query and per save, so
  a 30-second model call holds nothing in SQL Server.

The handler does **not** pre-check "is the conversation archived" or "does an answer follow
a question". The aggregate enforces both (ADR-0011).

---

## 5. What went wrong

### 5.1 `UPDATE` instead of `INSERT`: EF Core's Guid-key convention

**Symptom:** every ask returned 500 in the end-to-end tests, with
`DbUpdateConcurrencyException: expected to affect 1 row(s), but actually affected 0`.

**Cause:** by convention EF Core treats a `Guid` primary key as **store-generated on add**.
So when it discovers a new entity whose key is already set, it concludes the row already
exists. A new `Message` with a domain-generated `Guid.CreateVersion7()` id, added to a
*loaded* `Conversation`, was tracked as **Modified**, and `SaveChanges` sent an `UPDATE`
that matched nothing.

**Why Step 3 never saw it:** its round-trip tests create brand-new aggregates, and when the
root is `Add`ed, EF marks the whole graph Added. The bug needs load → change → save, which
is the shape of every real use case and of no test until now.

**Fix:** in `OnModelCreating`, once for the whole model, every `EntityAudit<Guid>` key is
`ValueGenerated.Never`. This matches the codebase's rule that cross-cutting mapping lives
in one place (as with the value converters). The schema is unchanged; only the model
snapshot records it.

**Proving the tests guard it:** `ChildInsertTests` covers a message added to a loaded
conversation, and **chunks added to a loaded document**. With the fix disabled, both fail
with exactly this exception. **Step 8 would have hit the chunk case.**

### 5.2 A test that passed while its request was a 500

`The_migration_seeded_template_is_what_the_model_is_instructed_with` passed throughout
§5.1, because it asserted only on the prompt the stand-in model received, and the model is
called *before* the save. It now asserts the status code. The general rule: **assert the
outcome the user sees, not just a side effect that happens earlier.**

### 5.3 An incremental build served stale binaries, after my own sabotage test

To prove `ChildInsertTests` guard the fix, I copied `EkaDbContext.cs` aside, disabled the
fix, built and ran the tests (both failed, as intended), then **moved the copy back**. The
copy carried a timestamp older than the build output, so MSBuild decided `EKA.Infrastructure`
was up to date. The next real run of the API failed with §5.1's exception while the source
was correct.

**Caught by the real end-to-end run** (§6): the stack trace pointed at code that no longer
existed. Fixed by touching the file and rebuilding, then rerunning all 394 tests on real
binaries. **The lesson:** incremental builds trust timestamps. Restoring a file must not
restore an old modification time. `git stash` / `git checkout` don't, but `cp` followed by
`mv` does.

### 5.4 `localhost` is not `127.0.0.1`

Migrating the demo database timed out against `Server=localhost,1433`. The compose file
publishes SQL Server on **127.0.0.1 only** (a Step 3 security choice), and on this machine
`localhost` resolves to IPv6 `::1` first. Use `127.0.0.1` in local connection strings.

---

## 6. The exit criterion against the real model

The real API, a throwaway database migrated with both migrations, OpenRouter
`openai/gpt-4o-mini`, three questions in one conversation:

| # | Question | History | Prompt tokens | Answer (abridged) |
|---|---|---|---|---|
| 1 | What is a vector database? | 0 | 118 | "A type of database designed to store, manage, and search high-dimensional vectors…" |
| 2 | How many days of annual leave do I get here? | 2 | 180 | "I cannot see your organization's documents yet, so I cannot provide information on annual leave policies." |
| 3 | Summarize my two questions so far | 4 | 218 | "You asked about the definition of a vector database and … annual leave days" |

Question 2 is the seeded v1 instruction doing its job: the model declined to invent a
policy. Question 3 proves the windowed history reaches the model. Read-back returned six
messages in order, each answer carrying its exact output tokens and model. The demo
database was dropped afterwards.

---

## 7. Decisions inside the code worth knowing about

- **Reads never provision.** A GET by a caller with no local user is a 404 or an empty list,
  never a write.
- **The development scheme now issues `email` and `name`.** The email is
  `<subject>@dev.invalid`; `.invalid` is reserved (RFC 2606), so it can never reach a
  mailbox or pass for a real one.
- **Asking returns 200, not 201.** One request creates two resources, neither addressable
  on its own.
- **`AskQuestionResponse` reports how the prompt was built:** history included and omitted,
  template version, provider-counted prompt tokens. A non-zero `HistoryMessagesOmitted`
  tells a client the answer was written without the whole thread in view.
- **`PromptBuilder` is transient.** It accumulates state in fields, so a shared instance
  would leak one prompt's history into another.
- **Provider name is the endpoint host** (`openrouter.ai`), recorded on every answer next
  to the model the provider *reported* (`openai/gpt-4o-mini`).

---

## 8. Alternatives, and when each would win

| Choice | Chosen | Alternative wins when… |
|---|---|---|
| Local identity | JIT provisioning ([ADR-0017](../adr/0017-just-in-time-user-provisioning.md)) | Directory sync (SCIM): access must be granted before first sign-in |
| Token sizes | Estimate + exact output ([ADR-0018](../adr/0018-conservative-token-estimation.md)) | Real tokenizer: model pinned to one vendor, budgets near the window |
| Template seeding | Migration `InsertData` ([ADR-0019](../adr/0019-prompt-templates-seeded-by-migration.md)) | Startup seeder or admin endpoint: prompts change faster than releases |
| Turn persistence | Atomic: call, then save both | Save question first: questions must survive outages, for example an audit requirement |
| Key generation | Domain ids, `ValueGenerated.Never` model-wide | Store-generated keys: ids never needed before the insert |
| Model in tests | Recording stand-in behind the port | Real model in CI: an evaluation suite, not a correctness suite (Step 11) |

---

## 9. What to take away from Step 6

1. **Identity lookups are tenant-scoped queries too.** The bug in §2 was in the one query
   that runs before there's any data to protect.
2. **Test the shape real use cases have: load, change, save.** A test that creates the
   whole graph at once can't see a bug that only happens when a child joins an existing
   parent.
3. **Assert what the user sees.** A prompt recorded by a stand-in model proved the code ran,
   not that it worked.
4. **Run the real thing once.** The integration suite was green, and a stale build still
   reached the running API. Ten minutes against the real stack found it.
5. **Order is data.** An unordered `Include` is a shuffled conversation, and a shuffled
   conversation is a wrong prompt.

---

## Next

**Step 7: document ingestion.** Upload with validation and limits, a storage abstraction,
text extraction with page and section metadata, status transitions, idempotency, and the
hand-off to the worker. The decisions waiting are the background-job engine, file storage,
and the `(TenantId, ContentHash)` unique index that closes the registration race from Step 4.
