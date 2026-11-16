# ADR-0024: Section-aware chunking, and vector ids derived from (document, chunk index)

- **Status:** Accepted
- **Date:** 2026-11-16
- **Deciders:** project owner

## Context

Step 8 turns extracted text (ADR-0023) into chunks and chunks into vectors. Two choices
shape everything after it:

- **Where chunks begin and end.** Step 10 cites a chunk by page and heading, so a chunk's
  metadata must be exact. Step 11 will tune chunk size and probably compare algorithms.
- **How a vector is identified in Qdrant.** Jobs are delivered at least once (ADR-0020),
  batches are retried, and a failed document can be reset and ingested again. Each of those
  writes some vectors a second time.

## Decision

**Chunking is a Strategy** (`IChunkingStrategy`), and the default is `SectionAwareChunker`:

- An extracted section (a PDF page, or a heading's body) is a hard boundary. A chunk never
  holds text from two different heading trails.
- Adjacent sections with the **same** heading trail merge while the result fits one chunk.
  Two short PDF pages become one chunk citing "pp. 3-4".
- A section longer than the target is cut into windows of about `ChunkTargetTokens` (500),
  packed from paragraphs, then sentences, then words. The next window repeats the previous
  one's last units, up to `ChunkOverlapTokens` (75). Overlap is measured in whole units, so
  no window starts mid-sentence.
- Tokens are counted with the same `ITokenCounter` the prompt builder uses, so a chunk the
  worker sized at 500 tokens costs 500 tokens in a prompt. Each piece is counted with its
  separator, so a window never exceeds the target.
- The text sent to the embedding model is `title + heading trail + chunk`
  (`EmbeddingInput`). The stored chunk is the plain text, because that is what a citation shows.

**A point's id is derived:** a version 8 UUID from the first 16 bytes of
`SHA-256("eka-chunk:{documentId:N}:{chunkIndex}")`. Rewriting chunk 7 of document D, for any
reason, overwrites the same point. Before new chunks are saved, the document's old points
are deleted, because a re-chunked document may have fewer chunks than before, and an
overwrite never removes the extras.

## Consequences

### Positive

- Every chunk's page range and heading are exact, not "roughly where this window started".
- Retries, redeliveries and re-ingestion cannot duplicate a vector. A search can never return
  the same passage twice from one document.
- The strategy is pure (no I/O, no clock, no ids), so it is unit-tested to the character, and
  re-chunking the same artifact is reproducible.
- A new algorithm is one class and one registration.

### Negative / costs we accept

- Uneven chunk sizes: a one-sentence section is its own small chunk when its neighbours have
  different headings. Small chunks embed with less context, which the heading prefix
  partly compensates for.
- The token count is the four-characters estimate (ADR-0018). Windows are sized by an
  approximation, not by the embedding model's tokenizer.
- Point ids are not the chunk rows' ids. Joining a search hit back to SQL goes through
  `(DocumentId, ChunkIndex)`, which the unique index `UX_DocumentChunks_DocumentId_ChunkIndex`
  already serves.
- Changing `EmbeddingInput`'s format changes every vector, like changing the model. It is
  one function so the decision is visible.

## Alternatives considered

- **Plain token window over the whole document.** Simplest, and uniform sizes. It wins when
  documents have no useful structure (transcripts, scraped text). It loses exact citations:
  windows straddle pages and headings.
- **Semantic chunking** (embed sentences, split where similarity drops). Better boundaries in
  prose with weak headings. It wins if Step 11 shows retrieval failing at window edges. It
  costs an embedding call per sentence during ingestion, and is not deterministic across
  model versions.
- **Parent/child chunks** (embed small chunks, return the larger parent). It wins when
  precise matching and generous context both matter. It needs a second chunk level in the
  schema, so it is a Step 11 experiment, not a default.
- **Point id = `DocumentChunk.Id`.** No derivation code. Idempotent only while the chunk
  rows survive. Re-chunking mints new ids, so every path that writes vectors would have to
  delete first, and a missed delete is a silent duplicate.

## Revisit when

- Step 11's evaluation shows misses at chunk boundaries, or that a different size wins.
- Documents arrive whose structure the extractors cannot see (scans, slides), making sections
  meaningless.
- The embedding model changes. That means a new collection and a full re-index, whatever
  the chunking.
