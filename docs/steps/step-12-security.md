# Step 12: Security and enterprise controls

**Status:** ✅ Complete
**Date:** 2026-11-16
**Branch:** `step-12-security` (branched from `step-11-evaluation`)
**Result:** 687 tests (685 on every run; 2 billed evaluation tests opt-in), zero warnings.
**The exit criterion was met twice:** 34 security tests over the real token path, SQL Server and
Qdrant, and the real stack with **Keycloak-issued tokens**, real embeddings and a real model (§6).

> *Security tests prove unauthorized content cannot be retrieved.*

Identity used to come from request headers, through a scheme that authenticated nobody
(ADR-0014). It now comes from signed OIDC access tokens: Keycloak locally, Entra ID in
production. Documents gained owners, and management became a privilege separate from reading.
Spending is bounded per user per minute and per tenant per month. Uploads are parsed only
after limits and a scanning port. The threat model names what remains.

---

## 1. What was built

| Piece | Where | Role |
|---|---|---|
| `JwtAuthentication`, `JwtAuthenticationOptions` | `EKA.Api/Security` | JWT bearer from any OIDC issuer; scope + tenant in the policy; startup guards (§2). |
| Development scheme **deleted** | `EKA.Api/Security` | `DevelopmentAuthenticationHandler`, its options and `NoIdentityProviderHandler` removed (ADR-0014 → superseded). |
| Keycloak 26.6.4 + realm | `deploy/docker-compose.yml`, `deploy/keycloak/` | Local IdP: users, groups, `eka.admin`, mappers for `tid`/`groups`/`roles`/audience; `get-token.ps1`. |
| `Document.OwnerSubject`, `CanBeManagedBy`, `IsVisibleTo` | Domain | Owner or tenant admin manages; visibility ≠ content (§3). |
| `AccessScope.SubjectId`, `.IsAdministrator` | Domain | Who is asking, including the admin role. |
| `DocumentAuthorization` | Application | One place turning the rules into 404 or 403. |
| `RateLimiting` | `EKA.Api/Security` | Per tenant+user token buckets on ask, search, upload (§4). |
| `TokenBudget`, `ITokenUsageLedger`, `SqlTokenUsageLedger` | Application, Infrastructure | Monthly budgets; atomic ledger; `GET /api/usage` (admins) (§4). |
| `ExtractionLimits`, `ZipBombGuard`, page and text caps | Infrastructure, Application | Hostile-file limits before parsing gets expensive (§5). |
| `IMalwareScanner` + `NoMalwareScanner` | Application port, Infrastructure | Scan before any parser; infected → fail and delete. |
| `ErrorType.LimitExceeded` → 429 | Application, API | Budget exhausted. |
| Log hygiene | `appsettings.json` | `Microsoft.Extensions.AI` pinned to Information (it logs prompts at Trace). |
| Migrations `DocumentOwnership`, `TenantTokenUsage` | Infrastructure | Owner backfilled from `CreatedBy`; the ledger table. |
| `TestTokens` | Integration tests | Signs tokens the API trusts through its production validation path. |
| `SecurityTests`, `LimitTests`, `UploadHardeningTests`, `LogHygieneTests`, `TokenUsageLedgerTests` | Integration tests | The exit criterion and its neighbours (§6). |
| Threat model | [docs/security/threat-model.md](../security/threat-model.md) | Assets, boundaries, OWASP LLM Top 10 mapping, residual risks. |

---

## 2. Authentication: validate tokens, trust nothing else

**What is validated, and why each:**

