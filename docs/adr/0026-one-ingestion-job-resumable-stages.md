# ADR-0026: One ingestion job per document, with stages that resume from the document's state

- **Status:** Accepted
- **Date:** 2026-11-16
- **Deciders:** project owner

## Context

Step 7 had one job: extract text, leave the document in `Chunking`. Step 8 adds chunking
and embedding. Embedding a large document is many provider calls that can fail partway,
and the job engine retries the whole job. Something has to decide how the stages are
scheduled, and what a retry does with the work already finished.

## Decision

- **One Hangfire job, `IngestionJobs.IngestAsync(tenantId, documentId)`**, runs every stage:
  `ExtractDocumentTextHandler`, then `IndexDocumentHandler`. Each handler asks the aggregate
  whether its stage is owed (`AwaitsExtraction`, `AwaitsChunking`, `AwaitsEmbedding`), so the
  same job serves a fresh upload, a redelivery, and a retry halfway through.
- **Embedding progress is saved per wave.** Batches of `EmbeddingBatchSize` chunks run
  `MaxConcurrentBatches` at a time. After each wave, the chunks whose vectors were written are
  recorded (`Document.RecordEmbeddings`) and saved. A retry embeds only
  `ChunksAwaitingEmbedding`. `CompleteIngestion` refuses while any chunk lacks a vector.
- **Failures split in two:** provider refusals (`ai.rejected`, dimension mismatch) fail the
  document and delete its vectors. Everything else is thrown for the job's retries, with
  progress kept.
- **`IIngestionQueue.EnqueueExtractionAsync` is renamed `EnqueueIngestionAsync`.** Reset
  now enqueues too, instead of waiting for the sweep.
- **The Step 7 job method `ExtractTextAsync` stays as a forwarder.** Hangfire stores jobs by
  type and method name, so renaming a job method breaks jobs already queued under the old name.

## Consequences

### Positive

- No hand-off between stages, so no second place where a hand-off can be lost, and no
  sweep needed for intermediate statuses.
- A failure on chunk 2,900 of 3,000 retries 100 chunks, not 3,000.
- The stage logic lives in the handlers and the aggregate, not in job-chaining configuration.

### Negative / costs we accept

- One retry policy for all stages: five attempts with Hangfire's backoff. Parsing and
  embedding fail differently, and cannot be tuned separately.
- A job that exhausts its retries leaves the document in its intermediate status (now
  `Extracting`, `Chunking` or `Embedding`). The sweep only recovers `Pending`. The Step 7 gap
  remains, and now spans three statuses.
- Waves wait for their slowest batch, leaving some throughput unused in exchange for touching
  the non-thread-safe `DbContext` from one thread only.
- A forwarder method to remove later, once no old-named job can still be queued.

## Alternatives considered

- **A job per stage, chained with Hangfire continuations.** Independent retry policies and
  visibility per stage in the dashboard. It wins if stages need different queues, for example
  embedding on machines near the provider, or a separate concurrency limit. It adds a
  hand-off per stage that can be lost.
- **A sliding window instead of waves** (`Channel` + a single recording loop). Full use of the
  concurrency limit. It wins if large documents dominate and provider latency varies a lot.
  It costs a producer/consumer structure to keep `DbContext` access on one thread.
- **Recording progress only at the end.** Simplest. A failure late in a large document would
  redo, and re-bill, everything.

## Revisit when

- Stages need different worker pools or retry policies.
- Documents stuck after exhausted retries show up in practice. That is the trigger for
  extending the sweep, or for a "stalled since" timestamp on the document.
- No job named `ExtractTextAsync` remains in Hangfire storage: delete the forwarder.
