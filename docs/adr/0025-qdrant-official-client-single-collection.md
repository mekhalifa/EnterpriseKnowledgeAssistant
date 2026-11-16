# ADR-0025: Qdrant through its official client, one collection partitioned by tenant, no text in the payload

- **Status:** Accepted
- **Date:** 2026-11-16
- **Deciders:** project owner

## Context

Vectors need a store that supports filtered nearest-neighbour search. Qdrant was chosen at
project start (README). Step 8 decides how the application talks to it and how the data is
laid out. Three constraints apply:

- Step 9 must filter by tenant (and later by document access) **before** any content is
  returned. Authorization is enforced in retrieval (CLAUDE.md).
- The API deletes vectors with documents. The worker writes them. Only the worker knows the
  embedding model's dimension.
- Chunk text already lives in SQL Server, its source of truth.

## Decision

- **`Qdrant.Client` 1.20.0**, the official gRPC client, behind the application-owned
  `IVectorStore` port (in `EKA.Application`). The adapter, `QdrantVectorStore`, is internal
  to `EKA.Infrastructure`. Server image `qdrant/qdrant:v1.20.0`, pinned to the client's minor
  in compose and in the test fixture.
- **One collection** (`VectorStore:CollectionName`, default `eka_chunks`) for every tenant.
  Each point carries `tenant_id` and `document_id`, both indexed as keywords. `tenant_id` is
  marked `is_tenant`, Qdrant's multitenancy layout hint. Every delete and count filters on
  **both** fields, and Step 9's search will filter on the tenant too.
- **The payload holds identifiers and filterable metadata only:** tenant, document, chunk
  index, page range, heading trail, embedding model. **No chunk text.** Step 9 resolves hits
  against SQL Server, which also re-checks the tenant and that the document is still
  Completed.
- **The collection is created on the first write**, sized from the vectors themselves, with
  cosine distance. An existing collection with a different size is refused with an error
  that names the cause (the model changed without re-indexing). Payload indexes are
  (re)created on every first check, so a crash between "create collection" and "create
  index" cannot leave the tenant filter unindexed.
- Writes use `wait: true`, because the caller records chunks as embedded straight after.
- One `QdrantClient` per process (it owns a gRPC channel). Constructing it does not connect,
  so the API and the worker start even when Qdrant is down.

## Consequences

### Positive

- Full control of what Step 9 needs: payload indexes, tenant layout, filter composition.
- The API needs no embedding configuration to delete vectors, and starts without Qdrant.
- One copy of the text. A chunk edited or deleted in SQL can never be served stale from
  the vector store, because the vector store never had the text.
- A mismatched embedding dimension fails loudly at the first write, instead of producing
  meaningless similarity scores.

### Negative / costs we accept

- **Retrieval is two round trips:** Qdrant for ids and scores, then SQL for text. It is
  cheaper to keep correct than one round trip with duplicated text.
- **Tenant isolation in Qdrant is a filter, not a boundary.** A query that forgets the filter
  sees every tenant. Mitigated by the port (every method takes the tenant) and by Step 9's
  re-check against SQL. A collection per tenant would make it structural.
- Vectors are a third store with no transaction shared with SQL. Deletes clean up best
  effort after the row commits, so a failure leaves orphan points. They are harmless, because
  every hit is resolved against SQL, but they take up space until a reconciliation job exists.
- No API key locally: the ports are bound to 127.0.0.1. Any shared deployment must set
  `VectorStore:ApiKey` as a secret.

## Alternatives considered

- **`Microsoft.Extensions.VectorData` with the Qdrant connector.** One abstraction over
  many stores, in the same family as Microsoft.Extensions.AI. It wins if switching vector
  stores is a real requirement. It loses here: thinner control of payload indexes and
  multitenancy settings, and the connector is still in preview.
- **A collection per tenant.** Structural isolation: a forgotten filter cannot cross
  tenants. It wins for a few large tenants with strict isolation requirements. It costs
  memory and overhead per collection, and Qdrant's own guidance prefers payload partitioning
  for many tenants.
- **Chunk text in the payload.** One round trip per search. It wins when SQL becomes the
  bottleneck at query time. It costs a second copy of every chunk and a consistency problem
  on every edit and delete.
- **Creating the collection at startup from configuration.** Simpler to read. It forces the
  API to know the embedding dimension and fail when Qdrant is down.

## Revisit when

- Step 9's latency budget is not met because of the SQL round trip.
- A tenant needs contractual isolation (its own collection, or its own cluster).
- The vector count outgrows one node, which makes sharding and replication factors decisions.
