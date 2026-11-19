# Configuration: development values and their production replacements

Every key and address the system uses in development: where it is set, and what replaces it
before production. The release gates themselves live in
[release-checklist.md](release-checklist.md).

Nothing secret is committed. Real secrets come from `deploy/.env` (gitignored) or User
Secrets; the committed values are local placeholders, and every one of them must be replaced.

## 1. Secrets

| Key | Dev value / where it is set | How the app reads it | Production |
|---|---|---|---|
| **SQL `sa` password** `MSSQL_SA_PASSWORD` | `ChangeMe_Local_Dev_1` in `deploy/.env.example`, copied to `deploy/.env` | Compose puts it into the connection string (`deploy/docker-compose.app.yml`, `x-dotnet-environment`) | No `sa` at all. A managed SQL service (Azure SQL) with a least-privilege login or Managed Identity; the whole `ConnectionStrings__EkaDatabase` from Key Vault or the platform's secret store. |
| **Connection string** `ConnectionStrings:EkaDatabase` | User Secrets of `src/EKA.Api` and `src/EKA.Ingestion.Worker` (each its own), or the env var in Compose | `ConnectionStrings__EkaDatabase` | Env var from Key Vault. Drop `TrustServerCertificate=True` and use a real TLS certificate. The `migrate` job needs it too, ideally with a separate login that has DDL rights. |
| **AI provider key** `Ai:ApiKey` | User Secrets of each project, or `EKA_AI_API_KEY` in `deploy/.env` | `Ai__ApiKey` | Key Vault → env var. A separate key per environment, with a spending cap, rotated. The API and worker refuse to start without it. |
| **Redis password** `REDIS_PASSWORD` | `ChangeMe_Local_Dev_3` in `.env.example` | `ConnectionStrings__Redis: redis:6379,password=…` | Managed Redis with TLS (`ssl=true`), password or Entra authentication from the secret store. |
| **Keycloak admin** `KEYCLOAK_ADMIN_PASSWORD` | `ChangeMe_Local_Dev_2` in `.env.example`; username `admin` in `deploy/docker-compose.yml` | Keycloak bootstrap only | Not needed with Entra ID. With a managed Keycloak: a strong password from the vault, the bootstrap admin disabled after first setup. |
| **Test-user passwords** | `local-dev-only` for alice, carol, dana, root, bruno in `deploy/keycloak/eka-realm.json`; also in `deploy/keycloak/get-token.ps1` and `src/EKA.Web/e2e/upload-ask-cite.e2e.ts` | Keycloak realm import | **Never import this realm in production.** Real users come from the directory. |
| **Telemetry dashboard** | `DASHBOARD__OTLP__AUTHMODE: Unsecured` in `docker-compose.app.yml`; the UI login token is in `docker logs eka-otel` | — | An OTLP collector and a durable backend (Azure Monitor, Grafana, …) with authenticated ingestion (ADR-0035). |
| **Qdrant** | No API key; ports bound to `127.0.0.1` only | — | A Qdrant API key, or managed Qdrant with TLS. The `VectorStore` section has **no API-key setting yet**, so this needs a code change. |

## 2. Addresses

