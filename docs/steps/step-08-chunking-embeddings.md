# Step 8: Chunking and embeddings

**Status:** ✅ Complete
**Date:** 2026-11-16
**Branch:** `step-08-chunking-embeddings` (branched from `step-07-document-ingestion`)
**Result:** 477 tests, zero warnings. **The exit criterion was met twice:** in the
integration suite (the real API and the real worker composition with a real Qdrant, embedding
model faked), and on the real stack (§6) with real OpenRouter embeddings.

> *An ingested document yields ordered chunks and matching vectors; re-ingesting the same
> document is safe.*

At the end of Step 7 a document stopped at `Chunking`, with its text in a JSON artifact.
Now the same worker job carries on. It cuts the text into chunks that respect pages and
headings, embeds them in bounded batches, and writes the vectors to Qdrant, keyed so that
writing them again overwrites instead of duplicating. The document is `Completed` only when
every chunk has a vector, and deleting it removes the row, the files and the vectors.

---

## 1. What was built

| Piece | Where | Role |
|---|---|---|
| `IVectorStore`, `ChunkVector` | `Application/Abstractions/Ingestion` | The port: upsert, delete a document's vectors, count them. Every method takes the tenant. |
| `IChunkingStrategy`, `SectionAwareChunker` | `Application/Documents/Indexing` | The Strategy and its default algorithm (§2). Pure. |
| `EmbeddingInput` | same | What is actually embedded: title + heading trail + chunk text. |
| `IndexingOptions` (+ validator) | same | `Indexing` section: target 500, overlap 75, batch 64, 2 batches in flight. |
| `IndexDocumentHandler` | `Application/Documents/IndexDocument` | Chunking → Embedding → Completed, resumable (§3). |
| `Document.AwaitsChunking`, `AwaitsEmbedding`, `ChunksAwaitingEmbedding`, `RecordEmbeddings` | Domain | The aggregate says which stage is owed and which chunks lack a vector. |
| `CompleteIngestion` (stricter) | Domain | Refuses unless the chunks are loaded and **every one** has a vector. |
| `QdrantVectorStore`, `VectorStoreOptions` | `Infrastructure/VectorStore` | The adapter: derived point ids, lazy collection, tenant-indexed payload (§4). |
| `IngestionJobs.IngestAsync` | `Infrastructure/Jobs` | One job runs extraction and indexing. `ExtractTextAsync` stays as a forwarder. |
| `IEmbeddingService.Model` | Application port + `EKA.AI` | The configured model name, recorded on every chunk. |
| Worker composition | `EKA.Ingestion.Worker` | Now calls `AddAi` and binds `Indexing`. Needs its own `Ai:ApiKey`. |
| Qdrant | `deploy/docker-compose.yml`, test fixture | `qdrant/qdrant:v1.20.0`, loopback-only ports, named volume. |

Also changed:

- `IIngestionQueue.EnqueueExtractionAsync` → `EnqueueIngestionAsync`. The job now does more than extract.
- `ResetDocumentHandler` enqueues the document straight away. Before, a reset document waited up
  to five minutes for the sweep.
- `DeleteDocumentHandler` deletes vectors as well as files, after the row, best effort (§3.4).
- `ITokenCounter` moved to `AddApplication`, because both hosts now count tokens.

No migration: the chunk table has had every column this step needs since Step 3. `dotnet ef
migrations has-pending-model-changes` confirms it, after one fix (§5.2).

---

## 2. Chunking: where a chunk begins and ends

**What.** `SectionAwareChunker` turns the artifact's sections into `ChunkDraft`s:

1. **A section is a hard boundary.** A PDF page or a heading's body never shares a chunk with
   text under a *different* heading trail.
2. **Small neighbours merge.** Adjacent sections with the *same* heading trail combine while
   they fit one chunk. Two short PDF pages become one chunk citing pp. 1-2.
3. **Long sections become windows** of up to 500 tokens, packed from paragraphs. A paragraph
   too long for a window is split into sentences, and a sentence too long into words. A
   "word" longer than a window (base64, a flattened table) is sliced by length.
4. **Overlap in whole units.** Each window begins with the previous window's last
   paragraphs, up to 75 tokens. It never starts mid-sentence.

**Why this approach.** The citation in Step 10 is only as good as the chunk's metadata.
With a plain sliding window over the whole document, a chunk that starts on page 3 under
"Annual leave" and ends on page 4 under "Sick leave" has no honest page or heading. Its
vector is also an average of two topics, so it matches neither well. Respecting the
extractor's structure costs a few undersized chunks. In return, every chunk is about one
thing and says exactly where it came from.

