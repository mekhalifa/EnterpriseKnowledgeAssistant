# ADR-0028: The retrieval contract: dense-only, configured top-K and score floor, over-fetch, and "no evidence" as an empty success

- **Status:** Accepted
- **Date:** 2026-11-16
- **Deciders:** project owner

## Context

Step 9 builds the retriever that the search endpoint and Step 10's answer generation will
share. Three questions were open (PROGRESS.md): hybrid search, who sets top-K and the score
threshold, and what "nothing relevant" looks like to the caller.

## Decision

- **Dense vectors only.** No BM25 or sparse vectors. Hybrid search is added only if Step 11's
  evaluation shows dense retrieval missing exact terms (product codes, clause numbers).
- **Top-K and the score floor are configuration** (`Retrieval:*`). Defaults: K = 5, max 20,
  and a floor of 0.6 on the normalized scale (cosine 0.2). A request may pass `topK`. Values
  above the maximum are clamped, as with page sizes; values of zero or less are a 400.
- **Over-fetch by `CandidateMultiplier`** (2): the vector store is asked for 2K candidates, so
  hits dropped by the SQL re-check do not shrink the result below K.
- **Deterministic order:** score descending, then document id, then chunk index.
- **"No evidence" is a successful, empty result.** A provider failure is its `ai.*` error
  (502), never an empty list. Step 10 decides what to do with empty.
- **The question is embedded as itself**, trimmed (`EmbeddingInput.ForQuery`). Passages carry
  a title and heading prefix; questions do not.
- **Search is `POST /api/search`**, with the question in the body, never in a URL that proxies
  and browsers log.

## Consequences

### Positive

- One retriever, one behaviour, for search and for answers. Step 11 measures both at once.
- Tuning K and the floor is a configuration change that clients cannot override.
- Step 10 can tell "nothing relevant" (empty) apart from "could not search" (error).

### Negative / costs we accept

- The 0.6 floor is a guess, informed only by a real-stack run where related passages scored
  0.62 to 0.80. It admits weakly related chunks. Step 11 sets it from data.
- Over-fetching doubles the vector work per query. That is negligible at this scale.
- Exact-term queries may under-perform without lexical search.

## Alternatives considered

- **Hybrid (dense + BM25, fused with RRF).** It wins when the evaluation shows misses on
  identifiers or rare terms. Qdrant supports it in one query (prefetch + fusion), so adding it
  later changes the adapter, not the contract.
- **Top-K and threshold per request.** It wins for an exploratory search UI. It loses here,
  because a client could then turn the floor off, and Step 10's context budget depends on K.
- **"No evidence" as an error (404/422).** It makes a normal outcome look like a failure, and
  makes Step 10 branch on error codes.
- **A re-ranker (cross-encoder) after retrieval.** It wins when the top results are relevant
  but wrongly ordered. That is a Step 11 finding, not a starting assumption.

## Revisit when

- Step 11 reports hit-rate@K or groundedness, which sets K and the floor.
- Step 11 shows misses on exact terms. Add hybrid search.
- Latency budgets are measured and the SQL re-check dominates (see ADR-0025).