| Setting | Dev value | Set in | Production |
|---|---|---|---|
| **JWT issuer** `Authentication:Jwt:Authority` | `http://127.0.0.1:8080/realms/eka` | `src/EKA.Api/appsettings.Development.json`; Compose `Authentication__Jwt__Authority` | `https://login.microsoftonline.com/<tenant>/v2.0` (Entra ID) or the HTTPS Keycloak realm. Must equal the tokens' `iss` exactly. |
| `RequireHttpsMetadata: false` | Development only | `appsettings.Development.json`, Compose | **Remove.** Defaults to true; the API refuses to start with false outside Development. |
| `MetadataAddress` + `AllowHttpMetadataOnPrivateNetwork: true` | `http://keycloak:8080/...` | `docker-compose.app.yml` (api) | Remove both; metadata comes from the HTTPS issuer. |
| Audience / scope / admin role | `eka-api`, `eka.access`, `eka.admin` | `src/EKA.Api/appsettings.json` | Keep, or set `Authentication__Jwt__Audience` etc. to the Entra app registration (app ID URI or client id). |
| **CORS** `Cors:AllowedOrigins` | `http://localhost:4200` | `appsettings.Development.json` | Empty when the web tier serves `/api` on the same origin (as Compose does); otherwise `Cors__AllowedOrigins__0=https://eka.yourco.com`. |
| **Qdrant** `VectorStore:Endpoint` | `http://127.0.0.1:6334` | Both `appsettings.json` (API, worker); Compose `http://qdrant:6334` | `VectorStore__Endpoint=https://<qdrant-host>:6334` |
| **AI endpoint and models** | `https://openrouter.ai/api/v1`, `openai/gpt-4o-mini`, `openai/text-embedding-3-small` | Both `appsettings.json` | Fine to keep, or switch with `Ai__Kind` / `Ai__Endpoint`. Changing `EmbeddingModel` or `EmbeddingDimensions` means re-indexing every document. |
| **OTLP** | `http://otel:18889` | `docker-compose.app.yml` | `OTEL_EXPORTER_OTLP_ENDPOINT` = the collector, plus `OTEL_EXPORTER_OTLP_HEADERS` for its authentication. |
| **ClamAV** | `MalwareScanning__Scanner=ClamAv`, host `clamav:3310` in Compose; `None` when unset | `docker-compose.app.yml` (worker) | Keep `ClamAv`, pointed at the production clamd. **Never ship with `None`.** |
| **File storage** `Storage:RootPath` | `%LOCALAPPDATA%/EKA/files`; the `/data/files` volume in Compose | default / volume | Durable storage shared by the API and the worker. |
| **API URL (local)** | `http://localhost:5118` (`https://localhost:7260`) | `src/EKA.Api/Properties/launchSettings.json` | Not used; the container listens on 8080 behind the web tier. |

## 3. Web client (Angular)

| Setting | Dev value | Set in | Production (container env, no rebuild) |
|---|---|---|---|
| `apiBaseUrl` | `http://localhost:5118` | `src/EKA.Web/public/config.json` | `EKA_API_BASE_URL=""` (same origin) |
| `auth.authority` | `http://127.0.0.1:8080/realms/eka` | same file | `EKA_AUTH_AUTHORITY=https://…` (required; also feeds the CSP `connect-src`) |
| `clientId` / `scope` | `eka-cli`, `openid eka.access` | same file | `EKA_AUTH_CLIENT_ID`, `EKA_AUTH_SCOPE` (see `deploy/web/40-runtime-config.sh`) |

## 4. Identity-provider client

Dev: `deploy/keycloak/eka-realm.json`, client `eka-cli`. Production client, in Entra ID or Keycloak:

- **Password grant off** (`directAccessGrantsEnabled: false`). Dev enables it only for
  `get-token.ps1` and curl.
- Authorization code + PKCE (S256), public client.
- Redirect URIs, web origins and post-logout URIs: `https://eka.yourco.com/*` only. Dev allows
  `http://localhost:4200/*` and `http://localhost:8088/*`.
- A `tid` (tenant) claim, a `groups` claim and the `eka.admin` role mapped from real directory
  data. Dev hard-codes tenants `11111111-…` and `22222222-…`.
- Keycloak: `sslRequired: all`.

## Before production

1. `ASPNETCORE_ENVIRONMENT=Production` and `DOTNET_ENVIRONMENT=Production`: no OpenAPI
   document, no HTTP-metadata exception.
2. Every secret in section 1 in the vault; none in `.env` or `appsettings`.
3. HTTPS for every address in sections 2 and 3; the three HTTP-metadata overrides removed.
4. A real identity-provider client (section 4); the realm file and its test users not deployed.
5. TLS and authentication on Redis, SQL Server and Qdrant (Qdrant needs a code change).
6. The gates in [release-checklist.md](release-checklist.md).
