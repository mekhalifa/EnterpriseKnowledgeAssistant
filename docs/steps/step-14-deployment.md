# Step 14: Deployment and release readiness

**Exit criterion:** *a reproducible deployment and a completed release checklist.* **Met.**
The whole system comes up from the repository with one command:
`docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml up -d --build --wait`.
That gives migrated, warm, observable, two API replicas behind an edge that sets the CSP. The
[release checklist](../operations/release-checklist.md) records every gate and drill, with the
measured result next to each one. The checklist also lists twelve items that block exposing
the stack beyond this machine (§8).

Decisions:
- [ADR-0034](../adr/0034-single-host-compose-deployment.md): Compose on one host, CSP-hardened
  SPA, ClamAV, release shape.
- [ADR-0035](../adr/0035-opentelemetry-without-serilog.md): OpenTelemetry, *without* Serilog.
- [ADR-0036](../adr/0036-shared-rate-limits-with-local-fallback.md): rate limits shared in
  Redis, with a local fallback.

The three open decisions from `PROGRESS.md` were settled with the owner before any code, each
on the recommended option.

Concepts this step makes concrete: **the twelve-factor app** (§2), **the three pillars of
observability** (§4), **graceful degradation** (§5).

---

## 1. What was built

| Piece | Where | What it does |
|---|---|---|
| Images | `deploy/docker/dotnet.Dockerfile` (targets `api`, `worker`, `migrator`), `deploy/docker/web.Dockerfile` | One restore and one compile for all .NET images; base images pinned to exact versions; non-root users |
| Deployment | `deploy/docker-compose.app.yml` over `docker-compose.yml` | `migrate`, `api` ×2, `worker`, `web`, `redis`, `clamav`, `otel`; its own database, collection and file volume |
| Release step | `migrator` target: an EF Core migration bundle | Runs once and exits; the apps start only after it succeeded |
| Readiness | `/health/ready` (SQL Server, Qdrant, warm-up; Redis as Degraded); `/health/live` | Traffic reaches only warm instances; liveness never depends on a dependency |
| Warm-up | `src/EKA.Api/Operations/StartupWarmup.cs` | Pays identity metadata, the EF model and the connections before "ready". Step 13's ~20 s first request is gone |
| Edge | `deploy/web/default.conf.template` | nginx serves the client, proxies `/api/` without buffering (SSE), sets the CSP and the other security headers |
| Runtime client config | `deploy/web/*.sh`, `src/EKA.Web/src/app/core/config/` | `/config.json` written at container start: one build for every environment |
| Observability | `src/EKA.Infrastructure/Observability/Telemetry.cs` | JSON logs, traces and metrics over OTLP to the Aspire dashboard |
| Shared rate limits | `src/EKA.Api/Security/SharedRateLimitStore.cs`, `RateLimiting.cs` | Token buckets in Redis, per-process fallback |
| Malware scanning | `src/EKA.Infrastructure/Extraction/ClamAvMalwareScanner.cs` | clamd `INSTREAM` over TCP, before any parser |
| Exhausted retries | `src/EKA.Application/Documents/FailExhaustedIngestion/` | The last failed attempt marks the document `Failed`, so it can be reset |
| Identity on the container network | `JwtAuthentication.cs` | Metadata address separate from the issuer; the plain-HTTP exception is narrow and tested |
| Supply chain + CI | `packages.lock.json` ×12, `.github/workflows/ci.yml` | Locked restore, format gate, build, tests, client gates, image builds, compose validation |
| Load test | `deploy/tests/load.js` (k6) | Read throughput and the shared limiter, without a single billed call |
| Release checklist | [docs/operations/release-checklist.md](../operations/release-checklist.md) | Gates, smoke test, drills, the not-yet list, runbook |

---

## 2. Twelve factors, applied rather than recited

The useful way to read the twelve-factor app is as a list of things that break when a
service moves from "runs on my machine" to "runs from an image". Each one here was a concrete
change:

- **Config in the environment (III).** Connection strings, keys and endpoints arrive as
  environment variables (`ConnectionStrings__EkaDatabase`, `Ai__ApiKey`, `OTEL_*`). A missing
  secret stops the deployment *by name*: `${EKA_AI_API_KEY:?…}` in Compose, `ValidateOnStart`
  in the hosts. The client follows the same rule. Its authority used to be compiled in
  (`environment.ts`, deleted). Now the container writes `/config.json` at start, so the image
  CI builds is the image that runs everywhere.
