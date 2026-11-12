using System.Net;
using System.Text.Json;
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
        _factory = new EkaApiFactory(_sqlServer.ConnectionString);
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

    [Fact]
    public async Task Readiness_reports_the_database_check_by_name()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        report.GetProperty("status").GetString().ShouldBe("Healthy");

        var checks = report.GetProperty("checks").EnumerateArray().ToList();

        // Named, not just counted: an empty check list would also report "Healthy", and
        // that is precisely the failure a mistyped tag produces.
        checks.ShouldContain(check => check.GetProperty("name").GetString() == "sql-server");
    }

    [Fact]
    public async Task Readiness_fails_when_the_database_is_unreachable()
    {
        // A connection string pointing at a port with nothing behind it, so the check has
        // something real to fail against rather than a mocked failure.
        using var broken = new EkaApiFactory(
            "Server=127.0.0.1,1;Database=nothing;User Id=sa;Password=nope;"
            + "TrustServerCertificate=True;Connect Timeout=2");

        var response = await broken.CreateAnonymousClient().GetAsync("/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Both_probes_are_reachable_without_credentials()
    {
        var client = _factory.CreateAnonymousClient();

        // A probe that needs a token reports "unhealthy" the day the token expires.
        (await client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
