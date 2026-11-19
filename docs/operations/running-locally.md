# Running EKA locally (development)

There are two ways to run it locally:

- **Option A:** run the code yourself. Best for day-to-day development, with hot reload and a debugger.
- **Option B:** run the whole stack in Docker. Closest to production.

For what each local setting becomes in production, see
[configuration-dev-to-production.md](configuration-dev-to-production.md).

## Option A: run the code yourself

### 1. One-time setup

```powershell
Copy-Item deploy/.env.example deploy/.env      # holds the local SQL, Keycloak and Redis passwords
dotnet tool restore                            # installs dotnet-ef

# The API and the worker each keep their own user secrets
$cs = "Server=127.0.0.1,1433;Database=Eka;User Id=sa;Password=ChangeMe_Local_Dev_1;TrustServerCertificate=True"
dotnet user-secrets --project src/EKA.Api              set "ConnectionStrings:EkaDatabase" $cs
dotnet user-secrets --project src/EKA.Ingestion.Worker set "ConnectionStrings:EkaDatabase" $cs
dotnet user-secrets --project src/EKA.Api              set "Ai:ApiKey" "<openrouter key>"
dotnet user-secrets --project src/EKA.Ingestion.Worker set "Ai:ApiKey" "<openrouter key>"
```

- Use `127.0.0.1`, not `localhost`, because the SQL container only listens on IPv4.
- If you changed `MSSQL_SA_PASSWORD` in `deploy/.env`, put that password in the connection string instead.

### 2. Start SQL Server, Qdrant and Keycloak in Docker

```powershell
docker compose -f deploy/docker-compose.yml up -d
```

The Qdrant dashboard is at <http://127.0.0.1:6333/dashboard>. Keycloak is at <http://127.0.0.1:8080>.

### 3. Apply the database migrations

Do this on the first run, and again after any new migration.

```powershell
$env:EKA_MIGRATIONS_CONNECTION = $cs
dotnet dotnet-ef database update --project src/EKA.Infrastructure --startup-project src/EKA.Api
```

### 4. Run the three processes, each in its own terminal

```powershell
dotnet run --project src/EKA.Api --launch-profile http    # http://localhost:5118
dotnet run --project src/EKA.Ingestion.Worker             # ingests and embeds uploaded files
cd src/EKA.Web; npm ci; npm start                         # http://localhost:4200
```

Open <http://localhost:4200> and sign in as **carol / local-dev-only**.

| User | Role |
| --- | --- |
| alice | regular user |
| carol | hr |
| dana | finance |
| root | eka.admin |
| bruno | tenant B |

The API and the worker share file storage: `%LOCALAPPDATA%/EKA/files`, unless `Storage:RootPath` is set.

### Call the API directly

```powershell
$token = ./deploy/keycloak/get-token.ps1 -User carol
curl http://localhost:5118/api/documents -H "Authorization: Bearer $token"
curl -X POST http://localhost:5118/api/search -H "Authorization: Bearer $token" -H "Content-Type: application/json" -d '{"query":"How many days of leave?"}'
```

The `http` launch profile listens on **5118**, not 5000.

## Option B: run the whole stack in Docker

```powershell
$env:EKA_AI_API_KEY = "<key>"; $env:REDIS_PASSWORD = "ChangeMe_Local_Dev_3"
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.app.yml up -d --build --wait --wait-timeout 600
```

- The app is at <http://127.0.0.1:8088>. Sign in as carol / local-dev-only.
- The telemetry dashboard is at <http://127.0.0.1:18888>. Get its token with `docker logs eka-otel`.
- `EKA_AI_API_KEY` and `REDIS_PASSWORD` must be set for **every** compose command that uses the app file, including `down`.

## Stopping

```powershell
docker compose -f deploy/docker-compose.yml down       # add -v to also delete the data volumes
```

## If something fails

| What you see | Likely cause |
| --- | --- |
| The worker exits immediately | It has no `Ai:ApiKey` in its own user secrets |
| The API returns 503 | SQL Server isn't reachable. Check `docker ps` and the connection string |
| Sign-in fails | Keycloak isn't running yet. It takes about 30–60 s to start |
| Uploads stay in "processing" | The worker isn't running, or it uses a different connection string or storage root |
