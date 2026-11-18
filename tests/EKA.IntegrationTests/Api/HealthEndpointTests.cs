using System.Net;
using System.Text.Json;
using EKA.Application.Common;
using EKA.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;

namespace EKA.IntegrationTests.Api;

/// <summary>
/// The two probes an orchestrator acts on.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class HealthEndpointTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private EkaApiFactory _factory = null!;

    public HealthEndpointTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public Task InitializeAsync()
    {
        _factory = new EkaApiFactory(_sqlServer.ConnectionString, vectorStore: _sqlServer.QdrantEndpoint);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Liveness_answers_without_touching_a_dependency()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // "Healthy" with no check entries. If the database check ever leaks onto this
        // probe, a database outage starts restarting every instance — a recoverable
        // incident turned into an outage.
        (await response.Content.ReadAsStringAsync()).ShouldBe("Healthy");
    }

    /// <summary>
    /// Polls readiness the way an orchestrator does. Since Step 14 an instance reports ready
    /// only after its warm-up, a moment after it starts listening, so a single request straight
    /// after start-up may correctly see 503.
    /// </summary>
    private static async Task<HttpResponseMessage> WhenReadyAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var response = await client.GetAsync("/health/ready");
            if (response.StatusCode == HttpStatusCode.OK || DateTime.UtcNow > deadline)
            {
                return response;
            }

            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task Readiness_reports_the_database_check_by_name()
    {
        var response = await WhenReadyAsync(_factory.CreateAnonymousClient());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        report.GetProperty("status").GetString().ShouldBe("Healthy");

        var checks = report.GetProperty("checks").EnumerateArray().ToList();

        // Named, not just counted: an empty check list would also report "Healthy", and
        // that is precisely the failure a mistyped tag produces.
        checks.ShouldContain(check => check.GetProperty("name").GetString() == "sql-server");

        // Step 14: the vector store is a hard dependency too (no search without it), and the
        // warm-up gate is visible, so "why is this instance not ready" has a named answer.
        checks.ShouldContain(check => check.GetProperty("name").GetString() == "qdrant");
        checks.ShouldContain(check => check.GetProperty("name").GetString() == "warm-up");
    }

    [Fact]
    public async Task Readiness_fails_when_the_vector_store_is_unreachable()
    {
        // Same database, but a Qdrant endpoint with nothing behind it.
        using var noVectors = new EkaApiFactory(_sqlServer.ConnectionString, vectorStore: new Uri("http://127.0.0.1:1"));

        var response = await WhenReadyAsync(noVectors.CreateAnonymousClient());

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).ShouldContain("qdrant");
    }

    [Fact]
    public async Task Readiness_answers_promptly_and_names_the_database_when_it_cannot_be_reached()
    {
        // An address nothing answers (a hung connect, like a host that dropped off the network)
        // with a 30 s connect timeout, so only the check's own bound can end the wait. Before
        // that bound this probe waited out the connect timeout: the Step 14 drill saw no
        // answer within 15 s, and Compose's probe gives up after 5 s (step-14 §6.7).
        using var broken = new EkaApiFactory(
            "Server=10.255.255.1,1433;Database=nothing;User Id=sa;Password=nope;"
            + "TrustServerCertificate=True;Connect Timeout=30");
        var client = broken.CreateAnonymousClient();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var response = await client.GetAsync("/health/ready");
        watch.Stop();

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        // The bound is 5 s; the rest is slack for a loaded machine, still well under 30 s.
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));

        var checks = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("checks").EnumerateArray().ToList();
        checks.ShouldContain(check =>
            check.GetProperty("name").GetString() == DependencyInjection.SqlServerCheckName
            && check.GetProperty("status").GetString() == "Unhealthy");
    }

    [Fact]
    public void Every_readiness_check_has_a_time_bound()
    {
        // The test above proves the bound where an unroutable address hangs. On a network that
        // rejects it at once the test would pass without one, so the configuration is asserted
        // too. The warm-up gate reads a flag and needs no bound.
        var registrations = _factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Where(registration => registration.Tags.Contains(HealthTags.Ready) && registration.Name != "warm-up")
            .ToList();

        registrations.Select(registration => registration.Name).ShouldBe(["sql-server", "qdrant"], ignoreOrder: true);
        registrations.ShouldAllBe(registration => registration.Timeout == DependencyInjection.ReadinessCheckTimeout);
    }

    [Fact]
    public async Task Both_probes_are_reachable_without_credentials()
    {
        var client = _factory.CreateAnonymousClient();

        // A probe that needs a token reports "unhealthy" the day the token expires.
        (await client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WhenReadyAsync(client)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
