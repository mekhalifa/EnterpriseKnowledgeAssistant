# Step 13: Angular 21 client

**Exit criterion:** *a user uploads a document, waits for indexing, asks questions, and inspects
citations.* **Met on the real stack**: Keycloak sign-in, the API, the ingestion worker, SQL
Server, Qdrant, real embeddings and gpt-4o-mini, driven by Playwright through a real browser
(§6).

Decisions: [ADR-0033](../adr/0033-angular-client-pkce-sse-generated-contract.md). Settled with
the owner before any code: `angular-auth-oidc-client`, SSE streaming, Angular Material, the
client in this repository.

---

## 1. What was built

**Backend (small, deliberate):**

| Change | Why |
|---|---|
| `IChatCompletionService.StreamAsync` (default interface implementation = one delta) | Streaming as a side channel; the result is still one `Result<ChatCompletion>` |
| `ChatCompletionService.StreamAsync` over `GetStreamingResponseAsync` + `ToChatResponse` | Text, model and usage from the same fold as the non-streaming call |
| `ProviderFailure`: `IOException` → `ai.unavailable` | A connection dropped mid-body is upstream, and below the retry layer |
| `AskQuestionHandler.StreamAsync` | Same use case, same atomic turn; only the chat call differs |
| `POST /api/conversations/{id}/messages/stream` + `ServerSentEventWriter` | SSE that starts lazily, so pre-stream failures keep their status |
| `Cors:AllowedOrigins` (`BrowserOrigins`), `UseCors()` before authentication | Preflights carry no token; explicit origins, no wildcard, no credentials |
| `CanManage` on `DocumentResponse` / `DocumentSummaryResponse` | The client shows controls from the server's rule, not a copy of it |
| `JsonNumberHandling.Strict` | Integers in the OpenAPI document (and the generated types) are `number`, not `number \| string` |
| Realm: `post.logout.redirect.uris` for `eka-cli` | Keycloak 26 refuses an unregistered logout redirect |

**Client (`src/EKA.Web`)**, Angular 21.2, zoneless, standalone components, signals, Material 3:

```text
src/app/
  core/api/        schema.ts (GENERATED), api.ts (DocumentsApi, ConversationsApi + askStreaming)
  core/auth/       auth.config.ts (code + PKCE, refresh renew, secureRoutes), callback
  core/sse/        ServerSentEventParser, readServerSentEvents
  core/errors/     describeProblem: status + code → one actionable sentence
  shell/           skip link, banner, navigation, main landmark, sign-out (RP-initiated)
  shared/          ErrorBanner (role=alert; retry or sign in again; trace reference)
  features/documents/  upload with progress, polling, live-region announcements, access dialog
  features/chat/       ConversationStore, ChatPage, AnswerText, CitationPanel
openapi/eka-api.json   the checked-in contract
e2e/                   Playwright against the real stack
```

## 2. Streaming without a second code path

The design rule: **streaming changes when the user sees text, not what the application does
with it.** Citations can only be extracted, validated and renumbered from a finished answer
(step-10), the budget needs the usage that arrives last (step-12), and a turn is saved whole or
not at all (step-06). So the port's streaming method returns the same `Result<ChatCompletion>`
and passes deltas to a callback. The handler is unchanged except for which method it calls.

The wire protocol:

```text
event: delta       data: {"text":"You get "}           ← raw model output, zero or more
event: delta       data: {"text":"25 days [3]."}
event: completed   data: { …AskQuestionResponse… }     ← exactly one of these two
event: error       data: { …problem document… }
```

**Why the response starts lazily.** `TypedResults.ServerSentEvents` commits a 200 before the
first item. Every failure that happens before the model writes (wrong conversation, spent
budget, embedding or provider down) would become "200, then an error event", and a client
could no longer tell a 404 from a 502 by status. `ServerSentEventWriter` sets the headers on
the first event, so those keep their status; only a failure after text was shown travels
in-band. A no-evidence refusal never calls the model and is a stream of one `completed`.

**Why the client hides markers while streaming.** Deltas carry the model's numbering (`[3]`);
the server renumbers by first appearance (`[1]`). Showing one then swapping it for the other
would mislead, so markers appear only with the final answer, as buttons.

