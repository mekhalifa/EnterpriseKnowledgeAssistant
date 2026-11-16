using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using EKA.Application.Abstractions.AI;
using EKA.Application.Abstractions.Ingestion;
using EKA.Contracts.Documents;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using EKA.Ingestion.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;
using PdfPageSize = UglyToad.PdfPig.Content.PageSize;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace EKA.IntegrationTests.Api;

/// <summary>
/// Steps 7 and 8 end to end: <i>uploading a file creates a tracked document</i> (7), and <i>an
/// ingested document yields ordered chunks and matching vectors; re-ingesting the same
/// document is safe</i> (8). The real API and the real worker composition run in-process side
/// by side, sharing SQL Server, a job store, a storage folder and Qdrant, exactly as the two
/// processes do in production. The worker learns of the upload only through Hangfire.
/// </summary>
/// <remarks>
/// One substitution: the embedding model. Its vectors are deterministic stand-ins, so the
/// test is about the pipeline (every chunk gets a vector, in the right place, once), not about
/// what a model returns. The real adapter has its own smoke tests in EKA.AI.Tests.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class IngestionEndToEndTests : IAsyncLifetime
{
    private static readonly TimeSpan ExtractionDeadline = TimeSpan.FromSeconds(60);

    private readonly DeterministicEmbeddingService _embeddings = new();

    /// <summary>One tenant per test: xUnit builds a new instance, and so a new worker, per test.</summary>
    private readonly Guid _tenant = Guid.NewGuid();

    /// <summary>A collection per test, so dimension and content never leak between tests.</summary>
    private readonly string _collection = $"e2e_{Guid.NewGuid():N}";

    private readonly SqlServerFixture _sqlServer;
    private EkaApiFactory _api = null!;
    private IHost _worker = null!;

    public IngestionEndToEndTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        _api = new EkaApiFactory(
            _sqlServer.ConnectionString,
            new Dictionary<string, string?> { ["VectorStore:CollectionName"] = _collection },
            _sqlServer.QdrantEndpoint);

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
            // One chunk per request, so a document of three sections is three batches and a
            // failure can land between them.
            ["Indexing:EmbeddingBatchSize"] = "1",
            ["Ai:Kind"] = "OpenAICompatible",
            ["Ai:Endpoint"] = "https://ai.invalid/v1",
            ["Ai:ApiKey"] = "integration-tests-make-no-ai-calls",
            ["Ai:ChatModel"] = "unused",
            ["Ai:EmbeddingModel"] = _embeddings.Model,
            ["Ai:EmbeddingDimensions"] = DeterministicEmbeddingService.VectorSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        builder.Services.AddIngestionWorker(builder.Configuration);
        builder.Services.Replace(ServiceDescriptor.Singleton<IEmbeddingService>(_embeddings));

        // DisableDefaults above also switches off the build-time graph validation a real
        // Development host performs. Without restoring it, this test booted a MORE lenient
        // worker than production, and missed a worker that could not start (step-07 section 5.4).
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

    // ── The exit criterion, per format ─────────────────────────────────────────────

    [Fact]
    public async Task An_uploaded_text_file_ends_completed_with_one_vector_per_chunk()
    {
        var client = _api.CreateClientFor(_tenant);

        var document = await UploadAsync(client, DocumentEndpointTests.UniqueText(), "leave.txt", "text/plain");
        document.Status.ShouldBe(nameof(IngestionStatus.Pending));

        var completed = await WaitForStatusAsync(client, document.Id, IngestionStatus.Completed);

        completed.ExtractedAt.ShouldNotBeNull();
        completed.ChunkCount.ShouldBe(1);
        var chunk = (await ChunksAsync(document.Id)).ShouldHaveSingleItem();
        chunk.Content.ShouldContain("25 days");
        chunk.EmbeddingModel.ShouldBe(_embeddings.Model);
        (await VectorCountAsync(document)).ShouldBe(1);
    }

    [Fact]
    public async Task A_short_pdf_becomes_one_chunk_that_cites_both_its_pages()
    {
        var client = _api.CreateClientFor(_tenant);

        var document = await UploadAsync(client, TwoPagePdf(), "handbook.pdf", "application/pdf");
        await WaitForStatusAsync(client, document.Id, IngestionStatus.Completed);

        // The artifact still has a section per page; the chunker merged two small pages.
        (await ReadArtifactAsync(document)).GetProperty("sections").GetArrayLength().ShouldBe(2);

        var chunk = (await ChunksAsync(document.Id)).ShouldHaveSingleItem();
        chunk.PageRange.ShouldBe(new PageRange(1, 2));
        chunk.Content.ShouldContain("Annual leave");
        chunk.Content.ShouldContain("Remote work");
        (await VectorCountAsync(document)).ShouldBe(1);
    }

    [Fact]
    public async Task A_word_document_yields_ordered_chunks_one_per_heading()
    {
        var client = _api.CreateClientFor(_tenant);

        var document = await UploadAsync(client, WordWithHeadings(), "benefits.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        await WaitForStatusAsync(client, document.Id, IngestionStatus.Completed);

        var chunks = await ChunksAsync(document.Id);
        chunks.Select(c => c.Index.Value).ShouldBe([0, 1, 2]);
        chunks.Select(c => c.SectionPath).ShouldBe(["Benefits", "Benefits / Annual leave", "Benefits / Sick leave"]);
        (await VectorCountAsync(document)).ShouldBe(3);
    }

    // ── Safe to re-ingest ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_document_that_failed_mid_embedding_is_re_ingested_after_reset_without_duplicates()
    {
        var client = _api.CreateClientFor(_tenant);
        _embeddings.RejectOnceWhenInputContains("Grievances");

        var document = await UploadAsync(client, ThreeSectionMarkdown(), "procedures.md", "text/markdown");

        // Section one is embedded and stored, section two is refused: the document fails and
        // its partial vectors are removed with it.
        var failed = await WaitForStatusAsync(client, document.Id, IngestionStatus.Failed);
        failed.FailureReason.ShouldNotBeNull().ShouldContain("refused");
        (await VectorCountAsync(document)).ShouldBe(0);

        var reset = await client.PostAsync($"/api/documents/{document.Id}/reset", content: null);
        reset.StatusCode.ShouldBe(HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());

        var completed = await WaitForStatusAsync(client, document.Id, IngestionStatus.Completed);

        completed.ChunkCount.ShouldBe(3);
        (await ChunksAsync(document.Id)).Select(c => c.Index.Value).ShouldBe([0, 1, 2]);
        (await VectorCountAsync(document)).ShouldBe(3);
    }

    // ── Failure ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_corrupt_pdf_ends_as_failed_with_a_reason_instead_of_being_retried()
    {
        var client = _api.CreateClientFor(_tenant);

        // Passes the signature check (%PDF-) and is garbage after it: the detector's limit,
        // and why the extractor is the second line of defence.
        var corrupt = Encoding.ASCII.GetBytes($"%PDF-1.7\nnot really a pdf {Guid.NewGuid()}");
        var document = await UploadAsync(client, corrupt, "broken.pdf", "application/pdf");

        var failed = await WaitForStatusAsync(client, document.Id, IngestionStatus.Failed);

        failed.FailureReason.ShouldNotBeNull().ShouldContain("PDF");
        failed.FailureCount.ShouldBe(1);
        _embeddings.Calls.ShouldBe(0);
    }

    // ── Delete reaches all three stores ────────────────────────────────────────────

    [Fact]
    public async Task Deleting_an_ingested_document_removes_its_files_and_its_vectors()
    {
        var client = _api.CreateClientFor(_tenant);
        var document = await UploadAsync(client, DocumentEndpointTests.UniqueText(), "leave.txt", "text/plain");
        await WaitForStatusAsync(client, document.Id, IngestionStatus.Completed);
        Directory.EnumerateFiles(_api.StorageRoot, "*", SearchOption.AllDirectories).Count().ShouldBe(2);
        (await VectorCountAsync(document)).ShouldBe(1);

        (await client.DeleteAsync($"/api/documents/{document.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Directory.EnumerateFiles(_api.StorageRoot, "*", SearchOption.AllDirectories).ShouldBeEmpty();
        (await VectorCountAsync(document)).ShouldBe(0);
        (await ChunksAsync(document.Id)).ShouldBeEmpty();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    private static async Task<DocumentResponse> UploadAsync(HttpClient client, byte[] bytes, string fileName, string contentType)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);

        var response = await client.PostAsync("/api/documents", form);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<DocumentResponse>())!;
    }

    /// <summary>
    /// Polls the public API, as a client would. Polling the database instead would prove the
    /// worker wrote something, not that a user can see it.
    /// </summary>
    private static async Task<DocumentResponse> WaitForStatusAsync(HttpClient client, Guid documentId, IngestionStatus expected)
    {
        var deadline = DateTime.UtcNow + ExtractionDeadline;
        DocumentResponse? current = null;

        while (DateTime.UtcNow < deadline)
        {
            current = await client.GetFromJsonAsync<DocumentResponse>($"/api/documents/{documentId}");
            if (current!.Status == expected.ToString())
            {
                return current;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException(
            $"Document {documentId} did not reach {expected} within {ExtractionDeadline}; last status {current?.Status}, reason '{current?.FailureReason}'.");
    }

    /// <summary>Read straight from SQL Server: the API exposes a chunk count, not the chunks.</summary>
    private async Task<List<DocumentChunk>> ChunksAsync(Guid documentId)
    {
        await using var context = _sqlServer.CreateContext();
        return await context.DocumentChunks.AsNoTracking()
            .Where(chunk => chunk.DocumentId == documentId)
            .OrderBy(chunk => chunk.Index)
            .ToListAsync();
    }

    /// <summary>Through the worker's own vector store: the same adapter, collection and filter.</summary>
    private Task<long> VectorCountAsync(DocumentResponse document) =>
        _worker.Services.GetRequiredService<IVectorStore>()
            .CountDocumentAsync(new TenantId(_tenant), document.Id);

    private async Task<JsonElement> ReadArtifactAsync(DocumentResponse document)
    {
        var artifact = Directory.EnumerateFiles(_api.StorageRoot, "*.extracted.json", SearchOption.AllDirectories)
            .Single(path => path.Contains(document.Id.ToString("N"), StringComparison.OrdinalIgnoreCase));

        return JsonDocument.Parse(await File.ReadAllTextAsync(artifact)).RootElement;
    }

    /// <summary>A real two-page PDF, generated with PdfPig's writer. Unique per call, via the GUID.</summary>
    private static byte[] TwoPagePdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        builder.AddPage(PdfPageSize.A4).AddText($"Annual leave is 25 days. {Guid.NewGuid()}", 12, new PdfPoint(50, 750), font);
        builder.AddPage(PdfPageSize.A4).AddText("Remote work requires manager approval.", 12, new PdfPoint(50, 750), font);

        return builder.Build();
    }

    private static byte[] ThreeSectionMarkdown() => Encoding.UTF8.GetBytes(
        $"# Leave\n\nAnnual leave is 25 days. {Guid.NewGuid()}\n\n# Grievances\n\nRaise them with HR.\n\n# Exit\n\nGive one month of notice.\n");

    /// <summary>A real .docx with built-in heading styles, generated with the Open XML SDK.</summary>
    private static byte[] WordWithHeadings()
    {
        using var stream = new MemoryStream();
        using (var word = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            main.Document = new WordDocument(new Body(
                Paragraph("Benefits", "Heading1"),
                Paragraph($"What the company provides. {Guid.NewGuid()}"),
                Paragraph("Annual leave", "Heading2"),
                Paragraph("Employees receive 25 days."),
                Paragraph("Sick leave", "Heading2"),
                Paragraph("Up to 10 days with a certificate.")));
        }

        return stream.ToArray();
    }

    private static Paragraph Paragraph(string text, string? style = null)
    {
        var paragraph = new Paragraph();
        if (style is not null)
        {
            paragraph.Append(new ParagraphProperties(new ParagraphStyleId { Val = style }));
        }

        paragraph.Append(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        return paragraph;
    }
}
