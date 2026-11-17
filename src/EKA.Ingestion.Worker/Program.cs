using EKA.Infrastructure.Observability;
using EKA.Ingestion.Worker;

// ─────────────────────────────────────────────────────────────────────────────────────
// The ingestion worker (ADR-0004): a Hangfire server processing the "ingestion" queue,
// plus the recurring stalled-document sweep (ADR-0020).
//
// Needs the same connection string as the API. Locally, in this project's User Secrets:
//   dotnet user-secrets --project src/EKA.Ingestion.Worker set \
//     "ConnectionStrings:EkaDatabase" "Server=127.0.0.1,1433;Database=EnterpriseKnowledgeAssistant;User Id=sa;Password=<password>;TrustServerCertificate=True"
// In containers, the environment variable ConnectionStrings__EkaDatabase.
// File storage defaults to %LOCALAPPDATA%/EKA/files for both processes (see FileStorageOptions).
// ─────────────────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

// Logs, traces (one per ingestion job) and metrics over OTLP when configured (Step 14, ADR-0035).
builder.AddEkaTelemetry("eka-worker", configureTracing: null, EKA.AI.DependencyInjection.TelemetrySourceName);

builder.Services.AddIngestionWorker(builder.Configuration);

var host = builder.Build();
await host.RunAsync();
