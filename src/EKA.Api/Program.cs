using EKA.Api.Security;
using EKA.Application.Abstractions;
using EKA.Infrastructure;

// ─────────────────────────────────────────────────────────────────────────────────────
// Composition root.
//
// This is the only project that knows which implementation satisfies which interface.
// Everything else depends on abstractions declared in EKA.Application.
//
// Step 3 wires persistence only. Endpoints, validation, ProblemDetails error handling and
// health checks arrive in Step 4 — so this API currently serves no routes, which is
// expected and recorded in docs/PROGRESS.md.
// ─────────────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Required by HttpContextCurrentUser. Registered explicitly rather than relied upon as a
// side effect of some other call.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

// Throws at startup if the connection string is missing, which is deliberate: a
// misconfiguration found by a user is a misconfiguration found too late.
//
// Set it locally with User Secrets so it never reaches a committed file:
//   dotnet user-secrets --project src/EKA.Api set \
//     "ConnectionStrings:EkaDatabase" \
//     "Server=localhost,1433;Database=EnterpriseKnowledgeAssistant;User Id=sa;Password=<your password>;TrustServerCertificate=True"
//
// In containers it comes from the environment variable
// ConnectionStrings__EkaDatabase, and in production from Key Vault.
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.Run();
