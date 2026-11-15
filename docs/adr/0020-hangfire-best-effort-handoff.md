# ADR-0020: Hangfire on SQL Server, with a best-effort hand-off and a stalled-document sweep

- **Status:** Accepted
- **Date:** 2026-11-15
- **Deciders:** project owner

## Context

ADR-0004 put ingestion in a separate worker and left the hand-off mechanism open. An upload
writes a document row, and something must then tell the worker. Those are **two writes to
two places**. No transaction spans a database row and a queued job unless both live in one
transactional store, and even then the job library must take part in the application's
transaction. Whatever we choose must say what happens when the second write fails.

## Decision

- **Hangfire** (`Hangfire.Core`, `.SqlServer`, `.NetCore` 1.8.25) with its tables in the
  same SQL Server, under the `HangFire` schema. The API only enqueues. The worker runs the
  server on the `ingestion` queue (2 workers by default).
- **The hand-off is best effort, by contract.** `IIngestionQueue.EnqueueExtractionAsync`
  returns `false` instead of throwing. The adapter even resolves Hangfire's job client
  inside its try block, so an unreachable job store cannot fail an upload that has already
  been saved.
- **The document row is the source of truth.** A recurring job, `recover-stalled-documents`
  (every 5 minutes), re-enqueues documents still `Pending` after `Ingestion:StalledAfter`
  (5 minutes).
- **At-least-once delivery, idempotent processing.** The aggregate decides whether
  extraction is still owed (`AwaitsExtraction`) and can resume an interrupted one
  (`StartOrResumeExtraction`). The artifact write replaces the previous one whole.
- **Permanent failures are recorded, never retried.** Unreadable files (`Fail` on the
  document) end the job successfully. Transient exceptions propagate, and Hangfire retries
  them five times.
- Job arguments are primitives (`Guid`), never domain types, because stored jobs outlive
  the build that queued them.

## Consequences

### Positive

- No new infrastructure. Jobs are durable, with retry, backoff and a recurring scheduler
  included.
- A lost hand-off delays ingestion by at most `StalledAfter` plus 5 minutes. It never loses
  a document.
- The handlers know nothing of Hangfire. Replacing it changes one adapter and one job class.

### Negative / costs we accept

- **Duplicate work is possible** (sweep plus a slow original job). It is harmless by design,
  but it wastes extraction time.
- **No concurrency token on `Document`.** Two deliveries running at the same moment both
  extract and both save the same result. That is correct, and wasteful.
- **Retries exhausted leave the document `Extracting`.** The sweep covers only `Pending`. The
  job sits in Hangfire's Failed list. This is a known gap, tracked in PROGRESS.
- Hangfire's dashboard is **not wired yet**. Mapping it would resolve job storage at API
  startup, which defeats the lazy-storage design. It belongs with Step 14's operations work.
- Hangfire polls SQL Server once per second per worker process.

## Alternatives considered

- **The document table as the queue.** The worker claims `Pending` rows atomically. No
  package, and the row *is* the job, so nothing can drift. It wins at low volume when
  retry/backoff/scheduling are not wanted as a library. Here we would be rebuilding most of
  Hangfire.
- **A message broker** (RabbitMQ, Azure Service Bus). It wins at scale, or when other
  services consume the same events. It still needs an outbox table for the dual write, plus
  another container.
- **A transactional outbox in our own schema** with a relay to Hangfire. It wins when a lost
  hand-off must be recovered in seconds, not minutes. The sweep gives the same guarantee
  with less machinery at this scale.

## Revisit when

- Ingestion latency after a lost hand-off (up to about 10 minutes) becomes unacceptable.
- A second consumer of "document uploaded" appears (that points to an outbox plus a broker).
- Documents stuck in `Extracting` after exhausted retries appear in practice.