- **Backing services as attached resources (IV).** Redis is optional. With
  `ConnectionStrings:Redis` the buckets are shared, and without it they're per process. The
  code doesn't change; only the configuration attaches the store.
- **Build, release, run (V).** *Build* is the images. *Release* is the `migrate` job, which
  runs exactly once and before anything else. *Run* is the apps, which never migrate
  themselves. "Migrate on startup" is the alternative, and with two replicas it would make
  them race to change the schema.
- **Disposability (IX).** Fast, safe stops and starts. The drills in §5 stop each component
  under load.
- **Logs as event streams (XI).** One JSON object per line on stdout. Where those lines go is
  the environment's business, not the application's.

---

## 3. The edge: one origin, and a CSP that costs nothing

nginx serves the Angular build and proxies `/api/`, so the page and the API share one
origin. That has three consequences:

1. **No CORS at all** in the deployment. The API's allowed-origin list stays empty.
2. **The CSP can be tight.** `script-src 'self'` with no inline script, `connect-src 'self'`
   plus the identity provider, `frame-ancestors 'none'`, `object-src 'none'`. This is the
   chosen alternative to a backend-for-frontend (ADR-0033). The tokens stay in
   `sessionStorage`, and the CSP is what limits what injected script could do with them.
   Angular's critical-CSS inlining had to go: it emits an inline `onload` handler this
   policy would block. The smoke test counts inline scripts and handlers in `index.html`
   (zero).
3. **The proxy must not buffer.** `proxy_buffering off` on `/api/`, or the streamed answer
   arrives in one piece at the end. In the drill the answer arrived as 23 `delta` events at
   15 distinct times.

`Referrer-Policy: no-referrer` is not decoration. The sign-in callback URL carries the
authorization code, and a `Referer` header would hand it to any third-party resource.

---

## 4. Observability: three pillars, one library

ADR-0035 records the most instructive decision of the step: the plan said "Serilog +
OpenTelemetry", and the build kept only OpenTelemetry. Serilog's host integration
**replaces** the logging factory, so `Logging:LogLevel` stops applying. One of those levels
is a security control: `Microsoft.Extensions.AI` pinned at `Information`, because at `Trace`
it logs whole prompts, which means document text (Step 12). Keeping both would mean
maintaining that rule in two places, and two copies of a rule eventually drift apart.

- **Logs:** `Microsoft.Extensions.Logging`, JSON formatter outside Development.
  OpenTelemetry is one more provider, so every filter covers every sink.
- **Traces:** one upload is one trace in the worker (job span → SQL, Qdrant, embedding);
  one question is one trace in the API with the model call's tokens on it. A custom root
  sampler starts traces only at entry points. Without it, Hangfire's queue polling produced
  a trace every second.
- **Metrics:** runtime, ASP.NET Core, Kestrel, rate limiting, and the AI middleware's
  token counts.

The drill also showed what observability is *for*. When the streamed answer failed (§6.5), the
cause sat in one warning among hundreds of identical `SELECT 1` lines from the readiness
probe. The API now logs EF commands at Warning outside Development (§6.6).

---

## 5. Graceful degradation, proved by breaking things

Each drill stops one component while the system is in use and records what users would see.
The results are in the release checklist, section A4:

| Stopped | What happened | Why |
|---|---|---|
| One API replica, under 40 req/s | **0 failed reads** of 2,381 | nginx re-resolves `api` every 10 s and, on a refused connection, retries an idempotent request on the next address |
| Redis | Ready stays 200, `Degraded` with the reason; limits per instance (20 of 30 admitted instead of 10) | ADR-0036: a store that only tightens limits must not take the product down. The monthly token budget still caps spending |
| The worker | Upload accepted as `Pending`, processed 9 s after the worker returned | The queue is in SQL Server; the worker is disposable |
| SQL Server | Liveness 200, readiness failing, 0 restarts; first read after its return succeeded | A dead dependency is not a dead process. Restarting the API would add a cold start and fix nothing |
| The provider's network | A 502 `ai.unavailable` problem, not a hang or crash, but only after 94 s | Fixed: §6.5 |

