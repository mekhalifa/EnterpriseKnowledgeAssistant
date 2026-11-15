# ADR-0018: A conservative token estimate for the prompt budget, exact counts where providers give them

- **Status:** Accepted
- **Date:** 2026-11-15
- **Deciders:** project owner

## Context

History windowing needs the token size of every message *before* the request is sent.
Providers report exact counts only *after* the call, and only for the call as a whole
(input) and for the answer (output). The chat model is a configuration value behind
OpenRouter (ADR-0015), so the tokenizer that applies can change with a settings edit.

## Decision

- An `ITokenCounter` port in `EKA.Application`. The default, `EstimatingTokenCounter`,
  computes `ceil(characters / 4)`, plus a fixed 4 tokens of per-message framing in the
  builder.
- **Assistant messages store the provider's exact output count** (`Message.TokenCount`),
  and the builder prefers it over the estimate. User messages carry none, by domain design,
  and are estimated when the prompt is built.
- The budget, `Chat:MaxPromptTokens` (default 6000), sits far below the model's window. That
  leaves headroom for estimation error and caps the cost of one question.

## Consequences

### Positive

- No package and no tokenizer data. The estimate holds across every model OpenRouter
  serves.
- Over-estimating only trims history slightly early, which is the safe direction.
- As a conversation grows, more of its size is exact, because answers are usually the long
  part.

### Negative / costs we accept

- **Code, URLs and non-Latin scripts tokenize denser** than 4 characters per token, so the
  estimate *under*-counts them. The headroom is the mitigation. Step 11 measures whether it
  is enough.
- The 4-token framing overhead is an approximation of chat formatting, not a measurement.

## Alternatives considered

- **`Microsoft.ML.Tokenizers` with the o200k vocabulary.** Exact for gpt-4o-family models.
  It wins if the chat model is pinned to OpenAI and budgets need to run close to the window
  (long-context RAG in Step 10). It is wrong for Claude or Llama behind the same gateway.
- **Ask the provider to count first.** Exact for any model, but it costs a round trip per
  question. It wins only where a counting endpoint is free and latency does not matter.

## Revisit when

- Step 10's retrieved context makes prompts approach the budget routinely.
- Step 11 observes a provider rejection for context length, or answers degraded by
  over-trimmed history.
