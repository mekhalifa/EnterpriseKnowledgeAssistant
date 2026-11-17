using EKA.Application.Documents.FailExhaustedIngestion;
using EKA.Application.Tests.Fakes;
using EKA.Domain.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace EKA.Application.Tests.Documents;

/// <summary>
/// The terminal step for an ingestion job whose retries are exhausted (Step 14).
/// </summary>
public sealed class FailExhaustedIngestionTests
{
    private readonly FakeDocumentRepository _documents = new();
    private readonly FakeVectorStore _vectors = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private FailExhaustedIngestionHandler Handler() =>
        new(_documents, _vectors, _unitOfWork, FixedTimeProvider.Default, NullLogger<FailExhaustedIngestionHandler>.Instance);

    private static Document StuckIn(IngestionStatus status)
    {
        var document = TestData.Document();
        var at = FixedTimeProvider.DefaultNow;

        document.BeginExtraction(at);
        if (status is IngestionStatus.Chunking or IngestionStatus.Embedding)
        {
            document.CompleteExtraction(at);
        }

        if (status is IngestionStatus.Embedding)
        {
            document.AddChunks([new DocumentChunk(Guid.CreateVersion7(), document.Id, new ChunkIndex(0), "text", new Domain.Common.TokenCount(1), at)], at);
        }

        document.Status.ShouldBe(status);
        return document;
    }

    [Theory]
    [InlineData(IngestionStatus.Extracting)]
    [InlineData(IngestionStatus.Chunking)]
    [InlineData(IngestionStatus.Embedding)]
    public async Task A_document_stuck_mid_pipeline_becomes_failed_so_its_owner_can_reset_it(IngestionStatus stuckIn)
    {
        var document = StuckIn(stuckIn);
        _documents.With(document);

        var marked = await Handler().HandleAsync(new FailExhaustedIngestionCommand(document.TenantId, document.Id, "HttpRequestException"));

        marked.ShouldBeTrue();
        document.Status.ShouldBe(IngestionStatus.Failed);
        document.FailureReason!.ShouldContain("HttpRequestException");
        _unitOfWork.SaveCount.ShouldBe(1);

        // The point of failing it: Reset only accepts a failed document.
        Should.NotThrow(() => document.Reset(FixedTimeProvider.DefaultNow));
    }

    [Fact]
    public async Task A_failed_document_leaves_no_vectors_a_search_could_return()
    {
        var document = StuckIn(IngestionStatus.Embedding);
        _documents.With(document);

        await Handler().HandleAsync(new FailExhaustedIngestionCommand(document.TenantId, document.Id, "TimeoutException"));

        _vectors.DeleteCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_late_report_never_overwrites_a_completed_document()
    {
        var document = TestData.Completed();
        _documents.With(document);

        var marked = await Handler().HandleAsync(new FailExhaustedIngestionCommand(document.TenantId, document.Id, "IOException"));

        marked.ShouldBeFalse();
        document.Status.ShouldBe(IngestionStatus.Completed);
        _unitOfWork.SaveCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_document_deleted_while_retrying_is_not_an_error()
    {
        var marked = await Handler().HandleAsync(new FailExhaustedIngestionCommand(TestData.Tenant, Guid.NewGuid(), "IOException"));

        marked.ShouldBeFalse();
    }

    [Fact]
    public async Task The_lookup_is_tenant_scoped()
    {
        var document = StuckIn(IngestionStatus.Extracting);
        _documents.With(document);

        var marked = await Handler().HandleAsync(new FailExhaustedIngestionCommand(TestData.OtherTenant, document.Id, "IOException"));

        marked.ShouldBeFalse();
        document.Status.ShouldBe(IngestionStatus.Extracting);
    }

    [Fact]
    public async Task The_stored_reason_is_bounded()
    {
        var document = StuckIn(IngestionStatus.Extracting);
        _documents.With(document);

        await Handler().HandleAsync(new FailExhaustedIngestionCommand(document.TenantId, document.Id, new string('x', 5000)));

        document.FailureReason!.Length.ShouldBeLessThanOrEqualTo(FailExhaustedIngestionHandler.MaxReasonLength);
    }
}
