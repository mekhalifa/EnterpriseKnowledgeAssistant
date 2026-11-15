# Step 5: AI providers through Microsoft.Extensions.AI

**Status:** ✅ Complete
**Date:** 2026-11-15
**Branch:** `step-05-ai-providers` (branched from `step-04-application-api`)
**Result:** 362 tests, zero warnings. **The exit criterion was met against a real
provider:** one chat call (`openai/gpt-4o-mini`, 24 tokens in and 2 out) and one embedding
call (1536 dimensions) went through OpenRouter, using only the application's own ports.

Before this step the system could store a document and could not think about one. This
step connects it to a language model and an embedding model. No provider type crosses into
`EKA.Application`, and an upstream outage reaches a use case as a `Result`, not as a stack
trace.

---

## 1. What was built

| Piece | Where | Purpose |
|---|---|---|
| `IChatCompletionService`, `IEmbeddingService` | `EKA.Application/Abstractions/AI` | The ports. Domain vocabulary in, `Result<T>` out |
| `ChatTurn`, `ChatCompletion`, `EmbeddingBatch` | same | What crosses the port. `TokenCount?` for usage: null means "not reported", not zero |
| `AiErrors` | same | Eight stable `ai.*` codes, all `ErrorType.Provider` → 502 |
| `AIProviderKind.OpenAICompatible` | `EKA.Domain` | A new kind; the domain requires an endpoint for it |
| `AiOptions` + `AiOptionsValidator` | `EKA.AI/Configuration` | The `Ai` section, validated with `ValidateOnStart()` |
| `DependencyInjection.AddAi` | `EKA.AI` | The pipeline: options → HttpClient + resilience → SDK client (the Factory) → `IChatClient` with middleware → adapters |
| `ChatCompletionService`, `EmbeddingService` | `EKA.AI` | Adapters. Translation, the empty-answer guard, the dimension and alignment guards |
| `ProviderFailure` | `EKA.AI` | The only code that knows the SDK's exception vocabulary |
| 35 tests | `EKA.AI.Tests` | 11 resilience cases, 7 adapter, 13 options, 2 smoke, 2 boundary (pre-existing) |
| 6 new boundary cases | `EKA.Application.Tests` | OpenAI, Azure.AI, System.ClientModel, Microsoft.Extensions.AI, Microsoft.Extensions.Http and Polly are all forbidden |

Packages, all in `EKA.AI` only: `Microsoft.Extensions.AI` and `.AI.OpenAI` 10.11.0,
`OpenAI` 2.15.0, `Microsoft.Extensions.Http.Resilience` 10.11.0. The dotnet/extensions
release train is versioned apart from the runtime, so it has its own property in
`Directory.Packages.props`, `MicrosoftExtensionsLibrariesVersion`. Its 11.0.0 is the same
`net11.0` trap as in Step 3.

---

## 2. The call path, and why every layer is a decorator

```text
ChatCompletionService          our port: ChatTurn[] in, Result<ChatCompletion> out
 └ LoggingChatClient           Microsoft.Extensions.AI middleware   (.UseLogging())
    └ OpenAI IChatClient       Microsoft.Extensions.AI.OpenAI       (.AsIChatClient())
       └ OpenAI SDK            retries DISABLED
          └ HttpClient "eka-ai"  from IHttpClientFactory, Timeout = infinite
             └ standard resilience handler
                  total timeout → retry → circuit breaker → attempt timeout
                └ SocketsHttpHandler  (PooledConnectionLifetime = 2 min)
```

**What:** each box wraps the one below it and adds exactly one concern.

**Why this shape:** each concern can be removed or replaced on its own. Step 14 adds
`.UseOpenTelemetry()` as one more line in the builder chain, and nothing else changes.
Caching would be another layer. A different provider replaces two boxes in the middle. This
is the Decorator pattern from `concepts/design-patterns.md`, and
`Microsoft.Extensions.AI`'s builder is just a tidy way to write it.

**Best option:** use the library's builder (`AddChatClient(...).UseLogging()`) rather than
writing decorators by hand. Hand-written ones win only for a concern the library does not
have. Ours (failure translation, dimension checks) sits in the adapter, above the
pipeline, because it needs domain types.

---

## 3. Exactly one retry layer

**The trap:** the OpenAI SDK retries on its own, three times by default, with its own
backoff. Put it under an HTTP resilience handler that also retries three times, and one
sustained outage costs **(1+3) × (1+3) = 16 requests** per call. Any of them that the
provider accepted before failing may be billed.

