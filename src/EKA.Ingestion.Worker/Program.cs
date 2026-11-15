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

builder.Services.AddIngestionWorker(builder.Configuration);

var host = builder.Build();
await host.RunAsync();