**Why a Strategy.** Chunking is the parameter Step 11 will tune hardest, and there are known
alternatives worth trying (§8). Behind `IChunkingStrategy` a new algorithm is a new class
and one line in `AddIngestionHandlers`. The handler, the persistence and the vector store
don't change. That is a real, expected variation point, the condition under which the
pattern pays for itself.

**The budget arithmetic** (found while writing the tests, §5.3). Every unit is counted
*together with its separator* ("\n\n" or " "). Summing separately counted pieces leaves the
separators out, and a window "packed to 500" comes out over. Because the estimate rounds up
per piece, counting with separators can only over-count. A window is at most the target,
never over it. The test `No_chunk_exceeds_the_target_even_for_a_sentence_with_no_punctuation`
holds it to that.

**What gets embedded.** `EmbeddingInput.For(document, chunk)` sends:

```text
Employee handbook
Benefits / Annual leave

Employees receive 25 days.
```

"Employees receive 25 days" alone does not say *of what*. With the title and heading, the
vector sits near questions about annual leave. `DocumentChunk.Content` stays the plain text,
because that is what a citation shows. Changing this format re-embeds everything, so it is
one function.

---

## 3. The indexing stage: resumable, bounded, three stores

### 3.1 One job, stages keyed on state ([ADR-0026](../adr/0026-one-ingestion-job-resumable-stages.md))

`IngestAsync` calls the extraction handler, then the indexing handler. Each asks the
document whether its stage is owed. So the same job is correct for a fresh upload, for a
duplicate delivery (does nothing), and for a retry after a crash halfway through embedding
(continues). Separate jobs per stage would each need a hand-off, and every hand-off can be
lost. Step 7 needed a whole sweep to recover just one.

### 3.2 Backpressure: waves of bounded batches

Chunks go to the provider in batches of `EmbeddingBatchSize` (64), with at most
`MaxConcurrentBatches` (2) requests in flight per document. That is the backpressure: a
3,000-chunk document never has more than two requests waiting on the provider, however fast
the worker could produce them. The test checks it directly: nine chunks, batch size 2,
limit 2 → batches `[2,2,2,2,1]`, at most 2 in flight.

The batches run in **waves**. A wave's batches run concurrently. When the whole wave is
done, the successful batches are recorded and saved, and the next wave starts. Why waves
and not a sliding window? `DbContext` is not thread-safe. With waves, the database is only
touched between waves, by one thread, with no locking or channel. The cost is that each
wave waits for its slowest batch. ADR-0026 records the sliding-window alternative and when
it would win.

### 3.3 "One failure must not lose the batch's progress"

Each batch is embedded, then written to Qdrant. Only then are its chunks recorded as
embedded (`Document.RecordEmbeddings`). A batch method never throws (except on
cancellation). It returns its outcome, so one failed batch cannot hide whether its siblings
succeeded. After every wave:

1. Whatever succeeded is recorded and **saved**.
2. If a batch failed:
   - **Refused by the provider** (`ai.rejected`, dimension mismatch): retrying gives the same
     answer. The document's vectors are deleted and the document is `Failed` with the reason.
   - **Anything else** (timeout, rate limit, open circuit, Qdrant unreachable): thrown, for
     Hangfire's retry. The document stays `Embedding`. The retry embeds only
     `ChunksAwaitingEmbedding`.

A crash between "vector written" and "chunk recorded" re-embeds that batch and overwrites
the same points: some tokens wasted, nothing wrong. The reverse order could record a chunk
as embedded with no vector, which `CompleteIngestion` would then wrongly trust.

### 3.4 Three stores, one order

| Moment | Order | A crash in between leaves |
|---|---|---|
| Chunking | delete the document's old vectors → save new chunk rows | no vectors yet; the retry re-chunks |
| Each wave | upsert vectors → record chunks embedded | vectors not yet recorded; re-embedded on retry, same ids |
| Completion | all recorded → `CompleteIngestion` | `Embedding` with all chunks done; the retry completes |
| Delete | row (and chunks, by cascade) → vectors → files | orphan points/files; harmless (below) |

Why are orphan vectors harmless? The payload holds no text
([ADR-0025](../adr/0025-qdrant-official-client-single-collection.md)). Step 9 resolves every
hit against SQL Server, so a point whose document is gone points at nothing and is never
shown. That is also why delete clean-up is **best effort**. If Qdrant is down, the delete
still returns 204, because the caller's document *is* gone, and a warning names the ids. A
500 would only invite a retry that then 404s.

