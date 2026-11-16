# ADR-0029: Grounded answers: two-layer refusal, numbered citation markers, and the raw question for retrieval

- **Status:** Accepted
- **Date:** 2026-11-16
- **Deciders:** project owner

## Context

Step 10's exit criterion: *answers carry verifiable citations; unanswerable questions are
refused rather than fabricated.* The chat flow (Step 6) and retrieval (Step 9, ADR-0028)
both exist. Three decisions joined them, each with credible alternatives:

1. How an answer is refused when the documents do not support one.
2. How the sources behind an answer are determined.
3. What is embedded for retrieval when the question is a follow-up.

## Decision

- **Two-layer refusal.**
  - *No evidence, no model.* If retrieval returns nothing, or nothing fits the context
    budget, the system answers with `Chat:NoEvidenceAnswer` and calls no model. The answer
    is stored as an assistant message with provider `eka` and model `none:no-evidence`.
  - *Evidence, but not an answer.* The v2 instruction tells the model to answer only from
    the sources, and to say so and cite nothing when they do not cover the question.
  - The refusal text is the same whether nothing exists or the caller may not see it. Saying
    otherwise would confirm that a restricted document covers the question.
- **Numbered markers.** Sources are rendered as `<source id="n" title=… section=… pages=…>`
  blocks in the user turn, and the model cites them inline as `[n]`. The application parses
  the markers and keeps only numbers it actually offered. It then renumbers them in order of
  first appearance, rewrites the text to match, and persists exactly the cited chunks.
  `Citation.Number` is stored, because citation rows have no promised order. An answer that
  cites nothing is stored as ungrounded (`IsUngrounded`), and the response reports
  `grounding` as `cited`, `uncited` or `no_evidence`.
- **The raw question for retrieval.** The question is embedded as asked. History still goes
  to the model, so it can interpret a follow-up, but retrieval sees only the latest
  message.
- **Supporting rules.**
  - Sources are spent before history, within `Chat:MaxContextTokens` (3,000). A source that
    does not fit is skipped whole, never truncated.
  - Earlier answers lose their markers before they are replayed as history.
  - Tag-like `<source` text inside documents is defused.
  - Prompt template v2 is seeded by migration and v1 is deactivated, not edited (ADR-0019).

## Consequences

### Positive

- An unanswerable question with no evidence costs nothing and cannot be fabricated. On the
  real stack, off-topic and restricted questions never reached the model.
- Every persisted citation points at a chunk the model was actually shown, and every `[n]` in
  a stored answer has a citation row.
- A restricted document cannot reach a non-member's prompt. Retrieval is the same
  access-scoped `ChunkRetriever` that search uses.
- The marker format is plain text, so it survives token streaming (Step 13).

### Negative / costs we accept

- **Citations prove where a statement claims to come from, not that the source supports it.**
  On the real stack, a source saying "taxis are reimbursed 22:00–06:00" produced "yes, you can
  take a taxi after a late meeting [1]": cited, and an over-reach. Groundedness scoring is
  Step 11's job.
- The model's own refusal is recognised only by its having cited nothing. A cited answer that
  hedges, or an uncited answer that is actually right, is not distinguished.
- An out-of-range one-to-three-digit bracketed number is removed from the answer text, even
  if it was not meant as a citation (a footnote copied from a source).
- Follow-ups that carry no meaning of their own ("can I carry those over?") retrieve poorly.
- The refusal wording lives in configuration, not in a versioned prompt template.

## Alternatives considered

- **Model always decides** (no short-circuit). One code path, and a more natural refusal. It
  wins when retrieval is too weak to trust an empty result, for example with a badly tuned
  floor. It loses here: it pays for a call to have the model say what the system already
  knows.
- **Cite every retrieved chunk.** Trivial and robust. It wins only for a UI that shows "related
  documents" rather than claims. It over-claims, and no answer would ever be ungrounded.
- **Structured JSON output** (`{answer, citations}`). The most precise parsing. It wins with a
  provider whose structured output is guaranteed and when streaming is not needed. It loses
  on model variance through OpenRouter, on breaking token streaming, and on losing a whole
  answer to one malformed reply.
- **Query rewriting** (an LLM call condenses history into a standalone query). It wins once
  Step 11 shows follow-ups failing often enough to pay a second call per question.
- **Prepend the previous question** to the query. Cheap. It wins for pronoun-heavy
  follow-ups, and loses when the user changes topic.

## Revisit when

- Step 11 measures groundedness. Consider a verification pass, or a stricter instruction, if
  cited over-reach is common.
- Step 11 measures follow-up hit rate. Add query rewriting if it is poor.
- Step 13 streams tokens. Marker extraction must then run on the completed text, or
  incrementally.
- A provider with guaranteed structured output becomes the target.
