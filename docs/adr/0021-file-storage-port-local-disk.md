# ADR-0021: An `IFileStorage` port, with local disk as the first adapter

- **Status:** Accepted
- **Date:** 2026-11-15
- **Deciders:** project owner

## Context

Uploads need a durable home that both the API (which writes) and the worker (which reads)
can reach. Production will be a cloud blob store. Locally, the cost of anything beyond the
file system is a container plus an SDK, paid before there is a single document to store.

## Decision

- `IFileStorage` in `EKA.Application`, with three operations: `SaveAsync`, `OpenReadAsync`
  and `DeleteAsync`. **Writes are atomic per key**, and delete is idempotent.
- `LocalFileStorage` in `EKA.Infrastructure`:
  - writes to a uniquely named `.tmp` file in the target folder, then
    `File.Move(..., overwrite: true)`;
  - resolves every key to a full path and **refuses keys that escape the root**, even
    though `StorageKeys` already produces safe keys;
  - uses `%LOCALAPPDATA%/EKA/files` when `Storage:RootPath` is empty. That location is the
    same for both processes on one machine, regardless of their working directories. In a
    container, `Storage__RootPath` points at a shared volume.
- **Order of writes:** on upload, the file is saved before the row. On delete, the file is
  removed after the row. A crash in between leaves orphaned bytes, never a row pointing at
  a missing file.

## Consequences

### Positive

- No new infrastructure, and the port shape is what a blob store needs anyway.
- A crashed write never leaves a truncated file under a real key.
- Path traversal is refused by the adapter itself.

### Negative / costs we accept

- **One machine only.** The API and the worker must share a disk. That is fine locally, and
  a shared volume in containers, but wrong for a multi-node deployment.
- **Orphaned files** can accumulate after crashes. No reconciliation job exists yet.
- No encryption at rest beyond the operating system's own.

## Alternatives considered

- **Azure Blob Storage (Azurite locally).** It wins once deployment targets Azure (Step 14).
  It becomes the second adapter, and nothing above the port changes.
- **MinIO (S3-compatible) in Docker.** It wins if the target is AWS or on-premises S3, or if
  production-shaped storage is wanted from day one.
- **Files in SQL Server** (`varbinary(max)` or FILESTREAM). It wins only for very small files
  that must be transactional with the row. It bloats backups and the buffer pool.

## Revisit when

- Step 14 picks a deployment target.
- The API and worker run on different machines.
