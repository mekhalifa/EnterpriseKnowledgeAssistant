# ADR-0015: One OpenAI-compatible adapter, with OpenRouter as the first endpoint

- **Status:** Accepted
- **Date:** 2026-11-15
- **Deciders:** project owner

## Context

Step 5 needs one working chat model and one working embedding model. The learning path
assumed a choice between OpenAI direct and Azure OpenAI. The owner has neither key; they
have an **OpenRouter** key. OpenRouter is a gateway that speaks the OpenAI wire protocol at
`https://openrouter.ai/api/v1`, routes to many vendors' models, and has an embeddings
endpoint that serves `openai/text-embedding-3-small` with the `dimensions` parameter.

Two things get fixed here and are expensive to change later:

- **The shape of the configuration**: endpoint plus model names, or deployment names plus
  a resource URL.
- **The embedding model and dimension**, which fixes the Qdrant collection shape in Step 8.
  Changing it means re-indexing every document.

## Decision

- **One adapter over the official OpenAI .NET SDK**, reached through
  `Microsoft.Extensions.AI.OpenAI`, with a configurable base address. A new
  `AIProviderKind.OpenAICompatible` member names this case. The domain requires an endpoint
  for it: without one, the SDK would send the request, and a key meant for another service,
  to api.openai.com.
- **Configuration shape**, section `Ai`: `Kind`, `Endpoint`, `ApiKey`, `ChatModel`,
  `EmbeddingModel`, `EmbeddingDimensions`, and `Resilience`. The key lives in User Secrets
  or `Ai__ApiKey`, everything else in `appsettings.json`. Model names and dimension have no
  defaults in code.
- **Initial values:** OpenRouter, `openai/gpt-4o-mini` for chat (free to change at any
  time), `openai/text-embedding-3-small` at **1536** dimensions (not free to change).
- **`AzureOpenAI` is rejected at startup**, with a message pointing here, until it has a
  real adapter. Routing an Azure configuration through the OpenAI adapter would fail on the
  first request with a 404 from a URL shape Azure does not serve.

## Consequences

### Positive

- Pointing at OpenAI direct, a local Ollama (`http://localhost:11434/v1`, allowed because
  it is loopback), vLLM or LiteLLM is a configuration change, not a code change.
- One key reaches many models, so Step 11's evaluation can compare chat models without new
  accounts.
- The configuration shape is already right for the common case. Only Azure's
  deployment-name model will need an addition.

### Negative / costs we accept

- **A third party is now in the data path.** Prompts, retrieved document text and answers
  pass through OpenRouter before reaching the model vendor. Fine for learning on
  non-sensitive documents. A real enterprise deployment needs that processor approved, or
  needs to switch to Azure OpenAI or OpenAI direct.
- OpenRouter routing (choosing a vendor, falling back between them) is not used. The
  adapter sends plain OpenAI requests and nothing OpenRouter-specific.
- The provider reports model names in its own spelling (`text-embedding-3-small`, without
  the `openai/` prefix). Anything that compares model names must not assume they round-trip
  unchanged.

## Alternatives considered

- **OpenAI direct.** Identical code, with `Kind: OpenAI` and no endpoint. Wins the moment
  there is an OpenAI key and no reason to want many vendors. No code change needed.
- **Azure OpenAI with its own adapter** (`Azure.AI.OpenAI`, deployment names, Entra ID
  authentication). Wins when private networking, data residency or an enterprise agreement
  is required. That is a production requirement this project expects to meet by Step 14.
- **An OpenRouter-specific adapter** with routing headers and preferences such as pinning a
  vendor. Wins if vendor pinning becomes a compliance requirement ("only Azure-hosted
  OpenAI"). Rejected for now because it ties the adapter to one gateway.
- **`text-embedding-3-large`** at 3072, or reduced to 1024. Wins if Step 11 shows a
  retrieval-quality gap that 3-small cannot close. It costs 6.5× per token and a full
  re-index.

## Revisit when

- Real or sensitive documents are about to be ingested: decide about the data processor first.
- Step 11's evaluation shows retrieval quality limited by the embedding model.
- An Azure subscription becomes available for Step 14's production shape.
