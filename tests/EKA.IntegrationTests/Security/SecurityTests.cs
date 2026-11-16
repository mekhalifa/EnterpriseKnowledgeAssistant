using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EKA.Contracts.Common;
using EKA.Contracts.Conversations;
using EKA.Contracts.Documents;
using EKA.Contracts.Retrieval;
using EKA.Contracts.Usage;
using EKA.Domain.Access;
using EKA.IntegrationTests.Api;
using Shouldly;

namespace EKA.IntegrationTests.Security;

/// <summary>
/// Step 12's exit criterion: <i>security tests prove unauthorized content cannot be retrieved.</i>
/// </summary>
/// <remarks>
/// <para>
/// Real HTTP, real middleware, real SQL Server and Qdrant, and real token validation. The tokens
/// are signed by a test issuer, and the API validates them with its production configuration
/// (see <see cref="TestTokens"/>). Every route to document content is tried for every kind of
/// caller who must not reach it: no token, a bad token, another tenant, another group, an
/// administrator without read access.
/// </para>
/// <para>
/// Each restricted document carries a canary string. A test passes only if the canary is absent
/// from everything that caller receives, including the prompt sent to the model on their behalf.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class SecurityTests : IAsyncLifetime
{
    private const string Canary = "CANARY-7F3A-SALARY-BAND-C";

    private readonly SqlServerFixture _sqlServer;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private EkaApiFactory _api = null!;
    private Domain.Ingestion.Document _hrOnly = null!;
    private Domain.Ingestion.Document _otherTenant = null!;
    private Domain.Ingestion.Document _open = null!;

    public SecurityTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        _api = new EkaApiFactory(
            _sqlServer.ConnectionString,
            new Dictionary<string, string?> { ["VectorStore:CollectionName"] = $"security_{Guid.NewGuid():N}" },
            _sqlServer.QdrantEndpoint);

        // Tenant A: one HR-only document carrying the canary, one open document. Tenant B: the
        // same sentence, tenant-wide, WITHOUT the canary. Its own users may read it; tenant A's
        // users must never see it (checked by its id). The canary marks exactly one document,
        // so finding it anywhere is a leak of that document. (A first version gave tenant B the
        // canary too, and tenant B's users then correctly "leaked" their own document.)
        _hrOnly = await SearchableDocuments.SeedAsync(_sqlServer, _api, _tenantA, $"Salary band C is EUR 61,750. {Canary}", "Salary bands",
            DocumentAccess.Of([AccessPrincipal.Group("hr")]), owner: "olivia");
        _open = await SearchableDocuments.SeedAsync(_sqlServer, _api, _tenantA, "Employees receive 25 days of annual leave.", "Leave policy", owner: "olivia");
        _otherTenant = await SearchableDocuments.SeedAsync(_sqlServer, _api, _tenantB, "Salary band C is EUR 61,750.", "Salary bands", owner: "bruno");
    }

    public Task DisposeAsync()
    {
        _api.Dispose();
        return Task.CompletedTask;
    }

    // ── Authentication: only a valid token for this API gets in ─────────────────────

    public static TheoryData<string, string> ProtectedRoutes => new()
    {
        { "GET", "/api/documents" },
        { "POST", "/api/search" },
        { "GET", "/api/conversations" },
        { "POST", "/api/conversations" },
        { "GET", "/api/usage" },
    };

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Without_a_token_every_protected_route_is_401(string method, string path)
    {
        var response = await Send(_api.CreateAnonymousClient(), method, path);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    public static TheoryData<string> BadTokens => ["expired", "wrong-audience", "wrong-issuer", "foreign-key", "unsigned", "garbage"];

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task A_token_that_fails_validation_is_401_and_reaches_no_handler(string kind)
    {
        var token = kind switch
        {
            "expired" => TestTokens.For(_tenantA, lifetime: TimeSpan.FromMinutes(-10)),
            "wrong-audience" => TestTokens.For(_tenantA, audience: "some-other-api"),
            "wrong-issuer" => TestTokens.For(_tenantA, issuer: "https://evil.example/realms/eka"),
            "foreign-key" => TestTokens.For(_tenantA, key: TestTokens.ForeignKey),
            "unsigned" => TestTokens.Unsigned(_tenantA),
            _ => "not.a.jwt",
        };

        var embeddings = _api.Embeddings.ShouldBeOfType<DeterministicEmbeddingService>();
        var callsBefore = embeddings.Calls; // seeding embedded the documents

        var response = await _api.CreateClientWithToken(token).PostAsJsonAsync("/api/search", new SearchRequest("salary band"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        embeddings.Calls.ShouldBe(callsBefore, "no handler may run for an invalid token");
    }

    [Fact]
    public async Task A_valid_token_without_this_APIs_scope_is_403()
    {
        var response = await _api.CreateClientWithToken(TestTokens.For(_tenantA, scope: "some.other.scope")).GetAsync("/api/documents");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_valid_token_without_a_tenant_is_403()
    {
        var response = await _api.CreateClientWithToken(TestTokens.For(_tenantA, includeTenant: false)).GetAsync("/api/documents");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_retired_development_headers_change_nothing()
    {
        // A valid token for tenant A, plus the headers that chose the tenant before Step 12.
        var client = _api.CreateClientFor(_tenantA, "alice");
        client.DefaultRequestHeaders.Add("X-EKA-Tenant", _tenantB.ToString());
        client.DefaultRequestHeaders.Add("X-EKA-Groups", "hr");

        (await client.GetAsync($"/api/documents/{_otherTenant.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/documents/{_hrOnly.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── Content: every route, every unauthorized caller ─────────────────────────────

    public static TheoryData<string> UnauthorizedCallers => ["other-tenant", "same-tenant-no-group", "other-tenant-same-group", "admin-not-in-group"];

    private HttpClient Unauthorized(string caller) => caller switch
    {
        "other-tenant" => _api.CreateClientFor(_tenantB, "mallory"),
        "same-tenant-no-group" => _api.CreateClientFor(_tenantA, "bob"),
        // "hr" in tenant B is not "hr" in tenant A: group ids are only unique per directory.
        "other-tenant-same-group" => _api.CreateClientFor(_tenantB, "mallory", "hr"),
        // Administrators manage documents; they do not read restricted ones through the assistant.
        _ => _api.CreateAdminClientFor(_tenantA, "root"),
    };

    [Theory]
    [MemberData(nameof(UnauthorizedCallers))]
    public async Task Search_never_returns_restricted_content(string caller)
    {
        var response = await Unauthorized(caller).PostAsJsonAsync("/api/search", new SearchRequest("What is salary band C?", 20));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain(Canary);
        body.ShouldNotContain(_hrOnly.Id.ToString());
        if (caller != "other-tenant" && caller != "other-tenant-same-group")
        {
            body.ShouldNotContain(_otherTenant.Id.ToString());
        }
    }

    [Theory]
    [MemberData(nameof(UnauthorizedCallers))]
    public async Task Answers_never_contain_restricted_content_and_neither_does_the_prompt_behind_them(string caller)
    {
        var client = Unauthorized(caller);
        var conversation = (await (await client.PostAsync("/api/conversations", content: null)).Content.ReadFromJsonAsync<ConversationResponse>())!;
        var promptsBefore = _api.Chat.Prompts.Count;

        var response = await client.PostAsJsonAsync($"/api/conversations/{conversation.Id}/messages", new AskQuestionRequest("What is salary band C?"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(Canary);
        foreach (var prompt in _api.Chat.Prompts.Skip(promptsBefore))
        {
            prompt.ShouldAllBe(turn => !turn.Content.Contains(Canary));
        }
    }

    [Theory]
    [InlineData("other-tenant")]
    [InlineData("same-tenant-no-group")]
    [InlineData("other-tenant-same-group")]
    public async Task The_document_itself_is_not_found_by_id_or_in_the_list(string caller)
    {
        var client = Unauthorized(caller);

        (await client.GetAsync($"/api/documents/{_hrOnly.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var listed = await client.GetFromJsonAsync<PagedResponse<DocumentSummaryResponse>>("/api/documents?pageSize=100");
        listed!.Items.ShouldNotContain(item => item.Id == _hrOnly.Id);
    }

    [Fact]
    public async Task A_member_of_the_group_does_get_the_content_which_proves_the_tests_above_can_fail()
    {
        var member = _api.CreateClientFor(_tenantA, "carol", "hr");

        var body = await (await member.PostAsJsonAsync("/api/search", new SearchRequest("What is salary band C?", 20))).Content.ReadAsStringAsync();

        body.ShouldContain(Canary);
    }

    // ── Management: owner or administrator, never a mere reader ─────────────────────

    [Fact]
    public async Task A_reader_who_is_not_the_owner_cannot_delete_rename_or_reshare()
    {
        var reader = _api.CreateClientFor(_tenantA, "bob");

        (await reader.DeleteAsync($"/api/documents/{_open.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reader.PutAsJsonAsync($"/api/documents/{_open.Id}/title", new RenameDocumentRequest("Mine now"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reader.PutAsJsonAsync($"/api/documents/{_open.Id}/access", new SetDocumentAccessRequest(["user:bob"]))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reader.GetAsync($"/api/documents/{_open.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_owner_and_a_tenant_administrator_can_manage_and_a_foreign_administrator_cannot()
    {
        var owner = _api.CreateClientFor(_tenantA, "olivia");
        var admin = _api.CreateAdminClientFor(_tenantA, "root");
        var foreignAdmin = _api.CreateAdminClientFor(_tenantB, "root-b");

        (await owner.PutAsJsonAsync($"/api/documents/{_open.Id}/title", new RenameDocumentRequest("Leave policy 2026"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync($"/api/documents/{_hrOnly.Id}/title", new RenameDocumentRequest("Salary bands (HR)"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await foreignAdmin.PutAsJsonAsync($"/api/documents/{_hrOnly.Id}/title", new RenameDocumentRequest("Pwned"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── Usage reporting ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Usage_is_for_administrators_and_shows_the_month_spent_by_their_tenant()
    {
        var member = _api.CreateClientFor(_tenantA, "carol", "hr");
        (await member.PostAsJsonAsync("/api/search", new SearchRequest("salary band"))).EnsureSuccessStatusCode();

        (await member.GetAsync("/api/usage")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var usage = await _api.CreateAdminClientFor(_tenantA).GetFromJsonAsync<UsageResponse>("/api/usage");
        usage!.EmbeddingTokens.ShouldBeGreaterThan(0);
        usage.BudgetTokens.ShouldBeGreaterThan(0);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string path) => method switch
    {
        "POST" when path == "/api/search" => client.PostAsJsonAsync(path, new SearchRequest("anything")),
        "POST" => client.PostAsync(path, content: null),
        _ => client.GetAsync(path),
    };
}

/// <summary>Per-user rate limits and per-tenant budgets over the real pipeline (Step 12).</summary>
[Collection(SqlServerCollection.Name)]
public sealed class LimitTests(SqlServerFixture sqlServer)
{
    private EkaApiFactory Api(Dictionary<string, string?> settings)
    {
        settings["VectorStore:CollectionName"] = $"limits_{Guid.NewGuid():N}";
        return new EkaApiFactory(sqlServer.ConnectionString, settings, sqlServer.QdrantEndpoint);
    }

    [Fact]
    public async Task Searching_faster_than_the_limit_is_429_with_retry_after_and_only_for_that_user()
    {
        using var api = Api(new()
        {
            ["RateLimiting:Search:TokenLimit"] = "2",
            ["RateLimiting:Search:TokensPerMinute"] = "1",
        });
        var tenant = Guid.NewGuid();
        var eager = api.CreateClientFor(tenant, "eager");

        (await eager.PostAsJsonAsync("/api/search", new SearchRequest("a"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await eager.PostAsJsonAsync("/api/search", new SearchRequest("b"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var limited = await eager.PostAsJsonAsync("/api/search", new SearchRequest("c"));

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        JsonDocument.Parse(await limited.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString().ShouldBe("rate_limited");

        // A colleague in the same tenant has their own bucket.
        (await api.CreateClientFor(tenant, "colleague").PostAsJsonAsync("/api/search", new SearchRequest("d"))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_tenant_with_no_budget_left_is_refused_before_any_provider_call()
    {
        var suspended = Guid.NewGuid();
        using var api = Api(new() { [$"Budgets:Tenants:{suspended}"] = "0" });
        var client = api.CreateClientFor(suspended, "alice");

        var response = await client.PostAsJsonAsync("/api/search", new SearchRequest("leave"));

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString().ShouldBe("usage.budget_exhausted");
        api.Embeddings.ShouldBeOfType<DeterministicEmbeddingService>().Calls.ShouldBe(0);

        // Another tenant on the same server is unaffected.
        (await api.CreateClientFor(Guid.NewGuid(), "bob").PostAsJsonAsync("/api/search", new SearchRequest("leave"))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
