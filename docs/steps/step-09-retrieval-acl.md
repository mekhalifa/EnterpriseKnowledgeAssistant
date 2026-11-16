# Step 9: Vector retrieval with access control

**Status:** ✅ Complete
**Date:** 2026-11-16
**Branch:** `step-09-retrieval-acl` (branched from `step-08-chunking-embeddings`)
**Result:** 578 tests (2 of them billed smoke tests), zero warnings. **The exit criterion was
met twice:** in the integration suite (real API, real worker, real SQL Server and Qdrant,
embedding model faked), and on the real stack (§6) with real OpenRouter embeddings.

> *Queries return relevant chunks and never content outside the caller's access scope.*

Step 8 left vectors in Qdrant that nobody could ask anything of. Now a caller asks a
question: it is embedded, the nearest chunks are found **among those the caller may read**,
every hit is re-checked against SQL Server, and the text comes back ranked, with the
provenance a citation needs. Along the way, documents gained an access list. "The caller's
access scope" became a real thing, enforced in search and in every document endpoint.

---

## 1. What was built

| Piece | Where | Role |
|---|---|---|
| `AccessPrincipal`, `DocumentAccess`, `AccessScope` | `Domain/Access` | Who a grant names, who may read a document, who is asking (§2). |
| `Document.AccessKeys`, `Access`, `IsAccessibleTo`, `ChangeAccess` | Domain | The access rule, written once. New documents are tenant-wide. |
| `ChunkFilter` (+ `TenantIs`, `GrantedToAny`, `EmbeddingModelIs`, `AllOf`) | `Application/Retrieval` | The Specification (§3). |
| `IVectorStore.SearchAsync`, `SetDocumentAccessAsync`; `ChunkVector.AccessKeys` | Application port | Search with the filter inside; replace a document's keys. |
| `IChunkReader`, `ChunkPassage`, `ChunkKey` | `Application/Abstractions/Retrieval` | The SQL re-check port (§4). |
| `ChunkRetriever`, `RetrievalOptions` | `Application/Retrieval` | Embed → search → re-check → rank. Shared with Step 10. |
| `SearchChunks` slice | `Application/Retrieval/SearchChunks` | `POST /api/search`. |
| `SetDocumentAccess` slice | `Application/Documents/SetDocumentAccess` | `PUT /api/documents/{id}/access`. |
| `QdrantFilterTranslator`; search and payload update in `QdrantVectorStore` | `Infrastructure/VectorStore` | The Qdrant interpretation; `access` and `embedding_model` payload indexes. |
| `ChunkReader` | `Infrastructure/Persistence/Repositories` | One projected query: tenant, access, Completed, keys. |
| `Documents.Access` (JSON) + migration `DocumentAccess` | Infrastructure | Primitive collection. Existing rows backfilled to `["tenant"]` (§5.1). |
| `groups` claim, `X-EKA-Groups` header, `RequireAccessScope()` | `EKA.Api/Security` | The scope comes from the principal, never the body. |

Also changed: every document command and query (`Get`, `List`, `Rename`, `Reset`, `Delete`,
`Upload`) now carries an `AccessScope` instead of a bare `TenantId`. `DocumentResponse` gains
`Access`.

---

## 2. Access as one rule over strings

**What.** A principal is a canonical string: `tenant`, `user:<subject>`, `group:<id>`. A
document holds a non-empty list of them. A caller holds `[tenant, user:<me>, group:<g>…]`.
**Access = same tenant AND the two lists intersect.**

**Why this shape.** The rule has to hold in three places that speak different languages:
C# (the domain, and handlers that load one document), SQL (the list and the re-check) and
Qdrant (the search filter). A set intersection over identical strings is a rule all three
express natively. SQL uses `OPENJSON … IN`, Qdrant uses `match any`, and C# uses `Any(Contains)`.
They cannot drift on edge cases such as case or ordering, because there are none.

**Rules worth their exceptions:**

- **Never empty.** A document nobody may read cannot be recovered by anyone, either.
- **`tenant` stands alone.** `[tenant, user:alice]` looks restricted and is not. Someone
  "narrowing" it later by removing `tenant` would lock out everyone but Alice.
- **Not trimmed into validity.** `user: alice` is refused, not repaired. A repaired grant is a
  grant nobody wrote.
- **The tenant is checked even in `IsAccessibleTo`,** although repositories already filter
  on it. Group ids are unique only inside one identity provider, so `group:hr` in tenant B
  must not open tenant A's document. A test pins it.

**Who may change access?** Anyone who can read the document, until Step 12 brings roles.
The rule that matters now is the negative one: a caller who cannot read a document gets 404,
for changing its access too, so they cannot grant themselves in.

**Best option:** this one, for a list of a few principals that is always read whole.
**Alternative:** a `DocumentGrants` table wins once someone needs "everything group X can
read" as a fast query (an admin report). See [ADR-0027](../adr/0027-document-acl-canonical-keys.md).

---

## 3. The Specification: what a search may consider

