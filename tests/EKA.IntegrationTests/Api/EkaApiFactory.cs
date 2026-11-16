using System.Net.Http.Headers;
using EKA.Application.Abstractions.AI;
using EKA.Application.Common;
using EKA.Domain.Common;
using EKA.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace EKA.IntegrationTests.Api;

/// <summary>
/// Boots the real <c>Program.cs</c> in-process against the containerized SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the real application and not a hand-assembled host.</b> The bugs this step can
/// produce are wiring bugs: a filter attached to the wrong endpoint, a policy that is
/// registered but never applied, an exception handler ordered after the thing it should
/// catch, a route that quietly allows anonymous access. None of them is visible to a test
/// that constructs its own pipeline — such a test proves the handlers work, which the unit
/// tests already proved. Booting <c>Program.cs</c> is what makes the assertions about
/// authentication and status codes mean anything.
/// </para>
/// <para>
/// <b>Why the configuration is overridden explicitly rather than inherited.</b> Relying on
/// <c>appsettings.Development.json</c> being found through the content root makes the test
/// suite depend on file discovery, and a silent miss would turn the development
/// authentication scheme off — at which point every test fails with 401 for a reason that
/// has nothing to do with the code under test.
/// </para>
/// </remarks>
internal sealed class EkaApiFactory(
    string connectionString,
    IReadOnlyDictionary<string, string?>? settings = null,
    Uri? vectorStore = null,
    IEmbeddingService? embeddings = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development is a hard requirement, not a convenience: Program.cs refuses to
        // register the header authentication scheme in any other environment.
        builder.UseEnvironment(Environments.Development);

        // UseSetting, not ConfigureAppConfiguration. Under the minimal hosting model,
        // Program.cs reads builder.Configuration while its top-level statements run —
        // AddInfrastructure demands the connection string right there — and that happens
        // BEFORE ConfigureAppConfiguration callbacks are applied. The symptom is the
        // application's own "connection string is not configured" guard firing in a test
        // that plainly configures one. UseSetting writes into host configuration, which is
        // already present when builder.Configuration is first read.
        builder.UseSetting(
            $"ConnectionStrings:{DependencyInjection.ConnectionStringName}",
            connectionString);

        // The test issuer (see TestTokens). Its metadata is supplied in-process below, so the
        // HTTPS-metadata rule stays on, as in production.
        builder.UseSetting("Authentication:Jwt:Authority", TestTokens.Issuer);
        builder.UseSetting("Authentication:Jwt:Audience", TestTokens.Audience);

        // Generous limits by default: functional tests burst on purpose (eight concurrent
        // uploads prove idempotency). The rate-limit tests pass tight limits in `settings`,
        // which are applied after these and win.
        foreach (var policy in new[] { "Ask", "Search", "Upload" })
        {
            builder.UseSetting($"RateLimiting:{policy}:TokenLimit", "1000");
            builder.UseSetting($"RateLimiting:{policy}:TokensPerMinute", "1000");
        }

        // AddAi validates on start and refuses to boot without a key. These tests never call
        // a model, so a placeholder satisfies the validator and this suite runs on a machine
        // with no key at all (CI). Host settings have the lowest precedence, so a developer's
        // real key in User Secrets still wins locally; it is loaded, and never used.
        builder.UseSetting("Ai:ApiKey", "integration-tests-make-no-ai-calls");

        // A throwaway folder per factory. The default (%LOCALAPPDATA%/EKA/files) is the
        // developer's real storage, and a test suite must never write there.
        builder.UseSetting("Storage:RootPath", StorageRoot);

        // The fixture's Qdrant. Without one, deletes still succeed (vector clean-up is best
        // effort) and only log a warning, which is what the health tests' broken factory sees.
        if (vectorStore is not null)
        {
            builder.UseSetting("VectorStore:Endpoint", vectorStore.ToString());
        }

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        // The one substitution in an otherwise real application. Everything above the port
        // (endpoints, handlers, prompt building, persistence) is real; the model is not,
        // because a test that depends on what a model says is a test that flakes. The real
        // adapter is proven separately by EKA.AI.Tests' smoke tests.
        builder.ConfigureTestServices(services =>
        {
            // The issuer's published metadata, supplied directly: its name and its signing key.
            // Everything else about token validation is the production configuration.
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var metadata = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                metadata.SigningKeys.Add(TestTokens.SigningKey);
                options.Configuration = metadata;
            });

            services.Replace(ServiceDescriptor.Singleton<IChatCompletionService>(Chat));

            // Always replaced, never left real. Since Step 10 every question is embedded, and the
            // real adapter would make billed calls with a developer's key from User Secrets, or
            // fail on the placeholder key in CI (step-10 section 5.1). Retrieval tests pass the
            // stand-in their worker uses, so both sides share one vector space.
            services.Replace(ServiceDescriptor.Singleton(Embeddings));

            // A test's own additions, last, e.g. a log capture (LogHygieneTests).
            configureServices?.Invoke(services);
        });
    }

    /// <summary>The embedding stand-in the API uses: the one passed in, or a fresh one.</summary>
    public IEmbeddingService Embeddings { get; } = embeddings ?? new DeterministicEmbeddingService();

    /// <summary>
    /// A client that presents a signed access token for this tenant, subject and groups.
    /// </summary>
    /// <remarks>
    /// Two clients, two tenants, one server: what the isolation tests need. Since Step 12 the
    /// identity travels as a real bearer token, validated exactly as in production.
    /// </remarks>
    public HttpClient CreateClientFor(Guid tenantId, string subjectId = "test-user", params string[] groups) =>
        CreateClientWithToken(TestTokens.For(tenantId, subjectId, groups));

    /// <summary>A client holding the tenant administrator role.</summary>
    public HttpClient CreateAdminClientFor(Guid tenantId, string subjectId = "admin") =>
        CreateClientWithToken(TestTokens.For(tenantId, subjectId, roles: ["eka.admin"]));

    /// <summary>A client presenting exactly this token: the security tests' forged and expired ones too.</summary>
    public HttpClient CreateClientWithToken(string token)
    {
        var client = CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }

    /// <summary>The model stand-in: records every prompt, answers or fails on demand.</summary>
    public RecordingChatService Chat { get; } = new();

    /// <summary>Where this factory's uploaded files land. Shared with an in-process worker in
    /// the ingestion tests, exactly as the real API and worker share one folder.</summary>
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "eka-tests", Guid.NewGuid().ToString("N"));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(StorageRoot))
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
    }

    /// <summary>A client that presents no identity at all.</summary>
    public HttpClient CreateAnonymousClient() => CreateClient();
}

