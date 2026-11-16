using System.Text;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Documents.DeleteDocument;
using EKA.Application.Documents.ExtractDocumentText;
using EKA.Application.Documents.Ingestion;
using EKA.Application.Documents.RecoverStalledDocuments;
using EKA.Application.Documents.UploadDocument;
using EKA.Application.Tests.Fakes;
using EKA.Domain.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace EKA.Application.Tests.Documents;

public sealed class UploadDocumentHandlerTests
{
    private readonly FakeDocumentRepository _documents = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeFileStorage _storage = new();
    private readonly FakeIngestionQueue _queue = new();

    private UploadDocumentHandler Handler(long maxBytes = 1024 * 1024) => new(
        _documents, _unitOfWork, _storage, _queue,
        Options.Create(new IngestionOptions { MaxUploadBytes = maxBytes }), FixedTimeProvider.Default);

    private static UploadDocumentCommand Command(string text = "Leave policy: 25 days.", string fileName = "leave.txt", string? title = null) =>
        new(TestData.Caller(), fileName, new MemoryStream(Encoding.UTF8.GetBytes(text)), title, Authority: null);

    [Fact]
    public async Task An_upload_is_stored_recorded_and_handed_to_the_worker()
    {
        var result = await Handler().HandleAsync(Command());

        result.IsSuccess.ShouldBeTrue(result.ToString());
        result.Value.AlreadyRegistered.ShouldBeFalse();
        result.Value.Queued.ShouldBeTrue();

        var document = _documents.All.Single();
        document.Status.ShouldBe(IngestionStatus.Pending);
        document.Title.Value.ShouldBe("leave");
        document.File.ContentType.ShouldBe("text/plain");
        document.File.SizeBytes.ShouldBe(22);
        _storage.Files.Keys.ShouldBe([document.StorageKey]);
        _queue.Enqueued.ShouldBe([(TestData.Tenant, document.Id)]);
    }

