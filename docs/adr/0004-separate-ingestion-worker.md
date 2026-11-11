# ADR-0004: Ingestion runs as a separate process

- **Status:** Accepted
- **Date:** 2026-11-11
- **Deciders:** project owner

## Context

ADR-0001 chose a monolith, so any process split needs a specific justification. Ingestion
has one. Processing a large document means:

- parsing a file that may be hundreds of pages (CPU and memory heavy),
- generating thousands of embeddings across many provider calls (minutes of wall-clock
  time, rate-limited, retry-prone),
- writing thousands of rows and vectors.

Interactive chat, by contrast, is latency-sensitive and short-lived.

Running both in one process means a single large upload degrades every user's chat
response, and an API deployment or restart kills ingestion jobs mid-document.

## Decision

Run ingestion in **`EKA.Ingestion.Worker`**, a separate .NET Worker Service process. The
API accepts the upload, persists the document record with status `Pending`, enqueues the
job, and returns immediately. The worker performs extraction, chunking, embedding, and
indexing, persisting status after each stage.

Hand-off mechanism is an open decision for Step 7 (Hangfire with a SQL-backed queue and
dashboard, versus a dedicated message queue). Recorded in
[PROGRESS.md](../PROGRESS.md#decisions-still-to-make).

## Consequences

### Positive

- Ingestion load cannot affect chat latency — different processes, separately resourced.
- The worker can be scaled or restarted independently of the API.
- Long-running work survives an API deployment.
- Retry, backoff, and concurrency limits are configured where the problem is, not globally.
- Upload becomes a fast, non-blocking API call, which is also a better user experience.

### Negative / costs we accept

- **Asynchronous status is now a user-visible concern.** The client must poll or subscribe
  to know when a document is searchable, and Step 13 has to show ingestion state.
- Two processes to run locally, configure, deploy, and observe.
- Jobs must be **idempotent**, because at-least-once delivery means a document can be
  processed twice. Enforced by a unique `(DocumentId, ChunkIndex)` index and status
  transition guards.
- Debugging spans two processes, requiring correlation ids in logs.
- Shared code must live in projects both can reference — which the layering already
  provides.

## Alternatives considered

**Everything in the API process, using `BackgroundService` or `Task.Run`.** Simplest,
one process. Rejected: work is lost on restart, a large upload competes with request
threads, and there is no durable retry. `Task.Run` for a multi-minute job in a web request
is a well-known anti-pattern.

**Serverless function triggered by blob upload.** Attractive operationally — scales to
zero, no host to manage. Rejected for the MVP: execution time limits fit badly with large
PDF processing, local development and debugging are harder, and it would fragment the
stack we are deliberately keeping in Docker Compose.

**Synchronous ingestion on upload.** Rejected outright — multi-minute HTTP requests time
out at every proxy, and the user gets no progress information.

## Revisit when

- Ingestion volume justifies multiple worker instances, requiring a distributed lock or
  partitioned queue.
- Stages develop genuinely different resource needs (e.g. OCR wanting GPU), making
  per-stage processes worthwhile.
- The Hangfire-versus-queue decision in Step 7 changes the hand-off contract.
