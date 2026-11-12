using EKA.Application.Common;
using EKA.Application.Documents.GetDocument;
using EKA.Application.Documents.ListDocuments;
using EKA.Application.Tests.Fakes;
using EKA.Domain.Ingestion;
using Shouldly;

namespace EKA.Application.Tests.Documents;

public sealed class GetDocumentHandlerTests
{
    private readonly FakeDocumentRepository _documents = new();

    private GetDocumentHandler Handler => new(_documents);

    [Fact]
    public async Task A_document_in_the_callers_tenant_is_returned()
    {
        var document = TestData.Document();
        _documents.With(document);

        var result = await Handler.HandleAsync(new GetDocumentQuery(TestData.Tenant, document.Id));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Id.ShouldBe(document.Id);
    }

    [Fact]
    public async Task A_document_in_another_tenant_is_reported_as_not_found()
    {
        var document = TestData.Document(TestData.OtherTenant);
        _documents.With(document);

        // Same id, wrong tenant. The attacker's version of this test is a loop over GUIDs.
        var result = await Handler.HandleAsync(new GetDocumentQuery(TestData.Tenant, document.Id));

        result.IsFailure.ShouldBeTrue();

        // NotFound, not Forbidden. A 403 would confirm the id exists, which lets someone
        // map another tenant's document ids without ever reading one.
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Code.ShouldBe("document.not_found");
    }

    [Fact]
    public async Task A_missing_document_is_reported_as_not_found()
    {
        var result = await Handler.HandleAsync(
            new GetDocumentQuery(TestData.Tenant, Guid.CreateVersion7()));

        result.Error.Type.ShouldBe(ErrorType.NotFound);
    }
}

public sealed class ListDocumentsHandlerTests
{
    private readonly FakeDocumentRepository _documents = new();

    private ListDocumentsHandler Handler => new(_documents);

    private ListDocumentsQuery Query(string? status = null, int page = 1, int pageSize = 20) =>
        new(TestData.Tenant, status, page, pageSize);

    private void Seed(int count, EKA.Domain.Common.TenantId? tenantId = null)
    {
        for (var index = 0; index < count; index++)
        {
            _documents.With(TestData.Document(
                tenantId,
                title: $"Document {index}",
                contentHash: TestData.HashOf((char)('a' + (index % 6))),
                createdAt: FixedTimeProvider.DefaultNow.AddMinutes(index)));
        }
    }

    [Fact]
    public async Task A_page_reports_the_total_across_every_page()
    {
        Seed(25);

        var result = await Handler.HandleAsync(Query(pageSize: 10));

        result.Value.Items.Count.ShouldBe(10);
        result.Value.TotalCount.ShouldBe(25);
        result.Value.TotalPages.ShouldBe(3);
        result.Value.HasNextPage.ShouldBeTrue();
    }

    [Fact]
    public async Task The_last_page_reports_no_next_page()
    {
        Seed(25);

        var result = await Handler.HandleAsync(Query(page: 3, pageSize: 10));

        result.Value.Items.Count.ShouldBe(5);
        result.Value.HasNextPage.ShouldBeFalse();
    }

    [Fact]
    public async Task Another_tenants_documents_never_appear()
    {
        Seed(3);
        Seed(7, TestData.OtherTenant);

        var result = await Handler.HandleAsync(Query());

        result.Value.TotalCount.ShouldBe(3);
        result.Value.Items.Count.ShouldBe(3);
    }

    [Fact]
    public async Task An_oversized_page_is_capped_and_says_so()
    {
        Seed(ListDocumentsHandler.MaxPageSize + 10);

        var result = await Handler.HandleAsync(Query(pageSize: 100_000));

        // Capped rather than rejected: the request is reasonable, the limit is server
        // policy. The response states the size actually applied so the client is not left
        // guessing why it got fewer rows than it asked for.
        result.Value.Items.Count.ShouldBe(ListDocumentsHandler.MaxPageSize);
        result.Value.PageSize.ShouldBe(ListDocumentsHandler.MaxPageSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_page_below_one_is_rejected_rather_than_silently_corrected(int page)
    {
        var result = await Handler.HandleAsync(Query(page: page));

        // Serving page 1 instead would hide the client's bug until someone noticed their
        // pagination was skipping rows.
        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Code.ShouldBe("document.page_out_of_range");
    }

    [Fact]
    public async Task A_page_size_below_one_is_rejected()
    {
        var result = await Handler.HandleAsync(Query(pageSize: 0));

        result.Error.Code.ShouldBe("document.page_size_out_of_range");
    }

    [Fact]
    public async Task A_status_filter_narrows_the_result()
    {
        _documents.With(TestData.Document(contentHash: TestData.HashOf('a')));
        _documents.With(TestData.FailedDocument());

        var result = await Handler.HandleAsync(Query(status: nameof(IngestionStatus.Failed)));

        result.Value.TotalCount.ShouldBe(1);
        result.Value.Items.Single().Status.ShouldBe(nameof(IngestionStatus.Failed));
    }

    [Fact]
    public async Task A_status_filter_is_case_insensitive()
    {
        _documents.With(TestData.Document());

        var result = await Handler.HandleAsync(Query(status: "pending"));

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_status_is_rejected_rather_than_matching_nothing()
    {
        var result = await Handler.HandleAsync(Query(status: "Procrastinating"));

        result.Error.Code.ShouldBe("document.unknown_status");
        result.Error.Message.ShouldContain("Procrastinating");
    }

    [Fact]
    public async Task A_numeric_status_that_names_no_member_is_rejected()
    {
        _documents.With(TestData.Document());

        // Enum.TryParse happily accepts "999" and hands back a value no member declares.
        // Without the Enum.IsDefined check, this would succeed and filter out everything
        // while reporting a perfectly healthy empty page.
        var result = await Handler.HandleAsync(Query(status: "999"));

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("document.unknown_status");
    }

    [Fact]
    public async Task Documents_come_back_newest_first()
    {
        Seed(3);

        var result = await Handler.HandleAsync(Query());

        // Seed() stamps each document a minute later than the last, so the newest is the
        // one created last.
        result.Value.Items[0].Title.ShouldBe("Document 2");
        result.Value.Items[2].Title.ShouldBe("Document 0");
    }
}
