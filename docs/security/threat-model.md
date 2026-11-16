# Threat model: Enterprise Knowledge Assistant

**As of:** Step 12, 2026-11-16. Revisit at every step that adds an entry point, a store or a
provider.

## What is protected

| Asset | Why it matters |
|---|---|
| Document content (chunks, files, extracted text) | The organization's knowledge, including HR, finance, legal. **The primary asset.** |
| Document metadata (titles, access lists) | Titles leak by themselves ("Layoffs Q3"). |
| Questions and answers | As sensitive as the documents: they reveal what people are looking for. |
| Provider budget | Every question costs money. |
| Identity and tenant boundary | Everything above is partitioned by it. |

## Trust boundaries

```text
 Browser / client ──(1)──► API ──(2)──► SQL Server, Qdrant, file storage
        │                   │ └──(3)──► AI provider (OpenRouter → model vendor)
        └──(0)──► IdP       └── jobs ──► Worker ──(2)(3)── parsers ◄──(4)── uploaded files
```

- (0) the identity provider is trusted to issue tokens. Nothing else about the caller is.
- (1) everything in a request except a validated token is attacker-controlled: body, headers,
  query, file bytes, file names.
- (2) stores are trusted, and reachable only from loopback locally.
- (3) the provider sees prompts, and with them retrieved document text (ADR-0015).
- (4) uploaded files are hostile until proven otherwise, and parsers are their attack surface.

## OWASP Top 10 for LLM Applications (2025), mapped

| Risk | How it applies here | Controls | Proven by |
|---|---|---|---|
| **LLM01 Prompt injection** | Document text is pasted into prompts; a document can carry instructions (indirect injection). | Document text only in the user turn, in `<source>` delimiters it cannot forge; system turn is template text only; v2 instruction treats sources as data. | `SourceRendererTests`, `Instructions_planted_in_a_document_...` (E2E); evaluation `injection` questions (canary never in answers); real stack: planted "PWNED" ignored (step-10). **Residual:** model compliance is not guaranteed. |
| **LLM02 Sensitive information disclosure** | Retrieval could surface another tenant's or group's content; logs could hold questions. | ACL inside the vector query, re-checked in SQL; tenant in every repository method; no question or document text logged; `Microsoft.Extensions.AI` pinned to Information. | `SecurityTests` (every route × every unauthorized caller, canary); `LogHygieneTests`; evaluation hard rules. |
| **LLM03 Supply chain** | NuGet packages execute at build; container images. | Package source mapping (ADR-0006), NuGet audit as errors, transitive pinning; all images pinned to exact versions. | Build fails on NU1903 (seen in Step 7). **Residual:** no lock files yet (Step 14). |
| **LLM04 Data and model poisoning** | A user uploads a document written to mislead answers for colleagues. | Uploads attributed (owner); access lists limit audience; answers cite sources so a reader can judge. | **Residual:** no content review; authority score exists but is not used in ranking. |
| **LLM05 Improper output handling** | Answers rendered in a browser (Step 13) could carry markup. | API returns plain text; citation markers parsed, invented ones removed. | `CitationMarkerTests`. **Residual:** Step 13 must render as text, never HTML. |
| **LLM06 Excessive agency** | The assistant could act (send mail, change data). | It has no tools: read-only retrieval and text generation. | By construction. |
| **LLM07 System prompt leakage** | A user or document asks the model to reveal its instructions. | The system prompt holds no secrets (no keys, no other tenants' data); leaking it is harmless by design. | Prompt is in a public migration. |
| **LLM08 Vector and embedding weaknesses** | Cross-tenant vectors in one collection; stale access payloads. | Tenant filter structural (refused if missing); access in payload *and* SQL; orphan and mid-ingestion vectors dropped by re-check. | `QdrantVectorStoreTests`, `RetrievalEndToEndTests`, `ChunkFilterTests`. |
| **LLM09 Misinformation** | Answers that sound right and are not. | Grounded answers only; refusal on no evidence; citations; groundedness measured. | Evaluation tier 2 (judge calibrated); `ConversationHandlerTests`. **Residual:** single-vote judge; cited over-reach possible. |
| **LLM10 Unbounded consumption** | Floods of questions; huge files; runaway scripts. | Per-user rate limits; per-tenant monthly token budget; output cap; question length; upload, page, decompression and text limits. | `LimitTests`, `UploadHardeningTests`, `TokenUsageLedgerTests`, application budget tests. |

## Classic web risks, briefly

| Threat | Control | Proven by |
|---|---|---|
| Forged / expired / foreign tokens | JWT validation: signature from JWKS, issuer, audience, lifetime (1 min skew), required scope and tenant. | `A_token_that_fails_validation_is_401...` (6 cases), scope and tenant 403 tests. |
| Tenant spoofing through request content | Tenant only from the token; the old headers are ignored. | `The_retired_development_headers_change_nothing`. |
| IDOR (guessing document ids) | Not visible → 404, identical to non-existent. Visible but not manageable → 403. | `SecurityTests` management cases. |
| Privilege escalation via self-edited claims | `tid` is an admin-only attribute in Keycloak's user profile. | Realm configuration (review). **Residual:** depends on IdP configuration. |
| Keys over plain HTTP | `RequireHttpsMetadata` cannot be off outside Development (startup refuses). | Code review (startup guard). |
| Secrets in source | Keys in User Secrets / environment / vault; compose passwords from gitignored `.env`. | Repository review. |
| Malware in uploads | Scanning port before any parser; infected → document fails, file deleted. | `An_infected_file_fails...`. **Residual:** no scanner adapter configured (warns at runtime). |

## Accepted residual risks (owner decisions)

1. **Third-party data path.** Prompts with document text go to OpenRouter and on to the model
   vendor (ADR-0015). Acceptable for learning data; a data-processing decision is needed before
   real documents.
2. **Administrators see restricted documents' titles** (to manage them), not their content.
3. **Group overage** (Entra ID, more than 200 groups) yields no groups: fails closed.
4. **Tokens are valid until expiry** (5 minutes) after a user is disabled.
5. **Soft budget:** concurrent requests can overshoot by a few thousand tokens.
6. **Per-process rate limits** until the API is scaled out with a shared store.
7. **Prompt injection is mitigated, not solved.** The boundary is enforced; obedience is the model's.
