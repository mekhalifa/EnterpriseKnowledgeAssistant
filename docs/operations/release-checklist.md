# Release checklist and runbook

The operational half of Step 14's exit criterion: *a reproducible deployment and a completed
release checklist*. The deployment is the single-host Compose stack of
[ADR-0034](../adr/0034-single-host-compose-deployment.md); how it was built and what broke on
the way is in [step-14](../steps/step-14-deployment.md).

The checklist has two parts:

- **Part A: every release.** Gates that run each time. All passed on 2026-11-17 for the
  Step 14 release, with the evidence written next to each one.
- **Part B: before the stack leaves this machine.** These items are acceptable *only* because
  every published port is bound to loopback. They are not done, by design, and each one
  blocks any deployment another machine can reach.

---

## Part A: every release

### A1. Build and test gate (CI runs the same: `.github/workflows/ci.yml`)

| # | Check | Command | 2026-11-17 |
|---|---|---|---|
| A1.1 | Locked restore: packages match `packages.lock.json` exactly | `dotnet restore --locked-mode` | ✅ after regenerating six lock files (step-14 §6.1) |
| A1.2 | Formatting gate | `dotnet format --verify-no-changes` | ✅ after three fixes (§6.2) |
| A1.3 | Build, warnings as errors | `dotnet build` | ✅ 0 warnings |
| A1.4 | All non-billed tests (integration needs Docker) | `dotnet test --filter Category!=Smoke` | ✅ 726 passed, 2 billed skips |
| A1.5 | Client contract matches the API snapshot | `npm run check:api` (src/EKA.Web) | ✅ |
| A1.6 | Client unit tests and production build | `npm test && npm run build` | ✅ 31 passed |
| A1.7 | Retrieval evaluation at or above baseline (tier 1, free) | part of A1.4 | ✅ |
| A1.8 | Answer evaluation (tier 2, billed, ~5 cents) | `$env:EKA_EVAL_ANSWERS="1"; dotnet test tests/EKA.Evaluation --filter Category=Evaluation` | ⏳ deferred: needs key credit (PROGRESS) |

### A2. Release (deploy)

```powershell
# Secrets come from the environment (or deploy/.env, never committed). The four required:
# MSSQL_SA_PASSWORD, KEYCLOAK_ADMIN_PASSWORD, EKA_AI_API_KEY, REDIS_PASSWORD.
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml up -d --build --wait --wait-timeout 600
```

| # | Check | How | 2026-11-17 |
|---|---|---|---|
| A2.1 | `up --wait` exits 0: every service healthy, `migrate` exited | the command above | ✅ |
| A2.2 | Migrations ran once, before the apps (the API never migrates itself) | `docker inspect eka-migrate-1 --format '{{.State.ExitCode}}'` → `0`; its log | ✅ applied; idempotent on redeploy ("already up to date") |
| A2.3 | Both API replicas ready (warm-up done) | `docker inspect eka-api-1 --format '{{.State.Health.Status}}'` (and `-2`) | ✅ |
| A2.4 | Web tier healthy, not restarting | `docker inspect eka-web --format '{{.State.Health.Status}} {{.RestartCount}}'` | ✅ after fixing a crash loop `--wait` did not see (§6.3) |
| A2.5 | Only loopback ports published; API, Redis and ClamAV unpublished | `docker compose … ps`; `docker port eka-api-1` is empty | ✅ 8088, 18888 on 127.0.0.1 only |

### A3. Smoke test (about 2 minutes, under a cent)

