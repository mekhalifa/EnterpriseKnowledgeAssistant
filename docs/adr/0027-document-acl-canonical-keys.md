# ADR-0027: Document access as canonical principal keys, filtered inside the vector query and re-checked in SQL

- **Status:** Accepted
- **Date:** 2026-11-16
- **Deciders:** project owner

## Context

Step 9's exit criterion: *queries return relevant chunks and never content outside the
caller's access scope*. Until now the scope was the tenant. The owner chose to build
document-level access now instead of in Step 12, so that the retrieval path is designed with
it rather than patched later.

The constraints:

- Access must be enforced **in retrieval** (CLAUDE.md), and before content is returned. A
  nearest-neighbour search that is filtered afterwards returns fewer results than asked, or
  none, and that gap leaks information.
- The vector payload is a **copy**. SQL Server is the source of truth (ADR-0025), and the two
  stores share no transaction.
- There is no identity provider yet. The development scheme (ADR-0014) supplies tenant and
  subject, and now a `groups` claim, in the same shape Entra ID uses.

## Decision

- **A principal is a canonical string:** `tenant`, `user:<subject>` or `group:<group id>`.
  Subjects and group ids are the identity provider's own, not local ids, and they are
  case-sensitive.
- **A document's access is a non-empty list of principals** (`DocumentAccess`): `["tenant"]`
  by default, or named users and groups. At most 50 entries. `tenant` cannot be combined with
  others, because the result looks restricted and is not.
- **A caller's scope** (`AccessScope`) is their tenant plus the keys
  `[tenant, user:<sub>, group:<g>…]`. **The rule is one sentence:** same tenant, and the two key
  lists intersect. It is written once in the domain (`Document.IsAccessibleTo`), once in SQL
  (`AccessKeys.Any(k => callerKeys.Contains(k))`), and once as a Qdrant `match any` filter.
- **Storage:** an EF Core primitive collection, as a JSON column `Documents.Access`. In Qdrant,
  an `access` keyword array on every point, with a payload index.
- **Retrieval is two checks.** Qdrant filters by tenant, access keys and embedding model
  *inside* the query (the `ChunkFilter` Specification). Then every hit is re-read from SQL with
  tenant, current access, `Completed` status and exact (document, chunk) keys in the WHERE
  clause. SQL decides. The two checks are written independently, on purpose.
- **Changing access writes the copy first** (Qdrant `SetPayload` by filter, then the SQL
  save). With either order, every failure state is safe for readers, because SQL re-checks.
  Copy-first also makes a failure visible to the caller, who can retry the idempotent PUT,
  instead of a reported success that leaves the payload stale.
- **Every document use case takes an `AccessScope`**, not a bare tenant. A document a caller
  cannot read is `404`, as if it did not exist, including for changing its access.

## Consequences

### Positive

- One rule, three faithful translations, each tested: the domain unit tests, SQL through the
  list and re-check (integration), and Qdrant against a real server.
- A stale or lost payload update cannot leak a document. It can only hide one until the next
  access change.
- Step 12 swaps the identity provider, and nothing here changes: claims in, keys out.

### Negative / costs we accept

- **Anyone who can read a document can change its access**, including restricting it so
  that they themselves lose access. Owners and administrators arrive with roles in Step 12.
- **A concurrent ingestion can write stale keys.** The worker copies the keys it loaded when
  the job started. If access changes mid-embedding, later batches carry the old keys. When
  access was narrowed, SQL catches it. When it was widened, the document stays partly hidden
  until access is set again.
- **The upload duplicate check leaks existence.** Content hashes are unique per tenant, so
  uploading the bytes of a document you cannot read returns `409`. It names nothing, but it
  confirms that the bytes exist.
- A JSON column cannot be indexed. "Every document group hr can read" is a scan of the tenant.
- Points written before this change have no `access` field, so the filter excludes them (fails
  closed) until they are re-ingested or their access is set.

## Alternatives considered

- **Tenant-only now, ACL in Step 12.** Smaller step. It wins when no document will ever be
  narrower than its tenant, or when identity is being designed at the same time. It was
  rejected because the exit criterion names the caller's *access scope*, and because
  retrofitting a filter into a retrieval path is where such filters get forgotten.
- **A `DocumentGrants` child table.** Indexable, and the right shape for administrative
  queries across documents. It wins once such a report is needed (Step 12). For a small
  list that is always read whole, it costs a join on every list and re-check.
- **Filter after the search (post-filtering).** One generic search, then drop what the caller
  may not see. It wins never for authorization: the result has fewer than K hits, and the gap
  is observable.
- **A collection per tenant** (ADR-0025's alternative) makes the tenant structural, but does
  nothing for document-level access, which still needs this filter.
- **SQL first, then the payload.** Equally safe for readers. It loses because a failed payload
  update after a committed widening reports success, while the newly granted user never finds
  the document and nothing retries.

## Revisit when

- Step 12 adds roles. Who may change access becomes a policy, not "anyone who can read".
- Group membership exceeds what a token carries (Entra's groups overage). Keys must then
  be resolved server side.
- An administrative report needs access queried across documents. Move to a grants table.
- The "dropped by the SQL re-check" count in the retrieval log stays above zero. The copy is
  drifting, and needs a reconciliation job.