**Liveness versus readiness** is the distinction this step makes concrete. *Liveness* asks
whether the process should be killed, so it must never depend on anything outside the process.
*Readiness* asks whether traffic should be sent here, so it checks dependencies, and it also
stays false during warm-up. Mixing the two up gives the classic outage: the database blips,
every instance fails liveness, the orchestrator restarts all of them at once, and they come
back cold into a stampede.

---

## 6. What went wrong

The code arrived at this session complete but unverified. Running it found six defects, and
none of them were in the code that the unit tests cover.

### 6.1 The lock files had a Windows runtime baked in

`dotnet restore --locked-mode` failed with **NU1004** in six projects. The cause was a
`-r win-x64` operation against `EKA.Api` (a publish or an EF bundle), which had rewritten the
lock files of the API and its dependency closure with a `net10.0/win-x64` target. Locally the
lock files were "wrong" but nothing complained, because locked mode is enforced only under
`ContinuousIntegrationBuild`. CI on Linux and the image build both run locked, and both would
have failed. Fixed with `dotnet restore --force-evaluate`, which regenerated them from the
projects as declared.
**Lesson:** a lock file records the *restore* that produced it, not only the package graph.
Never commit lock files after a RID-specific restore.

### 6.2 The format gate would have failed CI

`dotnet format --verify-no-changes` reported two using directives out of alphabetical order
and one unused `using OpenTelemetry.Logs`. The build passed because those rules aren't build
errors. **Lesson:** a CI gate that has never run locally is a guess.

### 6.3 The web tier crash-looped while Compose said "Healthy"

nginx refused to start with `unknown "eka_auth_origin" variable`. The log line before it
gave the cause: `Ignoring /docker-entrypoint.d/10-eka-config.envsh, not executable`. The
script derives `EKA_AUTH_ORIGIN` for the CSP and is *sourced*, not run. The Dockerfile copied
it with `--chmod=0644`, on the reasoning that a sourced file needs no execute bit. **The
nginx image's entrypoint sources a `*.envsh` only if it is executable.** Without the
variable, `envsubst` left `${EKA_AUTH_ORIGIN}` in the configuration, and nginx read it as one
of its own variables.

Worse, `up --wait` reported `eka-web Healthy`. The service had **no healthcheck**, so all
Compose could say was "started". Two fixes:
- `--chmod=0755`, with a comment explaining it.
- A healthcheck that fetches `/config.json`. That file exists only once nginx accepted its
  configuration *and* the runtime-config script ran.

**Lesson:** a service without a healthcheck reports "started" as "healthy", and `--wait` can't
tell the difference.

### 6.4 The load test measured the wrong thing

The first k6 run failed its error threshold with **21 % errors**. All of them were
`/api/usage → 403`. That endpoint is administrators only (Step 12), and the test's users
are not administrators. The API was right; the script had never been run. It now reads the
conversation list, which is what the client fetches on every page.
**Lesson:** break down failures by status code before concluding anything. "21 % errors" was
100 % one correct 403.

### 6.5 An unreachable provider held the user for 94 seconds

During the streaming drill, the API container briefly lost its route to the provider:
connections refused, then DNS failures. The application behaved correctly, retrying and then
answering `502 ai.unavailable`. The problem was the 94 s it took. Each connect attempt ran
about 41 s, the operating system's TCP connect timeout, because the only bound was the 60 s
attempt timeout. That timeout has to stay long, since one attempt can be a whole streamed
answer. *Connecting* and *generating* are different phases, and only one of them was bounded
separately. Fixed with `SocketsHttpHandler.ConnectTimeout = 10 s`
(`DependencyInjection.ProviderConnectTimeout`). A test inspects the real handler chain, so the
bound can't silently disappear.
**Lesson:** a timeout bounds a phase. List the phases (DNS, connect, TLS, first byte, whole
body) and check that each one has a bound that fits it.

### 6.6 Probe noise buried the evidence

With the default `Information` level, EF Core logged the readiness probe's `SELECT 1` once
every 10 s on each replica. Finding §6.5 meant filtering out hundreds of these lines.
`Microsoft.EntityFrameworkCore.Database.Command` is now `Warning` in `appsettings.json` (the
worker already had this) and `Information` in Development, where seeing SQL is part of
learning. While writing it, I put an explanatory `//` key *inside* `LogLevel`. Every key there
is read as a category, so a non-level value makes logging configuration throw at startup.
It was caught before the build and moved up a level.

### 6.7 Observed, not changed

