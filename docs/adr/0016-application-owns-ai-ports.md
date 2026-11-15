# ADR-0016: The application owns its AI ports; `IChatClient` stays in the adapter

- **Status:** Accepted
- **Date:** 2026-11-15
- **Deciders:** project owner

## Context

`Microsoft.Extensions.AI` already provides vendor-neutral interfaces, `IChatClient` and
`IEmbeddingGenerator<string, Embedding<float>>`, plus a middleware ecosystem for logging,
OpenTelemetry, caching and function calling. Neither is OpenAI-specific. So there is a
genuine question: should use cases in `EKA.Application` take `IChatClient` directly, or
should `EKA.Application` declare interfaces of its own?

Constraints that apply:

- `ErrorType.Provider` → 502 exists, and ADR-0011 says expected failures are `Result`s, not
  exceptions. An upstream outage is an expected failure.
- `MessageRole` is a trust boundary in the domain: document text is never `System`.
- `TokenCount` exists precisely to stop token counts being confused with other integers.

## Decision

`EKA.Application` declares **`IChatCompletionService`** and **`IEmbeddingService`**, in
domain vocabulary (`ChatTurn(MessageRole, string)`, `TokenCount?`, `ReadOnlyMemory<float>`).
They return `Result<T>`, with failure codes from `AiErrors`. `EKA.AI` implements them over
`IChatClient` / `IEmbeddingGenerator`.

Rules:

- `EKA.Application` references **no** AI package: not OpenAI, not `System.ClientModel`, not
  Polly, and **not `Microsoft.Extensions.AI` either**. `ApplicationBoundaryTests` enforces
  each one.
- The adapter translates known provider failures to `AiErrors` (`ProviderFailure`).
  Anything it does not recognize still throws and becomes a 500, because it is a bug and not
  an outage.
- Cancellation by the caller is never translated: it throws `OperationCanceledException`.
- `IEmbeddingService` guarantees vector length equals `Dimensions`, or it fails with
  `ai.embedding_dimension_mismatch`.

## Consequences

### Positive

- Handlers return provider failures like any other `Result`. No handler needs a `try`
  block, and no test needs an AI package.
- The trust boundary and token typing survive the trip to the model. There is no second
  role enum to get wrong.
- The guarantees that matter to this system (the dimension check, index alignment, empty
  answers treated as failures) live in one adapter rather than in every caller.

### Negative / costs we accept

- **A translation layer to maintain.** When Step 10 wants streaming, or a later step wants
  tool calls or structured output, the port has to grow to expose it. `IChatClient` already
  has all three.
- Two sets of names for one idea (`ChatTurn` vs `ChatMessage`, `ChatCompletion` vs
  `ChatResponse`), which a reader of `EKA.AI` must keep apart.

## Alternatives considered

- **`IChatClient` directly in use cases.** It is vendor-neutral, so the dependency rule is
  arguably not broken. It wins if the application comes to need much of the
  `Microsoft.Extensions.AI` surface (tools, structured output, multimodal content), so that
  the port would become a copy of `IChatClient` under a different name. At that point,
  depend on `Microsoft.Extensions.AI.Abstractions` only and translate exceptions in a
  middleware instead.
- **Semantic Kernel.** It wins if orchestration (planners, plugins, agents) becomes the
  centre of the product. That is out of scope for this learning path.

## Revisit when

- The port has to be widened for a third Microsoft.Extensions.AI feature, after streaming.
- A use case needs function calling.