/// <summary>
/// An <see cref="IChatCompletionService"/> that answers deterministically and remembers what
/// it was asked, so a test can assert on the exact prompt the application assembled.
/// </summary>
internal sealed class RecordingChatService : IChatCompletionService
{
    private readonly List<IReadOnlyList<ChatTurn>> _prompts = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<IReadOnlyList<ChatTurn>> Prompts
    {
        get
        {
            lock (_gate)
            {
                return [.. _prompts];
            }
        }
    }

    /// <summary>When set, every call fails with this error instead of answering.</summary>
    public Error? FailWith { get; set; }

    /// <summary>When set, the reply to every call, e.g. an answer with citation markers.</summary>
    public string? Reply { get; set; }

    public Task<Result<ChatCompletion>> CompleteAsync(
        IReadOnlyList<ChatTurn> conversation,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _prompts.Add(conversation);
        }

        // Echo the question, so a test can tell which answer belongs to which question. Since
        // Step 10 the last turn is "Sources: … Question: <q>"; the echo is of <q> alone.
        var asked = conversation[^1].Content;
        var marker = asked.LastIndexOf("Question: ", StringComparison.Ordinal);
        var question = marker < 0 ? asked : asked[(marker + "Question: ".Length)..];

        Result<ChatCompletion> result = FailWith is { } error
            ? error
            : new ChatCompletion(
                Reply ?? $"Answer to: {question}",
                "test.provider",
                "test-model",
                new TokenCount(40),
                new TokenCount(9));

        return Task.FromResult(result);
    }
}