```csharp
ChunkFilter.InTenant(caller.TenantId)
    .And(ChunkFilter.GrantedToAnyOf(caller.Keys))
    .And(ChunkFilter.EmbeddedWith(embeddings.Model));
```

**What.** Small immutable condition objects, combined with `And`, with two interpretations:
`IsSatisfiedBy` (in memory: fakes and tests) and `QdrantFilterTranslator` (Qdrant's filter).

**Why a pattern here.** This is the one query where a missing condition is a breach. As
objects, each condition is a unit under test, and the translation is written once.

**Three design choices inside it:**

1. **`And` only, no `Or`.** Every condition narrows. A top-level `Or` could widen ("tenant A
   *or* group hr" reads group hr in every tenant). The one disjunction access needs, "any of
   my principals", is a single leaf.
2. **The tenant is structural.** `PinnedTenant` finds it at the top level. The adapter refuses
   a filter without one, before any I/O, so a forgotten tenant is a loud exception rather
   than a quiet cross-tenant search. Tested against the real adapter.
3. **Exhaustive translation.** The translator's switch throws on an unknown condition. A
   translator that skipped what it did not recognize would turn an unknown restriction into
   none.

**Why the embedding model is a condition.** The collection already refuses vectors of another
*size*. Two models of the same size place meanings differently, and comparing across them
gives scores that look normal and mean nothing, for example during a re-embedding.

**Why status is *not* a condition.** Status lives in SQL, and the payload would be one more
copy to keep in step. It is checked in the re-check (§4).

---

## 4. Two checks, and SQL decides

```text
question ─► embed ─► Qdrant: nearest 2K WHERE tenant ∧ access ∧ model   (filter INSIDE the HNSW walk)
                         │ ids + scores only
                         ▼
                   SQL: chunks WHERE tenant ∧ access(current) ∧ Completed ∧ (doc, index) ∈ hits
                         │ text + provenance
                         ▼
                   rank: score ↓, document id, chunk index → take K
```

**Filter inside, not after.** Post-filtering the top 10 returns fewer than 10, and possibly
none, whenever other tenants' chunks are nearer. The gap itself is information. Qdrant applies
the filter during the graph walk, so the caller gets *their* 10 nearest. On the real stack,
Bob's search for the restricted expense policy logged `0 vector hits`: nothing was found to
be dropped.

**Why re-check at all.** The payload is a copy, and copies lag: an access change in flight,
a lost update, an ingestion that wrote old keys. The payload also knows nothing about status.
The re-check catches:

| Situation | Payload says | SQL says | Result |
|---|---|---|---|
| Access narrowed, payload update lost | `tenant` | `user:olivia` | Dropped for Bob (tested end to end) |
| Document mid-re-ingestion | vectors exist | `Embedding` | Dropped (tested end to end) |
| Row deleted, vector clean-up failed | vectors exist | no row | Dropped (tested end to end) |
| Access widened, payload not yet | `user:olivia` | `tenant` | Not found yet: safe, under-returns |

**Written independently.** The re-check is a LINQ query, not a second translation of the
`ChunkFilter`. A bug in one is then not automatically a bug in the other. That is defence in
depth in the literal sense.

**Over-fetch.** The store is asked for `K × CandidateMultiplier` (2) candidates, so dropped hits
do not shrink the answer below K. A test hides two better-scoring chunks from Bob, and he
still gets two.

**Changing access writes the copy first** (payload, then SQL). Every failure state is safe
for readers either way, because SQL re-checks. Copy-first also means a failure leaves *nothing*
changed and shows the caller an error to retry, instead of a reported success whose payload
never caught up.

---

## 5. What went wrong

### 5.1 The generated migration would have locked every existing document

`dotnet ef migrations add` produced:

```csharp
migrationBuilder.AddColumn<string>(name: "Access", table: "Documents", nullable: false, defaultValue: "[]");
```

`"[]"` is an empty access list: readable by nobody. Worse, the domain refuses an empty list,
so loading any pre-existing document would have thrown in `Document.Access`. Nothing in the
build or the unit tests could see it, because both start from fresh data.

**Fix:** add the column nullable, `UPDATE … SET [Access] = N'["tenant"]'`, then make it NOT
NULL. No default constraint is left behind, so an insert that forgets the column fails
instead of silently becoming tenant-wide. A new test migrates a fresh database to
`DocumentUploadAndIngestion`, inserts a document the old way, migrates forward, and asserts
`["tenant"]` and no default constraint.

**Lesson:** read every generated migration. EF's default for a new non-nullable column is
"the CLR default". For a security column, that default is a decision, and EF made it.

### 5.2 "Unauthenticated" is not what it looks like in Development

The first search 401 test sent no headers and got 200. The development scheme fills in a
default tenant and subject when headers are absent, by design (ADR-0014). The existing suite
already tests 401 with a *malformed* identity, and the test now does the same. Recorded,
because the next person writing an auth test will hit it.

### 5.3 Small ones

- `[InlineData(new string[0])]` does not compile as an attribute argument: a lone array is
  read as `params object[]`. Principal lists in theories are now comma-joined strings.
- The score floor at exactly 0 would have depended on whether cosine −1 rounds to
  −1.0000001 in single precision. The adapter sends no threshold when the floor is 0.

---

## 6. The exit criterion on the real stack

API on Kestrel, worker as its own process, SQL Server and Qdrant from compose, a throwaway
database `EKA_Step9Demo` (all migrations applied from empty) and collection
`eka_step9_demo`, **real embeddings** (`openai/text-embedding-3-small` through OpenRouter).
Three Markdown policies in tenant A, with the expense policy restricted to
`group:finance, user:olivia`. The leave policy, byte-identical, in tenant B.

| Caller | Question | Result |
|---|---|---|
| A / bob | How many vacation days do I get each year? | 0.741 *Annual leave* #0, 0.693 *Sick leave* #1 |
| A / bob | Can I take a taxi home after a late meeting? | **no hits.** Log: `0 vector hits` (filtered inside Qdrant) |
| A / carol + finance | same | 0.711 *Travel expenses* #0 |
| A / bob | What do I do if my laptop is stolen? | 0.798 *Laptops*, 0.625 *Passwords* |
| B / bruno | How many vacation days…? | 0.741, 0.693: **tenant B's own document id** |
| B / bruno | taxi? | no hits |
| A / bob | What is the capital of Australia? | no hits (empty success) |

The Qdrant payload of an expense point carried `"access":["group:finance","user:olivia"]`.
Payload indexes: `tenant_id`, `document_id`, `access`, `embedding_model`. Afterwards the
processes were stopped, the database dropped, the collection deleted and the files removed.

What it shows about the **floor**: related passages scored 0.62 to 0.80, and an unrelated
question nothing. 0.6 admitted "Passwords" for a stolen-laptop question, which is weakly
related. Step 11 sets the floor from data (ADR-0028).

---

## 7. Decisions inside the code worth knowing about

- **`ChunkRetriever` is a service, `SearchChunksHandler` a thin slice.** Step 10's answer
  generation calls the retriever directly, so search and answers cannot retrieve differently.
- **"No evidence" is an empty success, a provider failure an `ai.*` error.** Step 10 must tell
  "nothing relevant" from "could not search".
- **Ties are broken by document id, then chunk index.** Two identical requests return the
  same list, which evaluation and any future cache depend on.
- **The question is embedded as itself.** Passages are prefixed with title and headings
  (Step 8); a question already says what it is about. Both formats live side by side in
  `EmbeddingInput`, because an instruction-tuned model would change both together.
- **`POST /api/search`, not GET.** Questions can be as sensitive as documents, and URLs end up
  in logs, history and `Referer` headers.
- **The question is never logged.** The retrieval log line has counts only: hits, dropped,
  returned. A rising "dropped" count means the payload copy is drifting.
- **Uploading bytes you cannot read is `409 document.duplicate_not_accessible`.** It names
  nothing, but confirms the bytes exist in the tenant. That is the smallest disclosure that
  keeps the per-tenant idempotency index.
- **A JWT with too many groups** (Entra's "groups overage") gives the caller *no* groups here:
  less access, never more. Resolving it is Step 12's.

---

## 8. Alternatives, and when each would win

| Choice | Alternative | It wins when |
|---|---|---|
| ACL now | Tenant-only until Step 12 | No document is ever narrower than its tenant |
| JSON column of keys | `DocumentGrants` table | An admin needs "all documents group X can read" fast |
| Filter inside the search | Post-filter results | Never, for authorization |
| SQL re-check | Trust the payload | The payload is the system of record (it is not here) |
| Copy-first access update | SQL-first | Payload updates are asynchronous and retried by a job |
| Dense only | Hybrid with BM25 (RRF) | Step 11 shows misses on identifiers and rare terms |
| Configured K and floor | Per-request | An exploratory search UI, with no context budget behind it |
| Empty = success | 404/422 for no evidence | Never: a normal outcome is not a failure |

---

## 9. What to take away from Step 9

1. **Authorization in retrieval means *inside* the query.** Filtering afterwards is a
   different, leakier operation that happens to use the same words.
2. **A copy can filter, and only the source of truth can decide.** Let the copy make search
   efficient, and re-check every result against the authority. Then a stale copy can only hide
   things, never reveal them.
3. **One rule, many translations: make the rule trivially translatable.** An intersection of
   canonical strings has no edge cases for SQL, Qdrant and C# to disagree on.
4. **Make the dangerous omission structural.** A filter without a tenant cannot be executed,
   and a command without an `AccessScope` cannot be constructed.
5. **Read generated migrations,** especially defaults for new non-nullable columns. EF picks
   one, and for a security column it picked "nobody".

---

## Next

**Step 10: RAG answer generation with citations.** `AskQuestionHandler` calls
`ChunkRetriever`, assembles the retrieved chunks into a bounded context with the
`PromptBuilder`, instructs the model to answer only from them and to refuse otherwise, and
persists citations (document, chunk, pages, section) on the answer. The empty-result contract
from this step is what makes "refuse rather than fabricate" decidable.
