using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace EKA.IntegrationTests.Api;

/// <summary>
/// What the Angular client (Step 13) depends on that no other test guards: the browser may call
/// the API from its own origin and only that one, and the OpenAPI document its types are
/// generated from matches the running API.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class BrowserClientTests : IAsyncLifetime
{
    private const string ClientOrigin = "http://localhost:4200";

    private readonly SqlServerFixture _sqlServer;
    private EkaApiFactory _factory = null!;

    public BrowserClientTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public Task InitializeAsync()
    {
        _factory = new EkaApiFactory(
            _sqlServer.ConnectionString,
            new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = ClientOrigin });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static HttpRequestMessage Preflight(string origin, string path = "/api/documents") => new(HttpMethod.Options, path)
    {
        Headers =
        {
            { "Origin", origin },
            { "Access-Control-Request-Method", "POST" },
            { "Access-Control-Request-Headers", "authorization,content-type" },
        },
    };

    // ── CORS ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_preflight_from_the_client_origin_is_answered_without_a_token()
    {
        // A preflight never carries Authorization. If it reached authentication it would be a 401,
        // and the browser would never send the real, authenticated request.
        var response = await _factory.CreateAnonymousClient().SendAsync(Preflight(ClientOrigin, "/api/conversations/00000000-0000-0000-0000-000000000001/messages/stream"));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([ClientOrigin]);
        string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant().ShouldContain("authorization");
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
    }

    [Fact]
    public async Task Another_origin_gets_no_permission_to_read_responses()
    {
        var response = await _factory.CreateAnonymousClient().SendAsync(Preflight("https://evil.example"));

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task An_actual_request_exposes_retry_after_and_location_to_the_page()
    {
        var client = _factory.CreateClientFor(Guid.NewGuid());
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/conversations") { Headers = { { "Origin", ClientOrigin } } };

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([ClientOrigin]);
        var exposed = string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers"));
        exposed.ShouldContain("Retry-After");
        exposed.ShouldContain("Location");
    }

    [Fact]
    public async Task A_cross_origin_request_without_a_token_is_still_refused()
    {
        // CORS grants a page permission to read; it never grants access. The token still decides.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/documents") { Headers = { { "Origin", ClientOrigin } } };

        var response = await _factory.CreateAnonymousClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ── The OpenAPI contract ────────────────────────────────────────────────────────

    /// <summary>
    /// The checked-in OpenAPI document must equal the running API's, so the client's generated
    /// types cannot silently drift from the server (ADR-0033). After a deliberate API change:
    /// <c>$env:EKA_OPENAPI_UPDATE="1"; dotnet test --filter OpenApi</c>, then
    /// <c>npm run generate:api</c> in src/EKA.Web, and commit both.
    /// </summary>
    [Fact]
    public async Task The_checked_in_OpenApi_document_matches_the_running_api()
    {
        var live = Normalize(await _factory.CreateAnonymousClient().GetStringAsync("/openapi/v1.json"));
        var path = Path.Combine(RepositoryRoot(), "src", "EKA.Web", "openapi", "eka-api.json");

        if (Environment.GetEnvironmentVariable("EKA_OPENAPI_UPDATE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, live);
            return;
        }

        File.Exists(path).ShouldBeTrue($"Missing {path}. Generate it with EKA_OPENAPI_UPDATE=1.");
        var checkedIn = Normalize(await File.ReadAllTextAsync(path));

        checkedIn.ShouldBe(
            live,
            "The API's OpenAPI document changed. If deliberate: EKA_OPENAPI_UPDATE=1, then `npm run generate:api` in src/EKA.Web.");
    }

    /// <summary>Indented, LF line endings, and without <c>servers</c> (it names the host that served
    /// the request, which differs between a test and a developer's machine).</summary>
    private static string Normalize(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("servers");
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}