| # | Check | 2026-11-17 |
|---|---|---|
| A3.1 | `http://127.0.0.1:8088/` carries the CSP (`script-src 'self'`, `connect-src` = self + identity provider), `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Permissions-Policy`, COOP | ✅ |
| A3.2 | `index.html` has no inline script and no inline event handler | ✅ 0 found |
| A3.3 | `/config.json` names the deployment's authority, same-origin API | ✅ |
| A3.4 | `/api/documents` without a token → 401; with a token → 200 | ✅ |
| A3.5 | No OpenAPI document in Production (`/openapi/v1.json` is 404 at the API; nginx answers it with the SPA) | ✅ |
| A3.6 | Upload a text file → `Completed` with ≥ 1 chunk | ✅ ~6 s |
| A3.7 | Upload the EICAR test string → `Failed`, reason names the signature, first attempt, never searchable | ✅ `Eicar-Test-Signature`, `failureCount: 1` |
| A3.8 | Ask about the uploaded file → streamed answer (several `delta` events at distinct times, then `completed`) with a citation to it | ✅ 23 deltas, correct, cited |
| A3.9 | A user of another tenant sees none of these documents | ✅ bruno: 0 |

### A4. Load and failure recovery (run when the deployment shape changes)

| # | Drill | Expected | 2026-11-17 |
|---|---|---|---|
| A4.1 | k6 `deploy/tests/load.js`: 40 req/s of reads for 60 s | p95 < 500 ms, < 1 % errors | ✅ p95 24 ms, 0 % of 2,401 |
| A4.2 | Same run: 40 simultaneous searches by one user | ≤ 11 admitted across both replicas (shared Redis buckets) | ✅ 10 admitted, 30 × 429 |
| A4.3 | `docker stop eka-api-1` during A4.1, start it 20 s later | No failed reads; nginx retries the other replica | ✅ 0 errors of 2,381 reads; refused connects retried |
| A4.4 | `docker stop eka-redis` | Ready stays 200 with `Degraded` and a reason; limits fall back per instance | ✅ 20 of 30 admitted (10 per replica); recovered on start |
| A4.5 | `docker stop eka-worker-1`, upload, start it | Upload accepted (`Pending`), processed after the worker returns | ✅ `Completed` 9 s after restart |
| A4.6 | `docker stop eka-sqlserver` | Liveness 200 (no restart), readiness fails, recovery without restarting the API. Since 2026-11-18 also: reads `503 database.unavailable`, readiness answers `503` naming `sql-server` within 5 s ([ADR-0037](../adr/0037-database-unavailable-is-503-bounded-readiness.md)) | ✅ both replicas `unhealthy`, 0 restarts, first read after return 200. The 2026-11-18 additions are proved by integration tests, **not yet re-drilled** on this stack |
| A4.7 | 8 simultaneous uploads by one tenant | All `Completed`, no retries | ✅ 23 s, 0 failed attempts |
| A4.8 | Provider unreachable | A problem response (`ai.unavailable`), not a hang | ✅ 502, but after 94 s; connect timeout added (§6.5) |

Load test, from the repository root:

```powershell
docker run --rm -i --network eka_default -e EKA_PASSWORD=local-dev-only `
  -v ${PWD}/deploy/tests:/scripts grafana/k6:1.4.2 run /scripts/load.js
