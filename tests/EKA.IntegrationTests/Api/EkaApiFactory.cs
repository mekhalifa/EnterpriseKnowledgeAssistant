using System.Net.Http.Headers;
using EKA.Application.Abstractions.AI;
using EKA.Application.Common;
using EKA.Domain.Common;
using EKA.Infrastructure;
using Microsoft.AspNetCore.Hosting;
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
    IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
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

        builder.UseSetting("Authentication:Development:Enabled", "true");

        // AddAi validates on start and refuses to boot without a key. These tests never call
        // a model, so a placeholder satisfies the validator and this suite runs on a machine
        // with no key at all (CI). Host settings have the lowest precedence, so a developer's
        // real key in User Secrets still wins locally; it is loaded, and never used.
        builder.UseSetting("Ai:ApiKey", "integration-tests-make-no-ai-calls");

        // A throwaway folder per factory. The default (%LOCALAPPDATA%/EKA/files) is the
        // developer's real storage, and a test suite must never write there.
        builder.UseSetting("Storage:RootPath", StorageRoot);

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        // The one substitution in an otherwise real application. Everything above the port
        // (endpoints, handlers, prompt building, persistence) is real; the model is not,
        // because a test that depends on what a model says is a test that flakes. The real
        // adapter is proven separately by EKA.AI.Tests' smoke tests.
        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IChatCompletionService>(Chat)));
    }

    /// <summary>
    /// A client that presents the given tenant on every request.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the development scheme reads headers rather than using a
    /// single fixed tenant: two clients, two tenants, one server — which is what the
    /// isolation tests need and a hard-coded tenant could not express.
    /// </remarks>
    public HttpClient CreateClientFor(Guid tenantId, string subjectId = "test-user")
    {
        var client = CreateClient();

        client.DefaultRequestHeaders.Add("X-EKA-Tenant", tenantId.ToString());
        client.DefaultRequestHeaders.Add("X-EKA-Subject", subjectId);
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

    public Task<Result<ChatCompletion>> CompleteAsync(
        IReadOnlyList<ChatTurn> conversation,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _prompts.Add(conversation);
        }

        // Echo the question, so a test can tell which answer belongs to which question.
        Result<ChatCompletion> result = FailWith is { } error
            ? error
            : new ChatCompletion(
                $"Answer to: {conversation[^1].Content}",
                "test.provider",
                "test-model",
                new TokenCount(40),
                new TokenCount(9));

        return Task.FromResult(result);
    }
}