**Why `fetch` and a hand-written parser.** `EventSource` can only GET and cannot send a bearer
token. The parser is a small state machine tested chunk by chunk: a split CRLF, a multi-byte
character split across network chunks, multiple `data:` lines, comments, a truncated event.

**Disconnects.** Closing the tab or pressing Stop aborts the fetch; ASP.NET cancels the request
token; the provider call is abandoned; nothing is persisted. The same outcome as a failed turn.

## 3. Sign-in, and what the client does not decide

- Code flow + PKCE against the realm's public client. No secret in the browser.
- **`secureRoutes` = the API origin only.** The interceptor never attaches a token to Keycloak,
  a CDN, or a URL in an answer. The streaming call uses `fetch` (the interceptor does not run)
  and attaches the token itself, to the same origin.
- Refresh-token renewal, not a hidden iframe (third-party cookie blocking breaks iframe renew).
  `ignoreNonceAfterRefresh`: Keycloak's refreshed id token has no nonce, correctly.
- Sign-out is RP-initiated: the Keycloak session ends too, otherwise "sign in" would silently
  sign the same person back in.
- **The route guard is a convenience, not a control.** It starts the sign-in redirect instead
  of rendering a page that would collect 401s. `CanManage` hides controls a caller would get a
  403 from. Every decision is still the API's, and the e2e test proves it from the browser (§6).

## 4. Rendering untrusted text

Answer text is model output, and model output is untrusted (threat model LLM05). `AnswerText`
splits it into text runs and citation markers; text runs are interpolated (escaped) and markers
become buttons with an accessible name ("Source 1: Leave policy"). No `innerHTML`, no Markdown —
not even "safe" Markdown, because a link in an answer is exactly what an injected instruction
would try to produce. Tests pin `<img onerror>`, `<script>` and `javascript:` links as visible
characters.

## 5. Accessibility (WCAG 2.2 AA, by construction)

- Landmarks (banner, nav, main, complementary) and a skip link; `aria-current` on navigation.
- One polite live region per page, spoken **once per outcome**: "Leave policy is ready for
  questions", "Answer ready, with 2 sources". Not per delta and not per intermediate ingestion
  stage — a region that talks every few seconds drowns out everything else.
- The streaming answer is `aria-busy`; errors are `role="alert"`.
- Choosing a marker moves focus to its citation in the Sources panel.
- Enter sends, Shift+Enter is a newline, IME composition is left alone.
- Material 3 colour roles for all custom styles (contrast holds in light and dark), visible
  focus rings, `prefers-reduced-motion` honoured. Checked by screenshot at desktop and phone
  width in both schemes.
- No web fonts from a CDN: privacy (no third-party request per page view) and a simpler CSP.

## 6. The exit criterion

`npm run e2e` (Playwright, Chromium, real stack, billed: fractions of a cent):

1. **upload, wait for indexing, ask, and inspect the citation** — Carol signs in through the
   Keycloak form; uploads a Markdown policy with a made-up amount and a unique token; the list
   polls Queued → Ready; she asks; the answer streams and arrives `cited` and containing the
   amount; the URL gains `?c=`; the marker's accessible name is the document title; clicking it
   focuses the citation, which shows the title and the section; a follow-up is answered from the
   same document; after a reload both answers are still there, still cited; she deletes it.
2. **a document restricted to another group never reaches a non-member's answer** — Dana
   (finance) uploads a finance-only fact and restricts it to `group:finance` through the access
   dialog; Carol (hr) asks for it: no citation, no figure, and the Sources panel never names it.

Both pass. In addition: **704 .NET tests** (702 run, 2 billed skipped) and **27 client unit
tests**, all green.

## 7. What went wrong

### 7.1 The local database was at Step 3

The first real-stack run needed the schema of Steps 6–12, and the local database had only
`InitialSchema` — earlier real-stack runs had evidently used a different database. All six
pending migrations applied cleanly over existing rows (the Step 9 and 10 backfills did their job).
Lesson: "the real stack" includes the developer's database; check `migrations list` first.

### 7.2 Two routes would have killed the stream

`/chat` and `/chat/:id` as two routes means the first question (which creates the conversation
and then puts its id in the URL) destroys and recreates the page mid-stream. One route with
`?c=` keeps the component alive. Found by reading the router behaviour before writing the test,
not after.

### 7.3 An effect that aborted its own stream

