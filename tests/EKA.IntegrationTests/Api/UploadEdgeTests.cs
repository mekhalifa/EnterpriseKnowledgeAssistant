using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EKA.Contracts.Common;
using EKA.Contracts.Documents;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EKA.IntegrationTests.Api;

/// <summary>
/// The edges of the upload endpoint: limits, spoofed types, and the race the unique index
/// settles. A small upload limit keeps the oversized cases cheap.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class UploadEdgeTests : IAsyncLifetime
{
    private const int MaxUploadBytes = 4096;

    private readonly SqlServerFixture _sqlServer;
    private EkaApiFactory _factory = null!;

    public UploadEdgeTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public Task InitializeAsync()
    {
        _factory = new EkaApiFactory(
            _sqlServer.ConnectionString,
            new Dictionary<string, string?> { ["Ingestion:MaxUploadBytes"] = MaxUploadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            _sqlServer.QdrantEndpoint);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static MultipartFormDataContent Form(byte[] bytes, string fileName, string claimedType = "text/plain")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(claimedType);
        form.Add(file, "file", fileName);
        return form;
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();

    [Fact]
    public async Task Concurrent_uploads_of_the_same_bytes_create_exactly_one_document()
    {
        // The race Step 4 left open. Every request passes the "seen this hash?" check before
        // any of them inserts. Only the unique index can pick one winner, and the losers must
        // answer with the winner's document, not a 500.
        var client = _factory.CreateClientFor(Guid.NewGuid());
        var bytes = DocumentEndpointTests.UniqueText();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => client.PostAsync("/api/documents", Form(bytes, "leave.txt"))));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.OK);
        responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(1);

        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<DocumentResponse>())!.Id));
        ids.Distinct().Count().ShouldBe(1);

        var listed = await client.GetFromJsonAsync<PagedResponse<DocumentSummaryResponse>>("/api/documents");
        listed!.TotalCount.ShouldBe(1);

        // And only one file survived: every loser deleted its own copy.
        Directory.EnumerateFiles(_factory.StorageRoot, "*", SearchOption.AllDirectories).Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_file_just_over_the_limit_is_a_400_naming_the_limit()
    {
        var client = _factory.CreateClientFor(Guid.NewGuid());
        var bytes = Encoding.UTF8.GetBytes(new string('a', MaxUploadBytes + 1));

        var response = await client.PostAsync("/api/documents", Form(bytes, "big.txt"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe("document.file_too_large");
    }

    [Fact]
    public async Task A_body_far_over_the_limit_is_still_refused_when_the_host_enforces_no_body_limit()
    {
        // The in-memory TestServer does not implement request-body size limits; that is
        // Kestrel's job (and Kestrel answers 413, verified against the real server in step-07
        // section 6). With the first line absent, the use case's own limit must still hold.
        var client = _factory.CreateClientFor(Guid.NewGuid());
        var bytes = new byte[MaxUploadBytes + (256 * 1024)];
        bytes.AsSpan().Fill((byte)'a');

        var response = await client.PostAsync("/api/documents", Form(bytes, "huge.txt"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe("document.file_too_large");
    }

    [Fact]
    public void The_upload_endpoint_declares_the_body_limit_kestrel_enforces()
    {
        // What a test server cannot enforce it can still show is configured. Kestrel reads
        // this metadata when it selects the endpoint, and cuts the body off with 413.
        var endpoint = _factory.Services
            .GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .Endpoints
            .Single(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>()?.EndpointName == "UploadDocument");

        var limit = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>();

        limit.ShouldNotBeNull();
        limit.MaxRequestBodySize.ShouldBe(MaxUploadBytes + (64 * 1024));
    }

    [Theory]
    [InlineData("invoice.pdf", "application/pdf")]
    [InlineData("payload.exe", "application/octet-stream")]
    public async Task A_file_whose_bytes_contradict_its_claim_is_refused(string fileName, string claimedType)
    {
        // Plain text dressed as a PDF, and an executable extension. The claimed Content-Type
        // is irrelevant either way: the server reads the bytes.
        var client = _factory.CreateClientFor(Guid.NewGuid());

        var response = await client.PostAsync("/api/documents", Form("just words"u8.ToArray(), fileName, claimedType));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe("document.unsupported_file_type");
        Directory.Exists(_factory.StorageRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task The_stored_content_type_is_the_detected_one_not_the_claimed_one()
    {
        var client = _factory.CreateClientFor(Guid.NewGuid());

        var response = await client.PostAsync(
            "/api/documents",
            Form(DocumentEndpointTests.UniqueText(), "notes.md", claimedType: "application/x-totally-legit"));

        (await response.Content.ReadFromJsonAsync<DocumentResponse>())!.ContentType.ShouldBe("text/markdown");
    }

    [Fact]
    public async Task An_upload_without_a_file_part_is_a_400()
    {
        var client = _factory.CreateClientFor(Guid.NewGuid());
        using var form = new MultipartFormDataContent { { new StringContent("Title only"), "title" } };

        var response = await client.PostAsync("/api/documents", form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
