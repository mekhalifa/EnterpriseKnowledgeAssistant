using System.Net.Http.Headers;
using EKA.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
internal sealed class EkaApiFactory(string connectionString) : WebApplicationFactory<Program>
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

    /// <summary>A client that presents no identity at all.</summary>
    public HttpClient CreateAnonymousClient() => CreateClient();
}