The page's effect called `store.open(id)`, which reads `store.current()`. Tracked, starting a
conversation (current set, `?c=` not yet) re-ran the effect with `null` and aborted the answer
being streamed. `untracked()` around the call: the effect depends on the URL only. Signals make
dependencies implicit, so a read inside a called method is a dependency too.

### 7.4 Whitespace in a `pre-wrap` template

A bare `{{ text }}` between indented lines is not a whitespace-only node, so the compiler
collapses its surroundings to a space on each side of every run of answer text. Wrapped tightly
in `<ng-container>`, the indentation becomes a separate whitespace-only node and is dropped. A
test pins the exact text.

### 7.5 `number | string` everywhere

The first generated types typed every integer as `number | string`: ASP.NET's web JSON defaults
accept quoted numbers, and the .NET 10 OpenAPI generator says so truthfully. Fixed at the source
(`JsonNumberHandling.Strict`), not with a type cast in the client.

### 7.6 Test assertions that were wrong, not the code

- The password locator matched both the textbox and the "Show password" button.
- The restricted-document test asserted `no_evidence`, but Carol could read leftover cycling
  documents from failed runs, a bonus question is "near" an allowance, and retrieval returned
  them (0.66–0.70). The model then declined to cite them (`uncited`). Not a leak — her search
  never returned the finance document. The assertion now states the property that matters (no
  figure, no finance citation), which does not depend on what else the tenant holds.

### 7.7 Observed, not resolved

- **A 30 s ledger timeout** on the very first run: two documents indexed concurrently, one
  `TenantTokenUsage` MERGE hit the command timeout ("budget under-counted by 99 tokens"), and
  ingestion took ~2.5 minutes. Ten concurrent MERGEs on one key afterwards took < 100 ms, a
  blocking sampler saw nothing in later runs, and indexing took seconds. Not reproduced; logged
  as an open observation for Step 14's load tests.
- **API cold start ~20 s** for the first authenticated request (JWKS, EF model, first
  connections). The first e2e attempt timed out on it. Step 14 readiness/warm-up.

## 8. Alternatives, and when each would win

See ADR-0033 for the full list. The two that matter most:

- **Backend-for-frontend** — when the deployment handles sensitive data and token theft by XSS
  is unacceptable. It is the strongest option; it was not chosen for a local learning client.
- **No streaming** — when a proxy in the path buffers responses, or perceived latency does not
  matter. The JSON route still exists and the client could switch with one method.

Smaller choices: **polling** ingestion status (3 s, only while something is in progress) over
push, because push needs a worker → API channel; **types-only generation** (openapi-typescript)
over generated services, because thin hand-written services keep `HttpClient` interceptors and
are a handful of lines; **raised bundle budget** (initial 528 kB raw / 128 kB transferred, warning
now 600 kB) because the overage is the OIDC client and Material core, both needed at startup.

## 9. What to take away from Step 13

1. **Stream the display, not the decision.** The streamed text is for the eye; what is stored,
   cited and billed is computed from the finished answer, exactly as before.
2. **Status codes are a contract; start the stream late** so failures before it keep theirs.
3. **The client hides; the server decides.** Guards and `CanManage` are courtesy; the e2e test
   proves the restriction from a real browser.
4. **A generated contract needs two gates**: the server's document against a snapshot, the
   client's types against the document.
5. **Model output is text.** No HTML, no Markdown, no clickable links from an answer.

## Running it

```powershell
docker compose -f deploy/docker-compose.yml up -d          # SQL Server, Qdrant, Keycloak
dotnet run --project src/EKA.Api --launch-profile http       # http://localhost:5118
dotnet run --project src/EKA.Ingestion.Worker
cd src/EKA.Web; npm ci; npm start                            # http://localhost:4200, sign in as carol
npm test                                                     # client unit tests (free)
npm run e2e                                                  # real stack, billed (cents)
# After an API contract change:
$env:EKA_OPENAPI_UPDATE="1"; dotnet test tests/EKA.IntegrationTests --filter OpenApi
npm run generate:api                                         # then commit both files
```

## Next

Step 14 — deployment and release readiness: containers for API, worker and web, CSP and the
BFF question, readiness and warm-up, Serilog + OpenTelemetry, CI (including `npm test`,
`check:api`, the OpenAPI snapshot), load and failure-recovery tests (the ledger timeout above).