- **During the SQL Server outage, a read returned `500`, not `503`.** That's honest about the
  failure but wrong about its nature. Mapping a database connectivity failure to 503 is a
  small change, left for later.
- **The readiness probe didn't answer within 15 s while SQL Server was down.** Its SQL check
  waits out the connect timeout, so the probe fails by timing out rather than by answering
  503. The outcome is the same and slower. A per-check timeout (`HealthCheckRegistration.Timeout`)
  would make it fast.
- **The Step 13 ledger timeout did not reproduce.** 8 simultaneous uploads from one tenant
  all completed in 23 s with no failed attempt. That isn't proof it can't happen; it's recorded
  as not reproduced.
- **Every `docker compose` command over the app file needs `EKA_AI_API_KEY`**, even `ps`,
  because the required-variable guard lives in a shared YAML anchor that is interpolated for
  every command. It's documented in the runbook. Moving the guard to the two services that use
  it would duplicate it.

---

## 7. Alternatives, and when each would win

| Chose | Over | The alternative wins when |
|---|---|---|
| Compose on one host | Azure Container Apps | There is a subscription and a need for managed TLS, scale-out, managed identity and Key Vault |
| Compose on one host | Kubernetes | A cluster already exists and is operated; the probes and images carry over unchanged |
| CSP-hardened SPA | Backend-for-frontend | Data is regulated, or token theft by XSS is unacceptable; costs a server component, cookies, CSRF and an SSE proxy |
| ClamAV container | Defender for Storage / vendor API | Files live in Azure Blob (Defender), or an outside scanning service is acceptable (a vendor API) |
| Migration job | Migrate on startup | Exactly one instance and no separate release step |
| Runtime `/config.json` | Build-time Angular environments | One environment only; simpler, but one build per environment |
| OpenTelemetry alone | Serilog + OpenTelemetry | The team needs Serilog's sinks or enrichers and accepts keeping log levels in two places |
| Aspire dashboard | Collector + Grafana/Tempo/Loki or a vendor | Telemetry must survive a restart, be searched over days, or alert (release checklist B8) |
| Redis buckets with fallback | Fail closed when Redis is down | Strict limits matter more than availability, for example a free tier that abuse would bankrupt |
| nginx retry on refused connect | A load balancer reading readiness | A platform with real service discovery: Kubernetes Services, Container Apps ingress |

---

## 8. What "done" honestly means here

The deployment is **reproducible and release-checked for a single machine**. It is not safe
to expose. The release checklist's Part B lists twelve blocking items, among them TLS, a real
identity provider, no password grant, a least-privilege SQL login, a Qdrant key, a secret
store, data-processor approval, durable telemetry, and backups with a tested restore. Each
is acceptable only because every port is bound to loopback. Writing them down is part of the
exit criterion. A deployment whose shortcuts are undocumented looks finished until the day
someone opens a port.

---

## 9. What to take away from Step 14

1. **Run the release before you declare it.** Six defects, every one invisible to 725
   passing tests, appeared within an hour of running the real deployment and its drills.
2. **A health signal is only as good as its question.** No healthcheck meant "started" passed
   for "healthy". Liveness must never ask about dependencies, and readiness must.
3. **Degrade on purpose.** For each dependency, decide in advance what users see when it's
   gone, then stop it and check.
4. **Bound every phase separately.** A long timeout that is right for generating an answer is
   wrong for opening a connection.
5. **One rule, one place.** Rejecting Serilog wasn't about taste. A duplicated security
   filter is one that eventually drifts.

## Running it

```powershell
# deploy/.env (from .env.example) or the environment: MSSQL_SA_PASSWORD, KEYCLOAK_ADMIN_PASSWORD,
# EKA_AI_API_KEY, REDIS_PASSWORD.
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml up -d --build --wait --wait-timeout 600
# http://127.0.0.1:8088   sign in as carol / local-dev-only
# http://127.0.0.1:18888  logs, traces, metrics (token: docker logs eka-otel)
docker run --rm -i --network eka_default -e EKA_PASSWORD=local-dev-only `
  -v ${PWD}/deploy/tests:/scripts grafana/k6:1.4.2 run /scripts/load.js
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml down
```

## Next

The 14-step learning path is complete. What comes after it is in `PROGRESS.md`. In priority
order: the deferred tier-2 evaluation, then the release checklist's Part B before anyone else
uses the system.