---

## 4. Qdrant: identity, layout, and creation

**Point ids** ([ADR-0024](../adr/0024-section-aware-chunking-deterministic-point-ids.md)).
A point's id is a UUID derived from `(DocumentId, ChunkIndex)`: SHA-256 of
`eka-chunk:{documentId:N}:{index}`, first 16 bytes, version and variant bits set to version
8 ("custom", RFC 9562). Chunk 7 of document D is the same point every time it is written: a
retried batch, a redelivered job, a re-ingestion after reset. A random id would make each of
those a duplicate, and every search would return the passage twice.

Version 5 is the standard name-based UUID, so why not use it? It is defined on SHA-1, which
analyzers flag, and every reviewer would have to stop and justify it. Version 8 exists
precisely for "my own hash".

Deterministic ids overwrite, but they never delete. A reset document re-chunked into 5
chunks where it once had 7 would keep the old chunks 5 and 6. So the chunking stage deletes
the document's points first. The unit test seeds a stale point at index 7 and checks it is gone.

**Layout.** One collection for every tenant. Each point's payload carries `tenant_id` and
`document_id` (both keyword-indexed, `tenant_id` flagged `is_tenant`), plus chunk index, page
range, heading and model. Every delete and count filters on tenant **and** document. A
document id alone is never enough, and the adapter test proves that a delete under the wrong
tenant removes nothing.

**Lazy creation.** The collection is created on the first upsert, sized from the vectors.
The API never writes vectors, so it never needs the embedding dimension, and it starts with
Qdrant down. If the collection already exists at a different size, the adapter refuses with
an error naming the likely cause (the model changed without re-indexing). The alternative
is similarity scores that silently mean nothing. Payload indexes are re-created on every
first check, so a process that died between "create collection" and "create index" cannot
leave the tenant filter unindexed for good.

---

## 5. What went wrong

### 5.1 The shell and backslashes, again (almost)

A scripted rewrite of the end-to-end tests contained `\n` inside C# strings, the exact
hazard recorded after Step 7. This time the output was correct, and my check for corruption
was itself wrong: it searched for a doubled backslash that should never be there. Lesson
kept: check scripted output by reading the actual lines, not by grepping for a pattern
you expect to be absent.

### 5.2 A computed property became a relationship

The first integration run failed every test at fixture start-up with
`PendingModelChangesWarning`. Nothing in the schema had changed on purpose. The new
`Document.ChunksAwaitingEmbedding` is an `IReadOnlyList<DocumentChunk>` with only a getter.
EF Core's conventions map a read-only collection of an entity type **as a navigation**, so
the model had grown a second Document→DocumentChunk relationship. The computed booleans
(`AwaitsChunking`) were ignored, being scalars without setters. The fix is one
`builder.Ignore(...)` with a comment. `has-pending-model-changes` now reports none.

**The lesson:** a domain property's *type* can make it persistent. A read-only scalar is
safe. A read-only collection of entities is a relationship unless you say otherwise.
`MigrateAsync` refusing to run with pending changes (an EF Core 9+ default) caught it before
it became a stray migration.

### 5.3 The chunker's budget leaked separators

Planning the test numbers by hand showed the first version of the word splitter would pack
50 one-token words into a "50-token" piece that was really 63 tokens: 50 words plus 49
spaces, which the per-word estimate ignored. The same leak existed between paragraphs. It
was fixed before any test ran (§2), and the invariant "no chunk exceeds the target" became
a test.

### 5.4 Small ones

- **Windows curl and Git Bash paths.** `curl -F "file=@/c/Users/..."` failed silently during
  the demo: the curl on this machine is the Windows build and does not understand MSYS
  paths. A relative path from the file's folder worked. The polling loop then ran its full
  60 seconds on an empty response, because it didn't check the upload first. A demo script
  should fail on its first unexpected answer.
- **A stale log line.** The domain-event dispatcher said "handlers arrive in Step 7". Now it
  makes no promise about when.
- **An internal validator with no container in the test project.** The options validator is
  `internal`. The test now takes its implementation type from `AddApplication()`'s own
  registration, which also proves the registration exists.

---

## 6. The exit criterion on the real stack

API on Kestrel, worker as its own process, SQL Server and Qdrant from compose, a throwaway
database `EKA_Step8Demo` and collection `eka_step8_demo`, **real embeddings through
OpenRouter** (`openai/text-embedding-3-small`, 1536 dimensions). The worker's key was
passed to the demo process as an environment variable read from the API's User Secrets,
never printed and not stored anywhere new.

