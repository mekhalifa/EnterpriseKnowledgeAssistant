# ADR-0034: Single-host Compose deployment — release, edge, scanner, supply chain

- **Status:** Accepted
- **Date:** 2026-11-17
- **Deciders:** project owner

## Context

Step 14's exit criterion is *a reproducible deployment and a completed release checklist*.
Three decisions were open in `PROGRESS.md` and settled first by the owner, each on the
recommended option:

| Decision | Chosen | Over |
|---|---|---|
| Deployment target | **Docker Compose on one host** | Azure Container Apps, Kubernetes |
| Browser tokens | **CSP-hardened SPA** (tokens stay in `sessionStorage`) | Backend-for-frontend |
| Malware scanner | **ClamAV container** behind `IMalwareScanner` | Defender for Storage, a vendor API |

Compose was chosen because it is the only target that can be built, run, broken and repaired
on this machine at no cost, which is what "reproducible" has to mean for a learning project.
The other choices follow from it: no cloud storage (so local-disk storage on a shared volume
stays), no managed identity (so secrets arrive as environment variables), and Keycloak stays
the identity provider of the deployment.

## Decision

**Shape.** `deploy/docker-compose.app.yml`, layered on the dependency file
(`docker-compose.yml`: SQL Server, Qdrant, Keycloak), adds: `migrate`, `api` (two replicas),
`worker`, `web`, `redis`, `clamav` and `otel` (ADR-0035). Two ports are published, both on
loopback: the web tier (8088) and the telemetry dashboard (18888). The deployment has its own
database, vector collection and file volume, so it never mixes with a developer's `dotnet run`.

**Images.** One `dotnet.Dockerfile` with targets `api`, `worker`, `migrator` (one restore, one
compile), and `web.Dockerfile`. Every base image pinned to an exact version (SDK to
`global.json`, runtimes to `MicrosoftRuntimeVersion`). Non-root users (`$APP_UID`, nginx 101).

**Release: migrations run once, before anything else.** The `migrator` image is an EF Core
migration bundle; `api` and `worker` depend on it with `service_completed_successfully`. No
application instance migrates at startup.

**Readiness gates traffic.** `/health/ready` checks SQL Server, Qdrant and a warm-up gate
(identity metadata, EF model, connections paid for before "ready"), with Redis as Degraded
only. Compose starts `web` only when an `api` instance is ready.

**Edge: nginx serves the client and proxies `/api/`.** Same origin, so the API's CORS list is
empty. It sets the Content-Security-Policy (`script-src 'self'`, `connect-src 'self'` + the
identity provider, `frame-ancestors 'none'`, `object-src 'none'`), `Referrer-Policy:
no-referrer` (the callback URL carries the authorization code), `nosniff`, `X-Frame-Options`,
`Permissions-Policy`, COOP. Response buffering is off for `/api/` so streamed answers stream.
Angular's critical-CSS inlining is off: it emits an inline `onload` handler that this CSP
blocks.

**Runtime configuration for the client.** `/config.json` is written by the web container at
start from `EKA_AUTH_AUTHORITY` (and optional client id, scope, API base). One build serves
every environment ("build, release, run").

**Identity metadata on the container network.** Tokens carry the issuer the browser uses
(`http://127.0.0.1:8080/realms/eka`), which is not reachable from inside a container. The API
gets `Authentication:Jwt:MetadataAddress` (`http://keycloak:8080/...`) and Keycloak
`KC_HOSTNAME_BACKCHANNEL_DYNAMIC`. Plain-HTTP metadata outside Development needs the explicit
`AllowHttpMetadataOnPrivateNetwork` **and** a single-label host (a Compose service name);
loopback, dotted names and IP addresses are refused. The rule is a pure, tested function.

**Malware scanning.** `ClamAvMalwareScanner` speaks clamd's `INSTREAM` protocol over TCP
(~40 lines, no client package), selected by `MalwareScanning:Scanner=ClamAv`. Only `FOUND` is
infected; anything else that is not `OK` throws, so the job retries.

**Exhausted retries end in Failed.** On its last attempt the ingestion job marks the document
Failed (exception type as reason) before rethrowing, so it can be reset.

**Supply chain and CI.** `packages.lock.json` for every project, restored in locked mode in
CI and in the image build. GitHub Actions: locked restore, `dotnet format --verify-no-changes`,
build, tests (no secrets, no billed calls), web `check:api`/test/build, image builds, compose
validation. Line endings are LF everywhere (`.gitattributes` `eol=lf`, `.editorconfig`), which
the format gate needs on a Linux runner.

## Consequences

### Positive

- `docker compose ... up -d --build` from a clean checkout gives the whole system, migrated,
  warm and observable, with the same images CI builds.
- A forgotten secret stops the deployment with its name (`:?` in Compose, `ValidateOnStart`).
- Uploads are scanned by a real engine; EICAR is rejected end to end (Step 14 drill).
- The client has no inline script anywhere, so the CSP costs nothing to keep.

### Negative / costs we accept

- **One host.** No failover; the host is the single point of failure, and "two API replicas"
  protects against a crashed process, not a dead machine.
- **No TLS at the edge.** Acceptable only because every published port is on loopback.
  Exposing the stack beyond this machine requires TLS termination, HSTS and
  `upgrade-insecure-requests` first ([release checklist](../operations/release-checklist.md), Part B).
- **Keycloak runs in development mode** (`start-dev`, H2, bootstrap admin): it is the local
  identity provider standing in for Entra ID or a managed Keycloak (ADR-0031).
- **Plain HTTP inside the network** (API → Keycloak metadata, worker → clamd, apps → Qdrant
  without an API key). Acceptable on one host's private bridge network only.
- **Readiness does not steer nginx.** Compose has no load balancer that reads health: a
  not-ready instance still receives requests from the web tier. Kubernetes or Container Apps
  would act on the same probes.
- **ClamAV needs ~1.5 GB of memory** and several minutes to download signatures on first start.
- The applications connect to SQL Server as `sa`. A least-privilege login is a checklist item
  before any shared deployment.

## Alternatives considered

- **Azure Container Apps.** Wins with an Azure subscription and a need for managed TLS,
  scale-out, managed identity and Key Vault; brings a Blob storage adapter and Entra ID.
- **Kubernetes.** Wins only where a cluster already exists and is operated; the probes,
  images and configuration here carry over unchanged.
- **Backend-for-frontend.** Removes tokens from the browser entirely; wins for regulated data
  or long-lived refresh tokens. Costs a server component, cookies, CSRF protection and an SSE
  proxy (ADR-0033).
- **Defender for Storage / a vendor API.** The first needs Azure Blob; the second sends every
  document to another third party.
- **Migrate on startup** (`Database.Migrate()` in the API). Simplest; wins with exactly one
  instance and no separate release step. Races with two.
- **Build-time Angular environments** (file replacements). Simpler; one build per environment.

## Revisit when

- The stack must be reachable from another machine (TLS, real identity provider, Qdrant key,
  least-privilege SQL login — all on the release checklist).
- A second host is needed (Container Apps or Kubernetes; storage moves to Blob or S3).
- The data handled becomes sensitive enough that token theft by XSS is unacceptable (BFF).