**The fix:** `RetryPolicy = new ClientRetryPolicy(maxRetries: 0)` on the SDK. The
resilience handler becomes the only retry layer. It is configurable, has jitter, honors
`Retry-After` on 429, and has a circuit breaker.

**The test that proves it:**
`A_sustained_outage_makes_exactly_one_plus_max_retries_requests` asserts **4** calls
reached the network. The number is the assertion. If someone deletes the `RetryPolicy`
line, it becomes 16 and the test fails.

**Other timeouts that had to be neutralized:**

- **`HttpClient.Timeout`**, 100 s by default, set to infinite. Otherwise it sits above the
  resilience pipeline and can kill a legitimate retry sequence.
- **The SDK's `NetworkTimeout`**, set to the total timeout plus 5 s, so the pipeline, which
  knows about retries, always decides first.

**Retrying a POST** is accepted deliberately. Chat completions and embeddings have no side
effect beyond cost, so a retry risks paying twice and never risks *doing* something twice.
An endpoint with real side effects would call `DisableForUnsafeHttpMethods()`.

**The attempt timeout is 60 s, not the library's 10 s.** A non-streaming chat completion
sends nothing back until the model finishes. A 10 s attempt timeout would cut off ordinary
long answers and then *retry* them, paying for the same tokens twice and still failing.

---

## 4. Testing resilience with no key: replace the network, not the client

The obvious test double is a fake `IChatClient`. It would test our translation code and
nothing this step actually added: whether retries happen, whether the SDK's retries are
really off, whether a hung connection is cut off.

So `ScriptedHandler` replaces **only the primary handler** of the `eka-ai` client, the
lowest box in §2. Every test request goes through the real resilience handler, the real
SDK and the real `Microsoft.Extensions.AI` adapter. The test container is built with the
same `AddAi` that `EKA.Api` calls. One detail matters for embeddings: the OpenAI SDK asks
for **base64-encoded** vectors to save bandwidth, so the scripted reply honors whichever
encoding the request asked for. A fake that always returned float arrays would pass
against a client real servers would confuse.

| Test | Proves |
|---|---|
| 503 then 200 → success, 2 calls | transient failures are retried |
| 429 then 200 → success | rate limits are retried |
| 503 forever → `ai.unavailable`, **4** calls | one retry layer; exhaustion is a `Result`, not an exception |
| 401/403/400/404 → 1 call | client errors are not retried; a bad key is not hammered |
| hang then 200 → success | the attempt timeout cuts a hung connection and retries |
| hang forever → `ai.timeout` | the total timeout ends the call as a 502 |
| caller cancels → `OperationCanceledException` | a closed browser tab is not a "provider timeout" |
| URI is `…/v1/chat/completions` | the key goes where configuration says |

**The smoke tests** (`Category=Smoke`) run the same `AddAi` against the real provider.
They read `src/EKA.Api/appsettings.json`, then the API's User Secrets, then environment
variables, exactly like the app does. **Without a key they skip with a reason**, unlike the
integration tests, which fail without Docker. Docker is free and expected of every
contributor; a provider key is paid and personal. A suite that fails for everyone without a
paid account is a suite people learn to ignore.

---

## 5. What went wrong

### 5.1 Every configuration error was reported twice

**Symptom:** the first test run had 11 failures. Each expected an
`OptionsValidationException` and got an `AggregateException` holding **the same message
twice**.

**Cause:** `Microsoft.Extensions.Http.Resilience` registers its own `ValidateOnStart()` for
the pipeline options. My `.Configure((resilience, serviceProvider) => …)` callback read
`IOptions<AiOptions>.Value` to get the timeouts. So when the host validated the resilience
options at startup, it ran my callback, which resolved `AiOptions`, which ran
`AiOptionsValidator` a second time. The first copy of the failure came from validating
`AiOptions`, the second from inside the resilience package's validation.

**Fix:** the resilience callback binds `Ai:Resilience` straight from `IConfiguration`, and
never resolves the validated options. `Every_failure_is_reported_once_and_all_at_once` now
asserts exactly two messages for two mistakes.

**The lesson:** resolving a validated options type from inside *another* options type's
configuration callback ties their validations together. It is invisible until something
is misconfigured, which is exactly when an operator least needs a confusing error.

### 5.2 A comment I wrote was wrong, and the test showed it

I wrote that the resilience package validates its options "only when the first request is
made". The run after the fix showed that it validates **at startup**. A bad
`TotalTimeout` produced two *different* messages: ours (naming `Ai:Resilience:TotalTimeout`)
and the library's ("Total request timeout resilience strategy must have a greater
timeout…"). The comment is corrected. Our check stays, because it is the one that names the
configuration key. The test helper now collects every message, from both
`OptionsValidationException` and `AggregateException`, as an operator reading the log would.