| Action | Result |
|---|---|
| Upload a 6 KB handbook (4 headings, one long section of 40 clauses) | 201 `Pending`, then `Completed` **5 s later**, 7 chunks |
| Worker log | `extracted 4 sections` → `cut into 7 chunks` → `Created Qdrant collection … 1536 dimensions, cosine` → `7 chunks embedded with openai/text-embedding-3-small` |
| Chunk rows | 22, 472, 467, 465, 248, 20, 20 tokens. The long section in 4 windows, each starting at a clause; the short sections whole |
| Qdrant count (tenant + document filter) | 7 |
| A point's payload | `tenant_id`, `document_id`, `chunk_index`, `section_path`, `embedding_model`; id `…-8d2e-b…` (version 8) |
| `DELETE` | 204; Qdrant count 0; storage folder empty |

Afterwards the processes were stopped, the database dropped, the collection deleted and the
files removed.

---

## 7. Decisions inside the code worth knowing about

- **`CompleteIngestion` requires the chunks to be loaded.** With only `ChunkCount`, "every chunk
  has a vector" would be unverifiable. The rule is checked where it can be checked, and
  `IndexDocumentHandler` loads with chunks.
- **`RecordEmbeddings` goes through the root,** not `DocumentChunk.MarkEmbedded` directly, so
  "vectors are recorded only while embedding" holds.
- **The configured model name, not the provider's echo,** is recorded on chunks. Providers may
  echo a dated snapshot name (`…-2024…`), which would make identical configurations look like
  different models.
- **Artifacts are checked before use:** `formatVersion` must be the one this build reads, and
  `contentHash` must match the document. A mismatch fails the document with "reset to
  extract again", instead of indexing the wrong text with full confidence (ADR-0023 put those
  fields there for this).
- **Chunk ids are UUIDv7** (`Guid.CreateVersion7(now)`) and are not the point ids. Search hits
  join back through `(DocumentId, ChunkIndex)`, which already has a unique index.
- **`ExtractTextAsync` survives as a forwarder.** Hangfire stores jobs by method name, so a
  rename is a breaking change to stored data, like renaming a column.
- **The worker validates its AI options at start.** A worker without a key refuses to start,
  instead of failing every job five times.

---

## 8. Alternatives, and when each would win

| Choice | Alternative | It wins when |
|---|---|---|
| Section-aware windows | Plain token window | Documents have no structure worth respecting (transcripts) |
| | Semantic chunking | Step 11 shows misses at window edges in prose with weak headings |
| | Parent/child chunks | Precise matching *and* generous answer context both matter |
| Derived point ids | Chunk row id | Never re-chunked, and every write path deletes first |
| Qdrant.Client | M.E.VectorData connector | Swapping vector stores is a real requirement |
| One collection + tenant payload | Collection per tenant | Few, large tenants needing structural isolation |
| No text in payload | Text in payload | SQL becomes the query-time bottleneck |
| One job, resumable stages | Job per stage (continuations) | Stages need different pools or retry policies |
| Waves | Sliding window | Large documents dominate and latency varies widely |

---

## 9. What to take away from Step 8

1. **Idempotence comes from identity.** Derived ids make "write it again" an overwrite. A
   delete-first rule handles the one case an overwrite cannot: fewer chunks than before.
2. **Record progress after the side effect, never before.** Vector first, then "embedded".
   Get it backwards and a crash leaves a promise with nothing behind it.
3. **Let the aggregate say what work is owed.** `AwaitsChunking`, `AwaitsEmbedding` and
   `ChunksAwaitingEmbedding` make one job body correct for first runs, duplicates and
   resumptions alike.
4. **Backpressure is a number you choose.** Two requests in flight is a decision about the
   provider's limits and everyone else sharing them, not a performance accident.
5. **Delete order follows what readers trust.** The row is the source of truth, so it goes
   first, and the rest is best-effort clean-up that can't make a reader see a ghost.
6. **A property's type can make it persistent.** Read-only collections of entities are
   navigations to EF Core unless ignored.

---

## Next

**Step 9: vector retrieval with ACL filtering.** Embed the question, search Qdrant with the
tenant filter applied in the query, resolve hits against SQL (Completed documents only), and
apply score thresholds. The decisions to settle first are the ACL model (`DocumentAcl`) and
whether hybrid search is in scope.