| Check | What it stops |
|---|---|
| Signature (keys from the issuer's JWKS) | Forged tokens; "alg: none" |
| Issuer | Tokens from another identity provider |
| Audience `eka-api` | A token issued for another application of the same IdP being replayed here |
| Lifetime, 1 min skew | Expired tokens (the library default tolerates 5 minutes) |
| Scope `eka.access` (`scope` or `scp`) | Users signed in somewhere in the directory but never granted this API |
| `tid` claim | A caller with nothing to filter by |

**Claim names as issued** (`MapInboundClaims = false`). With mapping on, `sub` becomes a long
WS-Fed URI, and code reading `sub` gets nothing: the kind of break that appears after a library
upgrade.

**Configuration refuses to be unsafe.** No Authority or Audience → no start.
`RequireHttpsMetadata=false` outside Development → no start. Signing keys fetched over plain
HTTP can be swapped by anyone in the path, and with them every identity.

**Tests mint real tokens.** `TestTokens` signs with an RSA key, and the factory hands the API
that key as the issuer's metadata (`JwtBearerOptions.Configuration`). The production
validation code runs unchanged, so every negative case (expired, wrong audience, wrong issuer,
foreign key, unsigned, garbage) tests what protects production. No "test mode" switch exists in
the API for a misconfiguration to leave on.

**Proof that nothing downstream changed:** all 121 existing integration tests passed on the
token path on the first run. ADR-0014 predicted this ("one line in `Program.cs` changes and
nothing downstream notices"), and it held.

---

## 3. Owners, administrators, and three different rights

| Right | Who | Used by |
|---|---|---|
| **Read content** | access list (Step 9) | search, answers |
| **See metadata** | read, or manage | get, list |
| **Manage** | owner, or `eka.admin` in the same tenant | rename, reset, delete, change access |

**An administrator manages, and does not read.** They can repair an access list that locks
everyone out, and still get nothing from the HR folder through search or answers unless the list
grants it. On the real stack, `root` got the salary document's metadata (200) and no salary
figures from a search.

**403 versus 404.** A reader who may not manage gets 403: they can already see the document, so a
404 would be a lie. A caller who cannot see it gets 404, identical to "does not exist".

**Owners of existing documents** come from `CreatedBy`, which has held the uploader's subject
since Step 4. Rows written by "system" or "migration" stay unowned, and only an admin can manage
them. That is the safe direction.

---

## 4. Spending: per user per minute, per tenant per month

**Rate limits.** Token buckets on ask (5 burst, 10/min), search (10, 30/min) and upload (5,
10/min), partitioned by tenant **and** subject. The limiter runs **after** authorization, so the
partition key is a verified identity, and anonymous floods get 401 without spending anyone's
allowance. Rejections are 429 with `Retry-After`.

**Budgets.** A monthly token budget per tenant (2M by default, overrides per tenant, 0 suspends).
The check comes **before** any provider call. The **record** follows every chat call, question
embedding and ingestion embedding: the provider's count, or the conservative estimate when none
is reported. Ingestion is recorded and never refused, because a half-indexed document is worse
than an overshoot.

**The ledger is the hard part.** Indexing records batches **concurrently**, so the ledger cannot
use the request's `DbContext`, which is not thread-safe. It opens its own connection and does one
`MERGE … WITH (HOLDLOCK)`. Without HOLDLOCK, two first-of-the-month writes both insert. Without
MERGE, increments are lost. A test fires 50 concurrent increments, including the row's creation,
and checks the exact sum.

---

## 5. Uploads: limits where parsing gets expensive

- **Zip bombs (DOCX).** A ZIP's directory *declares* uncompressed sizes, and a bomb lies about
  them. So the guard decompresses every entry into a counter and stops at the cap (200 MB),
  before the SDK opens the file. A real test document with 8 MB of zeros (under 100 KB on disk)
  is refused at a 1 MB cap. An ordinary document passes.
- **Page bombs (PDF).** The page count is read from the page tree before any page is parsed, with
  a cap of 2,000.
- **Text floods.** A cap of 2,000,000 extracted characters, before chunking and embedding make a
  file expensive.
- **Malware scanning port.** It runs in the worker before any parser. Infected: the document fails
  and the file is deleted. Scanner down: the job retries, and "couldn't scan" is never read as
  "clean". The adapter is a no-op that **warns** once per process, so "no scanner" is visible, not
  silent.

---

## 6. The exit criterion

**In the suite** (`SecurityTests`, 34 cases including theories): every route to content (search,
answers and the prompt behind them, get, list) for every unauthorized caller:

- another tenant;
- the same tenant without the group;
- another tenant *with a group of the same name*;
- an administrator who is not in the group.

A canary string marks the one restricted document, and finding it anywhere is a leak. A
**positive control** (an `hr` member *does* get the canary) proves the assertions can fail.
Plus: no token on five routes; six kinds of invalid token, each reaching no handler; missing
scope; missing tenant; the retired headers ignored; owner and administrator management versus
reader (403) and foreign admin (404); and usage restricted to administrators.

**On the real stack** (Kestrel, worker, SQL, Qdrant, **Keycloak 26.6.4 tokens**, real embeddings
and gpt-4o-mini):

| Action | Result |
|---|---|
| No token / tampered signature | 401 / 401 |
| alice (no `hr`) searches salary band C | 0 hits, nothing leaked |
| alice GETs the salary document by id | 404 |
| carol (`hr`) asks salary band C | cited: "EUR 61,750 to 74,200 per year [1]" |
| alice asks the same | `no_evidence`, nothing leaked |
| bruno (tenant B) searches | 0 hits |
| root (admin, not `hr`) searches salary | nothing leaked |
| carol renames alice's document | 403 |
| alice renames her own | 200 |
| alice deletes carol's (cannot see it) | 404 |
| root GETs the salary document's metadata | 200 |
| alice / root GET usage | 403 / 200, 448 tokens recorded (chat, questions, ingestion) |

The demo database, collection and files were removed afterwards. Keycloak stays in compose.

---

## 7. What went wrong

### 7.1 A configuration binder that silently ignored a suspension

`Budgets:Tenants:{guid} = 0` had **no effect**. The budget test found it. A probe confirmed that
the configuration binder **drops** entries of a `Dictionary<Guid, long>` without an error, while
string keys bind. An operator's "suspend this tenant" would have bound to nothing.

**Fix:** string keys, and a startup validator that rejects any key that is not a GUID.
**Lesson:** configuration that matters needs a test that it *took effect*, not only that it parses.

### 7.2 A test named "anonymous is rejected" asserted 200 for eight steps

Its comment explained that the development scheme let it through and Step 12 would fix it. The
name was true only from today. Tests whose names state an intent the assertion doesn't check are
debts with a due date. This one was paid on time, but only because the comment said when.

### 7.3 The security suite's own mistakes

- **The canary was not unique.** Tenant B was seeded with the same canary text, and tenant B's
  users correctly "leaked" their own document. A canary must mark exactly one document.
- **"No handler ran" counted embedding calls from zero,** but seeding had already made three.
  Now it compares before and after.

### 7.4 Small ones

- `DbContext` concurrency would have broken the ledger under parallel indexing. Designed out
  before it happened (§4).
- A stale `testhost` process locked the build output. Identified as this repo's own, and stopped.
- The development `appsettings` now points at Keycloak. `DevelopmentHeader` is gone, not
  disabled.

---

## 8. Alternatives, and when each would win

| Choice | Alternative | It wins when |
|---|---|---|
| Generic OIDC + Keycloak locally | Entra ID only | A tenant and app registration exist, and local runs can sign in online |
| Owner + admin manage | Admins only | A curated knowledge base fed by a small team |
| Admin manages, cannot read | Admin reads all | Never, under least privilege |
| Token bucket | Fixed window | Simplicity matters more than burst behaviour |
| Soft budget | Reservations | Overshoot costs real money |
| Measure decompression | Trust ZIP headers | Never: bombs lie about headers |
| Scanning port + no-op | ClamAV now | The deployment can run and update a scanner |

---

## 9. What to take away from Step 12

1. **Validate the token; trust nothing else in the request.** The tenant, the groups and the roles
   come from a signature, never from a header.
2. **Reading and administering are different privileges.** An administrator who can fix an access
   list should not thereby read the folder.
3. **Test the production validation path with your own keys,** not a test-only authentication mode
   that a misconfiguration could leave on.
4. **Prove configuration takes effect.** A binder dropped a suspension silently, and only a test of
   the behaviour noticed.
5. **Put a canary in what must not leak, and a positive control beside it.** A leak test that
   cannot fail proves nothing.

---

## Next

**Step 13: Angular 21 client.** Sign-in with the authorization-code flow and PKCE against the same
Keycloak client (`eka-cli`, redirect `http://localhost:4200`), a typed API client, conversations
with citations rendered as **text** (LLM05), upload with ingestion status, and accessibility.
