using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using EKA.Application.Abstractions.AI;
using EKA.Contracts.Common;
using EKA.Contracts.Documents;
using EKA.Contracts.Retrieval;
using EKA.Domain.Ingestion;
using EKA.Ingestion.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace EKA.IntegrationTests.Api;

/// <summary>
/// Step 9's exit criterion end to end: <i>queries return relevant chunks and never content
/// outside the caller's access scope.</i> Real API, real worker, real SQL Server and Qdrant;
/// the embedding model is the only stand-in, shared by both processes so questions and chunks
/// land in one vector space.
/// </summary>
/// <remarks>
/// <para>
/// The stand-in's vectors all point into one quadrant, so every chunk is "similar" to every
/// question. That is deliberate here: these tests are about <i>what may come back</i>, and a
/// model that made the forbidden chunk look irrelevant would let a missing filter pass.
/// Relevance ranking is tested against real Qdrant with chosen vectors
/// (<c>QdrantVectorStoreTests</c>), and with real embeddings on the running stack (step-09).
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class RetrievalEndToEndTests : IAsyncLifetime
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private readonly DeterministicEmbeddingService _embeddings = new();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly string _collection = $"retrieval_{Guid.NewGuid():N}";
    private readonly SqlServerFixture _sqlServer;
    private EkaApiFactory _api = null!;
    private IHost _worker = null!;

    public RetrievalEndToEndTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        _api = new EkaApiFactory(
            _sqlServer.ConnectionString,
            new Dictionary<string, string?> { ["VectorStore:CollectionName"] = _collection },
            _sqlServer.QdrantEndpoint,
            _embeddings);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:EkaDatabase"] = _sqlServer.ConnectionString,
            ["Storage:RootPath"] = _api.StorageRoot,
            ["Ingestion:WorkerCount"] = "2",
            ["VectorStore:Endpoint"] = _sqlServer.QdrantEndpoint.ToString(),
            ["VectorStore:CollectionName"] = _collection,
            ["Ai:Kind"] = "OpenAICompatible",
            ["Ai:Endpoint"] = "https://ai.invalid/v1",
            ["Ai:ApiKey"] = "integration-tests-make-no-ai-calls",
            ["Ai:ChatModel"] = "unused",
            ["Ai:EmbeddingModel"] = _embeddings.Model,
            ["Ai:EmbeddingDimensions"] = DeterministicEmbeddingService.VectorSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        builder.Services.AddIngestionWorker(builder.Configuration);
        builder.Services.Replace(ServiceDescriptor.Singleton<IEmbeddingService>(_embeddings));
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));

        _worker = builder.Build();
        await _worker.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _worker.StopAsync();
        _worker.Dispose();
        _api.Dispose();
    }

    // ── Tenants ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_tenants_with_identical_documents_each_retrieve_only_their_own()
    {
        // Same bytes in both tenants: same chunk text, same vectors, the same distance to any
        // question. Nothing but the tenant filter can tell the two apart.
        var text = Policy();
        var alice = _api.CreateClientFor(_tenantA, "alice");
        var bob = _api.CreateClientFor(_tenantB, "bob");
        var inA = await UploadAndIndexAsync(alice, text);
        var inB = await UploadAndIndexAsync(bob, text);

        var hitsA = (await SearchAsync(alice, "How many days of annual leave?")).Hits;
        var hitsB = (await SearchAsync(bob, "How many days of annual leave?")).Hits;

        hitsA.ShouldNotBeEmpty();
        hitsA.ShouldAllBe(hit => hit.DocumentId == inA.Id);
        hitsB.ShouldNotBeEmpty();
        hitsB.ShouldAllBe(hit => hit.DocumentId == inB.Id);
    }

    // ── Document access ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_document_restricted_to_a_group_is_invisible_to_everyone_outside_it()
    {
        var owner = _api.CreateClientFor(_tenantA, "olivia");
        var outsider = _api.CreateClientFor(_tenantA, "bob");
        var member = _api.CreateClientFor(_tenantA, "carol", "hr");
        var restricted = await UploadAndIndexAsync(owner, Policy("Salary bands"));
        var open = await UploadAndIndexAsync(owner, Policy("Canteen hours"));

        var narrowed = await SetAccessAsync(owner, restricted.Id, "group:hr", "user:olivia");
        narrowed.StatusCode.ShouldBe(HttpStatusCode.OK, await narrowed.Content.ReadAsStringAsync());

        // Search, get, list: the outsider cannot tell the document exists.
        (await SearchAsync(outsider, "salary")).Hits.ShouldAllBe(hit => hit.DocumentId == open.Id);
        (await outsider.GetAsync($"/api/documents/{restricted.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var listed = await outsider.GetFromJsonAsync<PagedResponse<DocumentSummaryResponse>>("/api/documents");
        listed!.Items.Select(item => item.Id).ShouldBe([open.Id]);
        listed.TotalCount.ShouldBe(1);

        // Nor can they take it back: a document you cannot read, you cannot re-share.
        (await SetAccessAsync(outsider, restricted.Id, "tenant")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Members of the group, and the named user, still see it.
        (await SearchAsync(member, "salary")).Hits.Select(hit => hit.DocumentId).ShouldContain(restricted.Id);
        (await SearchAsync(owner, "salary")).Hits.Select(hit => hit.DocumentId).ShouldContain(restricted.Id);
        (await member.GetAsync($"/api/documents/{restricted.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Widening_access_again_makes_the_document_searchable_for_the_whole_tenant()
    {
        var owner = _api.CreateClientFor(_tenantA, "olivia");
        var outsider = _api.CreateClientFor(_tenantA, "bob");
        var document = await UploadAndIndexAsync(owner, Policy());
        (await SetAccessAsync(owner, document.Id, "user:olivia")).EnsureSuccessStatusCode();
        (await SearchAsync(outsider, "leave")).Hits.ShouldBeEmpty();

        var widened = await SetAccessAsync(owner, document.Id, "tenant");

        (await widened.Content.ReadFromJsonAsync<DocumentResponse>())!.Access.ShouldBe(["tenant"]);
        (await SearchAsync(outsider, "leave")).Hits.ShouldAllBe(hit => hit.DocumentId == document.Id);
        (await SearchAsync(outsider, "leave")).Hits.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Uploading_the_bytes_of_a_document_you_cannot_read_is_a_conflict_that_names_nothing()
    {
        var owner = _api.CreateClientFor(_tenantA, "olivia");
        var outsider = _api.CreateClientFor(_tenantA, "bob");
        var text = Policy("Board minutes");
        var document = await UploadAndIndexAsync(owner, text);
        (await SetAccessAsync(owner, document.Id, "user:olivia")).EnsureSuccessStatusCode();

        var response = await UploadAsync(outsider, text);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("document.duplicate_not_accessible");
        body.ShouldNotContain(document.Id.ToString());
        body.ShouldNotContain("Board minutes");
    }

    // ── SQL decides ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_document_mid_re_ingestion_is_not_returned_although_its_vectors_still_exist()
    {
        var client = _api.CreateClientFor(_tenantA);
        var document = await UploadAndIndexAsync(client, Policy());
        (await SearchAsync(client, "leave")).Hits.ShouldNotBeEmpty();

        // The window a real re-ingestion opens: status no longer Completed, old vectors still in
        // Qdrant. Forced directly in SQL, because the domain only reaches it through a failure
        // and a reset, which delete the vectors first.
        await using (var context = _sqlServer.CreateContext())
        {
            await context.Documents
                .Where(d => d.Id == document.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(d => d.Status, IngestionStatus.Embedding));
        }

        (await SearchAsync(client, "leave")).Hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Access_narrowed_in_SQL_wins_over_a_stale_vector_payload()
    {
        var client = _api.CreateClientFor(_tenantA, "bob");
        var document = await UploadAndIndexAsync(client, Policy());

        // The payload still says "tenant"; SQL says only Olivia. As if the payload update had
        // been lost. The SQL re-check must drop the hit.
        await using (var context = _sqlServer.CreateContext())
        {
            await context.Database.ExecuteSqlAsync(
                $"UPDATE [Documents] SET [Access] = N'[\"user:olivia\"]' WHERE [Id] = {document.Id}");
        }

        (await SearchAsync(client, "leave")).Hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_orphan_vector_whose_row_is_gone_resolves_to_nothing()
    {
        var client = _api.CreateClientFor(_tenantA);
        var document = await UploadAndIndexAsync(client, Policy());

        // What a crash between "row deleted" and "vectors deleted" leaves behind (ADR-0025).
        await using (var context = _sqlServer.CreateContext())
        {
            await context.Documents.Where(d => d.Id == document.Id).ExecuteDeleteAsync();
        }

        (await SearchAsync(client, "leave")).Hits.ShouldBeEmpty();
    }

    // ── The contract ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hits_carry_the_text_and_provenance_a_citation_needs()
    {
        var client = _api.CreateClientFor(_tenantA);
        var document = await UploadAndIndexAsync(client, Policy());

        var response = await SearchAsync(client, "annual leave", topK: 3);

        response.TopK.ShouldBe(3);
        var hit = response.Hits.ShouldHaveSingleItem();
        hit.DocumentId.ShouldBe(document.Id);
        hit.DocumentTitle.ShouldBe(document.Title);
        hit.ChunkIndex.ShouldBe(0);
        hit.Content.ShouldContain("25 days");
        hit.SectionPath.ShouldBe("Leave");
        hit.Score.ShouldBeInRange(0.6, 1.0);
    }

    [Theory]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":"   "}""")]
    [InlineData("""{"query":"leave","topK":0}""")]
    public async Task Malformed_searches_are_rejected_before_any_embedding_call(string body)
    {
        var client = _api.CreateClientFor(_tenantA);

        var response = await client.PostAsync("/api/search", new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _embeddings.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Search_requires_an_authenticated_caller()
    {
        // The development scheme fills in a default identity when headers are absent, so the
        // unauthenticated case is a malformed one: authentication fails, and the route group's
        // policy must turn that into 401 before the handler runs.
        var client = _api.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-EKA-Tenant", "not-a-guid");

        var response = await client.PostAsJsonAsync("/api/search", new SearchRequest("leave"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _embeddings.Calls.ShouldBe(0);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    /// <summary>Markdown with one heading; unique per call so content hashes never collide.</summary>
    private static string Policy(string topic = "Leave") =>
        $"# {topic}\n\nEmployees receive 25 days of annual leave. Reference {Guid.NewGuid()}.\n";

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string markdown)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(markdown));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(file, "file", "policy.md");

        return await client.PostAsync("/api/documents", form);
    }

    private static async Task<DocumentResponse> UploadAndIndexAsync(HttpClient client, string markdown)
    {
        var response = await UploadAsync(client, markdown);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var document = (await response.Content.ReadFromJsonAsync<DocumentResponse>())!;

        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            var current = await client.GetFromJsonAsync<DocumentResponse>($"/api/documents/{document.Id}");
            if (current!.Status == nameof(IngestionStatus.Completed))
            {
                return current;
            }

            current.Status.ShouldNotBe(nameof(IngestionStatus.Failed), current.FailureReason);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"Document {document.Id} was not indexed within {Deadline}.");
    }

    private static async Task<SearchResponse> SearchAsync(HttpClient client, string query, int? topK = null)
    {
        var response = await client.PostAsJsonAsync("/api/search", new SearchRequest(query, topK));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<SearchResponse>())!;
    }

    private static Task<HttpResponseMessage> SetAccessAsync(HttpClient client, Guid documentId, params string[] principals) =>
        client.PutAsJsonAsync($"/api/documents/{documentId}/access", new SetDocumentAccessRequest(principals));
}