    [Fact]
    public async Task The_hash_is_computed_by_the_server_from_the_bytes()
    {
        await Handler().HandleAsync(Command("abc"));

        // SHA-256("abc"), the FIPS 180-2 test vector.
        _documents.All.Single().File.ContentHash
            .ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public async Task The_same_bytes_again_return_the_first_document_and_leave_no_second_file()
    {
        var first = await Handler().HandleAsync(Command());
        var second = await Handler().HandleAsync(Command(fileName: "copy-of-leave.txt", title: "Different title"));

        second.Value.AlreadyRegistered.ShouldBeTrue();
        second.Value.Document.Id.ShouldBe(first.Value.Document.Id);
        _documents.All.Count.ShouldBe(1);
        _storage.Files.Count.ShouldBe(1);
        _queue.Enqueued.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_lost_hand_off_still_records_the_document_for_the_sweep()
    {
        _queue.Fail = true;

        var result = await Handler().HandleAsync(Command());

        result.IsSuccess.ShouldBeTrue();
        result.Value.Queued.ShouldBeFalse();
        _documents.All.Single().Status.ShouldBe(IngestionStatus.Pending);
    }

    [Theory]
    [InlineData("%PDF-1.7 pretend", "renamed.txt")]
    [InlineData("plain words", "fake.pdf")]
    [InlineData("plain words", "script.exe")]
    public async Task Bytes_that_disagree_with_the_name_are_refused_before_anything_is_stored(string text, string fileName)
    {
        var result = await Handler().HandleAsync(Command(text, fileName));

        result.Error.Code.ShouldBe("document.unsupported_file_type");
        _storage.Files.ShouldBeEmpty();
        _documents.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_oversized_file_is_refused_and_leaves_nothing_behind()
    {
        var result = await Handler(maxBytes: 10).HandleAsync(Command("This is more than ten bytes."));

        result.Error.Code.ShouldBe("document.file_too_large");
        _storage.Files.ShouldBeEmpty();
        _documents.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        var result = await Handler().HandleAsync(Command(""));

        result.Error.Code.ShouldBe("document.empty_file");
    }

    [Theory]
    [InlineData(@"C:\fakepath\leave.txt")]
    [InlineData("../../etc/leave.txt")]
    public async Task Only_the_last_segment_of_a_client_path_is_kept(string clientName)
    {
        await Handler().HandleAsync(Command(fileName: clientName));

        _documents.All.Single().File.FileName.ShouldBe("leave.txt");
    }
}

public sealed class ExtractDocumentTextHandlerTests
{
    private readonly FakeDocumentRepository _documents = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeFileStorage _storage = new();

    private ExtractDocumentTextHandler Handler(params ITextExtractor[] extractors) => new(
        _documents, _unitOfWork, _storage,
        new TextExtractionPipeline(extractors.Length > 0 ? extractors : [FakeExtractor.Returning(DocumentFileType.PlainText, "Leave: 25 days.")]),
        new ExtractedTextStore(_storage),
        FixedTimeProvider.Default,
        NullLogger<ExtractDocumentTextHandler>.Instance);

    private Document StoredTextDocument(IngestionStatus? driveTo = null)
    {
        var document = new Document(
            Guid.CreateVersion7(), TestData.Tenant, new DocumentTitle("Leave"),
            new FileMetadata("leave.txt", "text/plain", 15, TestData.Hash), "t/leave.txt", FixedTimeProvider.DefaultNow);

        if (driveTo is IngestionStatus.Extracting)
        {
            document.BeginExtraction(FixedTimeProvider.DefaultNow);
        }

        _documents.With(document);
        _storage.Put(document.StorageKey, "Leave: 25 days."u8.ToArray());
        return document;
    }

    private ExtractDocumentTextCommand For(Document document) => new(document.TenantId, document.Id);

    [Fact]
    public async Task Extraction_saves_the_text_and_moves_the_document_to_chunking()
    {
        var document = StoredTextDocument();

        var outcome = await Handler().HandleAsync(For(document));

        outcome.ShouldBe(ExtractionOutcome.Extracted);
        document.Status.ShouldBe(IngestionStatus.Chunking);
        document.ExtractedAt.ShouldNotBeNull();

        var artifact = await new ExtractedTextStore(_storage).LoadAsync(document);
        artifact.FormatVersion.ShouldBe(ExtractedTextStore.CurrentFormatVersion);
        artifact.ContentHash.ShouldBe(document.File.ContentHash);
        artifact.Sections.Single().Text.ShouldBe("Leave: 25 days.");
    }

    [Fact]
    public async Task A_redelivered_job_for_finished_work_does_nothing()
    {
        var document = StoredTextDocument();
        var extractor = FakeExtractor.Returning(DocumentFileType.PlainText, "text");
        await Handler(extractor).HandleAsync(For(document));

        var again = await Handler(extractor).HandleAsync(For(document));

        again.ShouldBe(ExtractionOutcome.NothingOwed);
        extractor.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task An_interrupted_extraction_is_resumed_by_the_next_delivery()
    {
        // The worker died after saving Extracting. The redelivered job must finish it, not
        // throw an invalid transition and leave the document stuck forever.
        var document = StoredTextDocument(driveTo: IngestionStatus.Extracting);

        var outcome = await Handler().HandleAsync(For(document));

        outcome.ShouldBe(ExtractionOutcome.Extracted);
        document.Status.ShouldBe(IngestionStatus.Chunking);
    }

    [Fact]
    public async Task An_unreadable_file_fails_the_document_and_does_not_throw()
    {
        // Not throwing is the point: a throw would make the job engine retry a corrupt file
        // five times for the same answer.
        var document = StoredTextDocument();

        var outcome = await Handler(FakeExtractor.Throwing(DocumentFileType.PlainText, new UnreadableDocumentException("encrypted")))
            .HandleAsync(For(document));

        outcome.ShouldBe(ExtractionOutcome.Unreadable);
        document.Status.ShouldBe(IngestionStatus.Failed);
        document.FailureReason.ShouldBe("encrypted");
    }

    [Fact]
    public async Task A_transient_failure_propagates_for_the_job_engine_to_retry()
    {
        var document = StoredTextDocument();

        await Should.ThrowAsync<IOException>(
            () => Handler(FakeExtractor.Throwing(DocumentFileType.PlainText, new IOException("disk busy"))).HandleAsync(For(document)));

        // Left Extracting, the state StartOrResumeExtraction picks up on the retry.
        document.Status.ShouldBe(IngestionStatus.Extracting);
    }

    [Fact]
    public async Task A_missing_stored_file_is_a_permanent_failure()
    {
        var document = StoredTextDocument();
        await _storage.DeleteAsync(document.StorageKey);

        var outcome = await Handler().HandleAsync(For(document));

        outcome.ShouldBe(ExtractionOutcome.Unreadable);
        document.FailureReason.ShouldNotBeNull().ShouldContain("missing");
    }

    [Fact]
    public async Task A_document_deleted_while_queued_is_not_an_error()
    {
        var outcome = await Handler().HandleAsync(new ExtractDocumentTextCommand(TestData.Tenant, Guid.NewGuid()));

        outcome.ShouldBe(ExtractionOutcome.NothingOwed);
    }

    [Fact]
    public async Task The_job_is_tenant_scoped_even_though_the_worker_is_not()
    {
        var document = StoredTextDocument();

        var outcome = await Handler().HandleAsync(new ExtractDocumentTextCommand(TestData.OtherTenant, document.Id));

        outcome.ShouldBe(ExtractionOutcome.NothingOwed);
        document.Status.ShouldBe(IngestionStatus.Pending);
    }

    [Fact]
    public async Task Deleting_a_document_removes_its_file_and_its_extracted_text()
    {
        var document = StoredTextDocument();
        await Handler().HandleAsync(For(document));
        _storage.Files.Count.ShouldBe(2);

        await new DeleteDocumentHandler(_documents, _unitOfWork, _storage, new FakeVectorStore(), NullLogger<DeleteDocumentHandler>.Instance)
            .HandleAsync(new DeleteDocumentCommand(TestData.Caller(document.TenantId), document.Id));

        _storage.Files.ShouldBeEmpty();
    }
}

public sealed class RecoverStalledDocumentsHandlerTests
{
    [Fact]
    public async Task Only_documents_pending_longer_than_the_threshold_are_re_enqueued()
    {
        var clock = FixedTimeProvider.Default;
        var documents = new FakeDocumentRepository()
            .With(TestData.Document(createdAt: clock.Now.AddMinutes(-30)))
            .With(TestData.Document(createdAt: clock.Now.AddMinutes(-1), contentHash: TestData.HashOf('b')))
            .With(TestData.FailedDocument());
        var queue = new FakeIngestionQueue();

        var requeued = await new RecoverStalledDocumentsHandler(
                documents, queue, Options.Create(new IngestionOptions { StalledAfter = TimeSpan.FromMinutes(5) }), clock)
            .HandleAsync();

        requeued.ShouldBe(1);
        queue.Enqueued.Single().DocumentId.ShouldBe(documents.All[0].Id);
    }
}
