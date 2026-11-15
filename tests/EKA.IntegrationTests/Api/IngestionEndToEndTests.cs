using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using EKA.Contracts.Documents;
using EKA.Domain.Ingestion;
using EKA.Ingestion.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using PdfPageSize = UglyToad.PdfPig.Content.PageSize;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace EKA.IntegrationTests.Api;

/// <summary>
/// Step 7's exit criterion, literally: <i>uploading a file creates a tracked document and
/// extracts text asynchronously.</i> The real API and the real worker composition run
/// in-process side by side, sharing a SQL Server, a job store and a storage folder, exactly
/// as the two processes do in production. Nothing is faked. The worker learns of the upload
/// only through Hangfire.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class IngestionEndToEndTests : IAsyncLifetime
{
    private static readonly TimeSpan ExtractionDeadline = TimeSpan.FromSeconds(60);

    private readonly SqlServerFixture _sqlServer;
    private EkaApiFactory _api = null!;
    private IHost _worker = null!;

    public IngestionEndToEndTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        _api = new EkaApiFactory(_sqlServer.ConnectionString);

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
        });
        builder.Services.AddIngestionWorker(builder.Configuration);

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
    public async Task An_uploaded_text_file_is_extracted_by_the_worker()
    {
        var client = _api.CreateClientFor(Guid.NewGuid());

        var document = await UploadAsync(client, DocumentEndpointTests.UniqueText(), "leave.txt", "text/plain");
        document.Status.ShouldBe(nameof(IngestionStatus.Pending));

        var extracted = await WaitForStatusAsync(client, document.Id, IngestionStatus.Chunking);

        extracted.ExtractedAt.ShouldNotBeNull();
        var artifact = await ReadArtifactAsync(document);
        artifact.GetProperty("sections")[0].GetProperty("text").GetString().ShouldNotBeNull().ShouldContain("25 days");
    }

    [Fact]
    public async Task A_pdf_is_extracted_page_by_page()
    {
        var client = _api.CreateClientFor(Guid.NewGuid());

        var document = await UploadAsync(client, TwoPagePdf(), "handbook.pdf", "application/pdf");
        await WaitForStatusAsync(client, document.Id, IngestionStatus.Chunking);

        var sections = (await ReadArtifactAsync(document)).GetProperty("sections");
        sections.GetArrayLength().ShouldBe(2);
        sections[0].GetProperty("pageNumber").GetInt32().ShouldBe(1);
        sections[0].GetProperty("text").GetString().ShouldNotBeNull().ShouldContain("Annual leave");
        sections[1].GetProperty("pageNumber").GetInt32().ShouldBe(2);
        sections[1].GetProperty("text").GetString().ShouldNotBeNull().ShouldContain("Remote work");
    }

    [Fact]
    public async Task A_word_document_is_extracted_section_by_heading()
    {
        var client = _api.CreateClientFor(Guid.NewGuid());

        var document = await UploadAsync(client, WordWithHeadings(), "benefits.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        await WaitForStatusAsync(client, document.Id, IngestionStatus.Chunking);

        var paths = (await ReadArtifactAsync(document)).GetProperty("sections")
            .EnumerateArray()
            .Select(section => section.GetProperty("sectionPath").GetString())
            .ToList();

        paths.ShouldBe(["Benefits", "Benefits / Annual leave", "Benefits / Sick leave"]);
    }

    // ── Failure ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_corrupt_pdf_ends_as_failed_with_a_reason_instead_of_being_retried()
    {
        var client = _api.CreateClientFor(Guid.NewGuid());

        // Passes the signature check (%PDF-) and is garbage after it: the detector's limit,
        // and why the extractor is the second line of defence.
        var corrupt = Encoding.ASCII.GetBytes($"%PDF-1.7\nnot really a pdf {Guid.NewGuid()}");
        var document = await UploadAsync(client, corrupt, "broken.pdf", "application/pdf");

        var failed = await WaitForStatusAsync(client, document.Id, IngestionStatus.Failed);

        failed.FailureReason.ShouldNotBeNull().ShouldContain("PDF");
        failed.FailureCount.ShouldBe(1);
    }

    [Fact]
    public async Task Deleting_an_extracted_document_removes_its_files_from_storage()
    {
        var client = _api.CreateClientFor(Guid.NewGuid());
        var document = await UploadAsync(client, DocumentEndpointTests.UniqueText(), "leave.txt", "text/plain");
        await WaitForStatusAsync(client, document.Id, IngestionStatus.Chunking);
        Directory.EnumerateFiles(_api.StorageRoot, "*", SearchOption.AllDirectories).Count().ShouldBe(2);

        (await client.DeleteAsync($"/api/documents/{document.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Directory.EnumerateFiles(_api.StorageRoot, "*", SearchOption.AllDirectories).ShouldBeEmpty();
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