```

---

## Part B: before the stack leaves this machine

Each item is a deliberate shortcut of the single-host deployment (ADR-0034). **Any one
of them left open blocks exposing a port beyond loopback.**

| # | Item | Why it blocks | Where |
|---|---|---|---|
| B1 | **TLS at the edge** + `Strict-Transport-Security` + `upgrade-insecure-requests` in the CSP | Tokens and documents cross the network in clear text | `deploy/web/default.conf.template` or a TLS proxy in front |
| B2 | **A production identity provider**: Entra ID or a managed Keycloak, HTTPS metadata; drop `AllowHttpMetadataOnPrivateNetwork` and `RequireHttpsMetadata=false` | Keycloak runs `start-dev` on H2 with a bootstrap admin | ADR-0031, ADR-0034 |
| B3 | **Disable the password grant** and remove the shared test users (`local-dev-only`) | Anyone with the realm URL can mint tokens for known users | `deploy/keycloak/eka-realm.json` |
| B4 | **Least-privilege SQL login** (no `sa`); `TrustServerCertificate=False` with a real certificate | The applications own the server | Connection strings in `docker-compose.app.yml` |
| B5 | **Qdrant API key** (`VectorStore:ApiKey`) | Vectors and payloads are readable by anything on the network | `docker-compose.yml`, both apps |
| B6 | **Secrets from a secret store** (Key Vault, Docker secrets), not `deploy/.env` | A file on disk is one backup away from a leak | ADR-0034 |
| B7 | **Data processor approval** for the model provider | OpenRouter sees prompts and retrieved document text | ADR-0015, PROGRESS decisions |
| B8 | **Durable telemetry** (collector + a backend with retention) | The Aspire dashboard keeps everything in memory; an incident's evidence dies with the container | ADR-0035 |
| B9 | **Backups and a tested restore** of SQL Server, Qdrant and the `eka-files` volume | No backup exists; a lost volume is lost documents | — |
| B10 | **A second host or a managed platform** if downtime matters | One host is a single point of failure; two replicas only cover a crashed process | ADR-0034 *Revisit when* |
| B11 | **Hangfire dashboard behind authorization**, or not exposed at all | Not wired today; if wired naively it shows every job's arguments | PROGRESS decisions |
| B12 | **Tier-2 evaluation green** (A1.8) with the three-vote judge | Answer quality is otherwise unmeasured for the release | ADR-0030 |

---

## Runbook

### Roll forward

The release is the A2 command. Images are rebuilt from the checkout, `migrate` applies
pending migrations and exits, then `api` (two replicas), `worker` and `web` are recreated.
Compose starts `web` only once an API instance is ready.

### Roll back

1. `git checkout <previous release commit>`, then the A2 command. The images are rebuilt
   from that commit.
2. **Migrations do not roll back by themselves.** A release whose migration removed or
   renamed a column cannot simply run the previous image. Before such a release, write the
   migration as expand / contract across two releases, or generate the down script ahead of
   time:
   `dotnet dotnet-ef migrations script <new> <previous> --project src/EKA.Infrastructure --startup-project src/EKA.Api`.

### Reading the system

| Question | Where |
|---|---|
| Is it up? | `docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml ps` (needs `EKA_AI_API_KEY` set, see below) |
| Why is an API instance not ready? | `docker exec eka-api-1 bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080; printf "GET /health/ready HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n" >&3; cat <&3'`, one entry per dependency |
| Logs, traces, metrics | `http://127.0.0.1:18888`; the login token is in `docker logs eka-otel` |
| Raw logs | `docker logs eka-api-1`: one JSON object per line |
| Was a request retried at the edge? | `docker logs eka-web`: `connect() failed … while connecting to upstream` |

**Known log entries that are not incidents:**

- `Health check warm-up with status Unhealthy … 'Warming up.'` (Error level) during the first
  second of each API start. This is the warm-up gate holding readiness back on purpose.
- `Redis is unreachable; rate limits are per instance until it returns.` Degraded, not
  down. Find out why Redis is gone, but users are not affected.

**Every `docker compose` command over the app file needs `EKA_AI_API_KEY` in the environment**,
even `ps` and `logs`. The required-variable guard sits in a shared block that Compose
interpolates for every command. Run them where the deployment's environment is set.

### Recovery

| Symptom | Action |
|---|---|
| Documents stuck `Failed` after an outage | Fix the cause, then reset each one: `POST /api/documents/{id}/reset` (owner or administrator) |
| A replica `unhealthy` | Read its readiness report (above). SQL Server or Qdrant down: fix that; the instance recovers by itself, so do **not** restart it |
| `web` restarting | `docker logs eka-web`: an `[emerg]` line is a configuration error (step-14 §6.3) |
| ClamAV `starting` for minutes | First start downloads ~300 MB of signatures (up to 10 min); the worker waits for it |
