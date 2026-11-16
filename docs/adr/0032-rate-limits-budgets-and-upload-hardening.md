# ADR-0032: Per-user rate limits, per-tenant monthly token budgets, and hardened uploads

- **Status:** Accepted
- **Date:** 2026-11-16
- **Deciders:** project owner

## Context

An enterprise assistant spends money on every question and parses files that anyone in a
tenant may upload. Until now, nothing bounded how fast one person could spend, how much a
tenant could spend in a month, or how much work a hostile file could cause. The owner chose
both per-user limits and tenant budgets, and hardened parsing behind a scanning port.

## Decision

**Rate limits** (ASP.NET Core rate limiter)

- Token buckets on `ask` (burst 5, 10/min), `search` (10, 30/min) and `upload` (5, 10/min).
- Partitioned by tenant and subject, so one person cannot slow a colleague.
- Run after authorization, so anonymous traffic is refused with 401 without spending anyone's
  allowance.
- Rejections are **429** with `Retry-After` and code `rate_limited`. Requests are never queued.
- Configured under `RateLimiting:*`.

**Monthly token budgets per tenant**

- `Budgets:MonthlyTokens` (2,000,000 by default), with `Budgets:Tenants:{tenantId}`
  overrides. Zero suspends a tenant.
- `ask` and `search` check the budget **before** any provider call, and refuse with **429**
  `usage.budget_exhausted`.
- Every chat call, every question embedding and every ingestion embedding is **recorded**: the
  provider's count, or the conservative estimate when none is reported. Ingestion is
  recorded but never refused, because a half-indexed document is worse than an overshoot.
- The ledger (`TenantTokenUsage`) is one atomic `MERGE … WITH (HOLDLOCK)` per call, on its own
  connection, outside the request's transaction. Tokens are counted when they are paid
  for, even if the request then fails.
- `GET /api/usage` reports usage to administrators only.

**Uploads**

- Measured, not declared: a DOCX is decompressed into a counter before the SDK opens it (200 MB
  cap, at most 2,000 parts).
- PDFs over 2,000 pages are refused from the page tree, before any page is parsed.
- Text over 2,000,000 characters is refused before chunking and embedding.
- All three are permanent failures with a reason the user can act on.
- **`IMalwareScanner`** runs in the worker before any parser. The adapter is `NoMalwareScanner`,
  which logs a warning once. An infected file fails the document and is deleted. A scanner
  outage throws and is retried, never read as clean.

## Consequences

### Positive

- One user is bounded per minute, one tenant per month, one file per parse. A runaway script
  stops at the budget, not at the invoice.
- A zip bomb and a page bomb are refused by the real extractors (tested with generated files),
  and an infected file can never reach a parser once a scanner is plugged in.

### Negative / costs we accept

- **The budget is soft.** Concurrent requests that pass the check together overshoot by what
  they cost: a few thousand tokens. A hard limit needs reservations.
- **Rate limits are per process.** Several API instances each allow the full rate. A shared
  limiter (Redis) is needed once the API is scaled out (Step 14).
- **No real malware scanning yet.** The port is there, and the adapter is a no-op that warns.
- **Usage is tokens, not money.** Prices differ by model and change over time, so converting
  is left to a report.
- **Budgets live in configuration,** so changing one means a configuration change, not an API
  call. The usage endpoint reads; there is no admin endpoint that writes.

## Alternatives considered

- **Fixed-window rate limits.** Simpler to explain. They let twice the limit through at a window
  edge, and allow no natural burst.
- **Hard budgets with reservations.** Exact. It wins when overshoot costs real money (large
  tenants, expensive models). It means reserve-before and release-after on every call.
- **Budgets in a table with an admin API.** It wins for a SaaS with self-service plans. It is a
  new surface to secure and audit.
- **ClamAV in compose now.** Real scanning. It brings a daemon, a 200 MB signature database
  refreshed daily, and its own outages. It is the obvious adapter to add at deployment.
- **Trust the ZIP directory sizes.** Cheaper. A zip bomb lies about exactly those numbers.

## Revisit when

- More than one API instance runs: move the limiter to Redis.
- A tenant's overshoot matters financially: reserve tokens before calls.
- The deployment is chosen: plug in a scanner (ClamAV, Defender for Storage).
- Real usage data exists: tune the buckets and the default budget.
