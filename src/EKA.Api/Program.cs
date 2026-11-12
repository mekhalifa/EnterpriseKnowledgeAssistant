using EKA.Api.Endpoints;
using EKA.Api.Errors;
using EKA.Api.Security;
using EKA.Application;
using EKA.Application.Abstractions;
using EKA.Infrastructure;
using Microsoft.AspNetCore.Authentication;

// ─────────────────────────────────────────────────────────────────────────────────────
// Composition root.
//
// This is the only project that knows which implementation satisfies which interface.
// Everything else depends on abstractions declared in EKA.Application.
//
// The file composes and does nothing else: configuration, registrations, middleware, then
// one Map…Endpoints() call per feature. The failure mode of minimal APIs is a thousand-line
// Program.cs full of inline lambdas, and the only thing preventing it is the discipline of
// keeping endpoint bodies in EKA.Api/Endpoints/. See ADR-0003.
// ─────────────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// ── Error handling ──────────────────────────────────────────────────────────────────
//
// AddProblemDetails makes every unhandled failure — including the framework's own 404s
// and 415s — come back as RFC 9457 instead of an empty body. Without it a client gets a
// bare status code for some errors and a JSON document for others, and has to handle both.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        // The correlation id that ties this response to the log line and the trace. The
        // first question about any production error is "which request?", and without this
        // the answer has to be reconstructed from timestamps.
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        context.ProblemDetails.Instance ??=
            $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
    });

// Ordering matters: handlers run in registration order and the first one to return true
// wins. Each claims one exception family; anything unclaimed falls through to the default
// handler's opaque 500, which is the right answer for a failure nobody predicted.
//
// The first registration is not optional politeness. UseExceptionHandler intercepts
// BadHttpRequestException — which binding throws for malformed JSON and which already
// carries a 400 — and the fallback would rewrite it as a 500, blaming the server for the
// client's bad request across every endpoint at once.
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

// ── Identity ────────────────────────────────────────────────────────────────────────

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

builder.Services.AddOptions<DevelopmentAuthenticationOptions>()
    .BindConfiguration(DevelopmentAuthenticationOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

var developmentAuthentication = builder.Configuration
    .GetSection(DevelopmentAuthenticationOptions.SectionName)
    .Get<DevelopmentAuthenticationOptions>() ?? new DevelopmentAuthenticationOptions();

// Gate 3 of 3 (the other two are the default-false flag and the IsDevelopment check
// below): refuse to start rather than run a header-trusting authentication scheme outside
// development. A config file copied to staging now stops the deployment instead of
// silently accepting any tenant anyone cares to type.
if (developmentAuthentication.Enabled && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        $"'{DevelopmentAuthenticationOptions.SectionName}:Enabled' is true in the "
        + $"'{builder.Environment.EnvironmentName}' environment. That scheme trusts request "
        + "headers and must never run outside Development.");
}

var useDevelopmentAuthentication =
    developmentAuthentication.Enabled && builder.Environment.IsDevelopment();

// Exactly one scheme is registered, and which one is a deployment-time fact rather than a
// per-request decision. Step 12 adds a third branch here — JWT bearer — and no endpoint,
// handler or repository changes, because none of them reads anything but the principal.
if (useDevelopmentAuthentication)
{
    builder.Services
        .AddAuthentication(DevelopmentAuthenticationOptions.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
            DevelopmentAuthenticationOptions.SchemeName,
            configureOptions: null);
}
else
{
    builder.Services
        .AddAuthentication(NoIdentityProviderHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, NoIdentityProviderHandler>(
            NoIdentityProviderHandler.SchemeName,
            configureOptions: null);
}

builder.Services.AddAuthorization(options =>
    options.AddPolicy(
        TenantPolicy.Name,
        policy => policy
            .RequireAuthenticatedUser()
            // Not decoration: every repository method takes a tenant, so a token without
            // one has nothing to filter by. Rejecting here is the alternative to inventing
            // a tenant somewhere further down.
            .RequireClaim(TenantPolicy.TenantClaim)));

// ── Application and infrastructure ──────────────────────────────────────────────────

builder.Services.AddApplication();

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

// First in the pipeline, so it catches what everything after it throws.
app.UseExceptionHandler();

// Turns a bare 404 or 405 produced by the routing layer into a ProblemDetails body, so
// error responses have one shape whether they came from our code or the framework's.
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapDocumentEndpoints();

await app.RunAsync();

/// <summary>
/// Named so <c>WebApplicationFactory&lt;Program&gt;</c> can boot this exact application in
/// tests.
/// </summary>
/// <remarks>
/// Top-level statements compile into an internal <c>Program</c> class, which the test
/// project cannot name. Declaring the partial here makes it public. The alternative,
/// <c>InternalsVisibleTo</c>, opens every internal type in this assembly to the test
/// project — a larger hole than the one being filled.
/// <para>
/// This matters because it is what lets the endpoint tests run the real pipeline:
/// authentication, authorization, the validation filter, the exception handler and the
/// routing table as configured above. A test that assembles its own host instead proves
/// that the handlers work and nothing about whether the application is wired correctly,
/// which is where this class of bug actually lives.
/// </para>
/// </remarks>
public partial class Program;
