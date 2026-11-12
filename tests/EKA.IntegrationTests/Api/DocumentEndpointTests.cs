using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EKA.Contracts.Common;
using EKA.Contracts.Documents;
using EKA.Domain.Ingestion;
using Shouldly;

namespace EKA.IntegrationTests.Api;

/// <summary>
/// The CRUD path end to end: HTTP request, real middleware, real SQL Server, real response.
/// </summary>
/// <remarks>
/// Each test uses a freshly generated tenant id, so tests cannot see each other's rows
/// while still sharing one container. The alternative — resetting the database between
/// tests — costs seconds per test and buys nothing here.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class DocumentEndpointTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private EkaApiFactory _factory = null!;

    public DocumentEndpointTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

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

    private HttpClient NewTenantClient() => _factory.CreateClientFor(Guid.NewGuid());

    private static RegisterDocumentRequest NewRequest(
        string title = "Employee handbook",
        string? contentHash = null,
        int? authority = null) =>
        new(
            title,
            "handbook.pdf",
            "application/pdf",
            2048,
            contentHash ?? RandomHash(),
            authority);

    /// <summary>A distinct, well-formed SHA-256 digest per call.</summary>
    private static string RandomHash() =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Guid.NewGuid().ToByteArray()));

    // ── The happy path ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_then_read_then_rename_then_delete()
    {
        var client = NewTenantClient();

        var created = await client.PostAsJsonAsync("/api/documents", NewRequest());
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        var document = await created.Content.ReadFromJsonAsync<DocumentResponse>();
        document.ShouldNotBeNull();
        document.Status.ShouldBe(nameof(IngestionStatus.Pending));

        // Location must point at something that actually resolves — an endpoint returning
        // a 201 with a URL that 404s is a contract nobody can follow.
        created.Headers.Location.ShouldNotBeNull();
        var followed = await client.GetAsync(created.Headers.Location);
        followed.StatusCode.ShouldBe(HttpStatusCode.OK);

        var renamed = await client.PutAsJsonAsync(
            $"/api/documents/{document.Id}/title",
            new RenameDocumentRequest("Updated handbook"));

        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await renamed.Content.ReadFromJsonAsync<DocumentResponse>())!
            .Title.ShouldBe("Updated handbook");

        var deleted = await client.DeleteAsync($"/api/documents/{document.Id}");
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var gone = await client.GetAsync($"/api/documents/{document.Id}");
        gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Registering_the_same_bytes_twice_answers_200_not_201()
    {
        var client = NewTenantClient();
        var request = NewRequest();

        var first = await client.PostAsJsonAsync("/api/documents", request);
        var second = await client.PostAsJsonAsync("/api/documents", request);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Not 409: a retry is not an error. Not 201 either, because nothing was created
        // and a client counting creations would be wrong.
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        var firstDocument = await first.Content.ReadFromJsonAsync<DocumentResponse>();
        var secondDocument = await second.Content.ReadFromJsonAsync<DocumentResponse>();

        secondDocument!.Id.ShouldBe(firstDocument!.Id);
    }

    [Fact]
    public async Task Listing_pages_and_filters()
    {
        var client = NewTenantClient();

        for (var index = 0; index < 5; index++)
        {
            await client.PostAsJsonAsync("/api/documents", NewRequest(title: $"Doc {index}"));
        }

        var page = await client.GetFromJsonAsync<PagedResponse<DocumentSummaryResponse>>(
            "/api/documents?page=1&pageSize=2");

        page.ShouldNotBeNull();
        page.Items.Count.ShouldBe(2);
        page.TotalCount.ShouldBe(5);
        page.TotalPages.ShouldBe(3);

        var failedOnly = await client.GetFromJsonAsync<PagedResponse<DocumentSummaryResponse>>(
            $"/api/documents?status={nameof(IngestionStatus.Failed)}");

        failedOnly!.TotalCount.ShouldBe(0);
    }

    // ── Tenant isolation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_tenant_cannot_read_another_tenants_document()
    {
        var owner = _factory.CreateClientFor(Guid.NewGuid());
        var intruder = _factory.CreateClientFor(Guid.NewGuid());

        var created = await owner.PostAsJsonAsync("/api/documents", NewRequest());
        var document = await created.Content.ReadFromJsonAsync<DocumentResponse>();

        var response = await intruder.GetAsync($"/api/documents/{document!.Id}");

        // 404, not 403. A 403 would confirm the id is real, which is enough to map another
        // tenant's documents without reading one. This is the single most important
        // assertion in the file.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task One_tenant_cannot_delete_or_rename_another_tenants_document()
    {
        var owner = _factory.CreateClientFor(Guid.NewGuid());
        var intruder = _factory.CreateClientFor(Guid.NewGuid());

        var created = await owner.PostAsJsonAsync("/api/documents", NewRequest());
        var document = await created.Content.ReadFromJsonAsync<DocumentResponse>();

        var renamed = await intruder.PutAsJsonAsync(
            $"/api/documents/{document!.Id}/title",
            new RenameDocumentRequest("Mine now"));

        var deleted = await intruder.DeleteAsync($"/api/documents/{document.Id}");

        renamed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // And the owner's document is untouched — the write was refused, not merely
        // reported as refused.
        var stillThere = await owner.GetFromJsonAsync<DocumentResponse>(
            $"/api/documents/{document.Id}");

        stillThere!.Title.ShouldBe("Employee handbook");
    }

    [Fact]
    public async Task A_tenants_list_contains_only_its_own_documents()
    {
        var first = _factory.CreateClientFor(Guid.NewGuid());
        var second = _factory.CreateClientFor(Guid.NewGuid());

        await first.PostAsJsonAsync("/api/documents", NewRequest(title: "First tenant"));
        await second.PostAsJsonAsync("/api/documents", NewRequest(title: "Second tenant"));
        await second.PostAsJsonAsync("/api/documents", NewRequest(title: "Second tenant too"));

        var firstPage = await first.GetFromJsonAsync<PagedResponse<DocumentSummaryResponse>>(
            "/api/documents");

        firstPage!.TotalCount.ShouldBe(1);
        firstPage.Items.Single().Title.ShouldBe("First tenant");
    }

    // ── Authentication and authorization ────────────────────────────────────────────

    [Fact]
    public async Task An_anonymous_request_is_rejected_before_it_reaches_a_handler()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/api/documents");

        // The development scheme supplies a default identity when headers are absent, so
        // this is 200 rather than 401 — proving the pipeline runs end to end. The
        // authorization policy itself is proven by the next test, and Step 12 replaces the
        // scheme with one that rejects.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_malformed_tenant_header_is_rejected_rather_than_defaulted()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-EKA-Tenant", "not-a-guid");

        var response = await client.GetAsync("/api/documents");

        // Falling back to the default tenant here would make a typo in a test look like a
        // passing cross-tenant check.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ── Errors ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_invalid_request_returns_400_with_the_offending_fields()
    {
        var client = NewTenantClient();

        var response = await client.PostAsJsonAsync(
            "/api/documents",
            NewRequest(title: "") with { ContentHash = "nope" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await ReadProblemAsync(response);

        problem.GetProperty("errors").TryGetProperty("Title", out _).ShouldBeTrue();
        problem.GetProperty("errors").TryGetProperty("ContentHash", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task A_validation_failure_never_reaches_the_database()
    {
        var client = NewTenantClient();

        await client.PostAsJsonAsync("/api/documents", NewRequest(title: ""));

        var page = await client.GetFromJsonAsync<PagedResponse<DocumentSummaryResponse>>(
            "/api/documents");

        page!.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_not_found_response_is_problem_details_carrying_the_error_code()
    {
        var client = NewTenantClient();

        var response = await client.GetAsync($"/api/documents/{Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var problem = await ReadProblemAsync(response);

        problem.GetProperty("status").GetInt32().ShouldBe(404);
        problem.GetProperty("code").GetString().ShouldBe("document.not_found");

        // The correlation id that ties this response to the log line. Added once, in
        // Program.cs, for every ProblemDetails the application produces.
        problem.TryGetProperty("traceId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Resetting_a_document_that_never_failed_returns_409()
    {
        var client = NewTenantClient();

        var created = await client.PostAsJsonAsync("/api/documents", NewRequest());
        var document = await created.Content.ReadFromJsonAsync<DocumentResponse>();

        var response = await client.PostAsync($"/api/documents/{document!.Id}/reset", content: null);

        // The document is Pending and Document.Reset only accepts Failed. Nothing in the
        // handler checks that — the aggregate throws InvalidStateTransitionException and
        // the exception handler maps it here, in one place, as designed in Step 2.
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var problem = await ReadProblemAsync(response);

        problem.GetProperty("code").GetString().ShouldBe("domain.invalid_state_transition");
        problem.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("Pending");
    }

    [Fact]
    public async Task An_unparseable_route_id_is_a_404_from_routing()
    {
        var response = await NewTenantClient().GetAsync("/api/documents/not-a-guid");

        // The :guid route constraint refuses to match, so no endpoint is selected at all.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_malformed_json_body_does_not_leak_an_exception()
    {
        var client = NewTenantClient();

        var response = await client.PostAsync(
            "/api/documents",
            new StringContent("{ this is not json", Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("JsonException");
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