### 5.3 Docker was not running

The integration tests failed with "Failed to connect to Docker endpoint". This was the
environment, not the code. They are not optional for this step: they are what proves
`Program.cs` still boots once `AddAi`'s startup validation is in place. They passed (63/63)
once Docker Desktop was started.

---

## 6. Decisions inside the code worth knowing about

- **No defaults in code for the model, endpoint or dimension.** A default would be a
  second, invisible source of truth, and the one most likely to drift. Resilience tuning
  *does* have defaults, because a wrong value there degrades latency rather than
  corrupting data.
- **The dimension guarantee has two halves.** At startup the validator catches the typo:
  a value of 0, or a pasted byte count. At runtime `EmbeddingService` checks every vector,
  which catches the model that ignores the `dimensions` parameter (some compatible servers
  do). Only the runtime half can see what the model actually did.
- **`dimensions` is sent on every request**, not just set as a default. text-embedding-3
  truncates to the requested size (Matryoshka representation), so asking explicitly makes
  1536 a contract rather than an assumption about the model's native size.
- **Count is checked before dimension.** A short response loses index alignment, and every
  vector after the gap would be stored against the wrong chunk's text. That is a silent
  retrieval bug.
- **An empty chat answer is a failure** (`ai.empty_response`), not `""`. Otherwise Step 6
  would persist blank assistant messages.
- **`http://` endpoints are rejected except on loopback.** Over plain HTTP the key travels
  in clear text. Loopback is exempt so a local Ollama works.
- **Unrecognized exceptions are not translated.** A JSON parse failure or a null reference
  is a bug and becomes a 500. Calling it "the provider is down" would send someone to debug
  the wrong system.
- **Error messages to clients are vague on purpose.** The upstream status and body go to
  the log. A client learning which vendor we use, and how it rejected our key, is an
  information leak.
- **The SDK client is a singleton holding one `HttpClient`**, so `IHttpClientFactory`'s
  handler rotation never happens. `PooledConnectionLifetime` on `SocketsHttpHandler` does
  rotation's real job (picking up DNS changes) from inside a long-lived handler. This is
  the documented pattern for a long-lived client.
- **`UseLogging()` logs message content only at Trace level.** Leave Trace off anywhere
  real documents flow; at Trace the log becomes a copy of every prompt.

---

## 7. Alternatives, and when each would win

| Choice | Chosen | Alternative wins when… |
|---|---|---|
| Provider | OpenAI-compatible adapter at OpenRouter ([ADR-0015](../adr/0015-openai-compatible-provider.md)) | Azure OpenAI: private networking, data residency or an enterprise agreement is required |
| Port | Own `IChatCompletionService` ([ADR-0016](../adr/0016-application-owns-ai-ports.md)) | `IChatClient` in use cases: the port is being widened into a copy of it (tools, structured output) |
| Resilience | `AddStandardResilienceHandler` | Hand-built Polly pipeline: a strategy the standard one lacks, such as hedging to a second provider (`AddStandardHedgingHandler`) |
| Retry layer | HTTP handler only | SDK retries only: no other HTTP traffic needs a pipeline, and you accept no circuit breaker |
| Provider selection | One configured provider, Factory over `Kind` | Keyed services per provider: two providers are live at once, for example a cheap model for summaries and a strong one for answers |
| Skippable test | Attribute that sets `Skip` | `Xunit.SkippableFact`, or `Assert.Skip` after a move to xUnit v3 |

---

## 8. What to take away from Step 5

1. **Count your retry layers.** Libraries retry by default, and retries multiply across
   layers. An SDK under a resilience handler is a 16× amplifier until one of them is
   switched off, and only a test that counts requests will notice.
2. **Test resilience at the network boundary, not the interface.** A fake `IChatClient`
   can't time out the way a socket does. Replacing the lowest handler kept every real layer
   in the test.
3. **Validate what you can at startup, and check what you can't on every call.** The
   configuration says 1536; only the response proves it.
4. **Vendor-neutral is not the same as ours.** `IChatClient` doesn't belong to any vendor,
   and it still isn't in the application's vocabulary. Owning the port is what let provider
   failure become a `Result`.
5. **A misconfiguration message is user interface.** Its reader is an operator under
   pressure. It should name the key, report every failure at once, and report each one once.

---

## Next

**Step 6: prompt templates and the chat API.** Versioned prompt templates, conversation
history windowing within a token budget, and message persistence with token metadata. Also
the conversation endpoints deferred from Step 4. `ChatCompletion.InputTokens` and
`OutputTokens` exist so Step 6 has something real to persist.
