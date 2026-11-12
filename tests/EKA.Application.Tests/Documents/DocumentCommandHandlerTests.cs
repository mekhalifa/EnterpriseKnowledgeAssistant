using EKA.Application.Common;
using EKA.Application.Documents.DeleteDocument;
using EKA.Application.Documents.RenameDocument;
using EKA.Application.Documents.ResetDocument;
using EKA.Application.Tests.Fakes;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using Shouldly;

namespace EKA.Application.Tests.Documents;

public sealed class RenameDocumentHandlerTests
{
    private readonly FakeDocumentRepository _documents = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FixedTimeProvider _clock = FixedTimeProvider.Default;

    private RenameDocumentHandler Handler => new(_documents, _unitOfWork, _clock);

    [Fact]
    public async Task Renaming_changes_the_title_and_stamps_the_modification()
    {
        var document = TestData.Document();
        _documents.With(document);
        _clock.Advance(TimeSpan.FromHours(2));

        var result = await Handler.HandleAsync(
            new RenameDocumentCommand(TestData.Tenant, document.Id, "  Updated handbook  "));

        result.Value.Title.ShouldBe("Updated handbook"); // trimmed by DocumentTitle
        result.Value.ModifiedAt.ShouldBe(FixedTimeProvider.DefaultNow.AddHours(2));
        _unitOfWork.SaveCount.ShouldBe(1);
    }

    [Fact]
    public async Task Renaming_a_document_in_another_tenant_is_not_found_and_commits_nothing()
    {
        var document = TestData.Document(TestData.OtherTenant);
        _documents.With(document);

        var result = await Handler.HandleAsync(
            new RenameDocumentCommand(TestData.Tenant, document.Id, "Mine now"));

        result.Error.Type.ShouldBe(ErrorType.NotFound);

        // The title of the other tenant's document is unchanged, and nothing was written.
        document.Title.Value.ShouldBe("Employee handbook");
        _unitOfWork.SaveCount.ShouldBe(0);
    }

    [Fact]
    public async Task An_empty_title_is_refused_by_the_domain()
    {
        var document = TestData.Document();
        _documents.With(document);

        // The validation filter rejects this at the edge in a real request. The domain is
        // the backstop, and it must still refuse — otherwise the ingestion worker or a
        // future caller could create a state the API would never accept.
        await Should.ThrowAsync<DomainException>(() => Handler.HandleAsync(
            new RenameDocumentCommand(TestData.Tenant, document.Id, "   ")));

        _unitOfWork.SaveCount.ShouldBe(0);
    }
}

public sealed class ResetDocumentHandlerTests
{
    private readonly FakeDocumentRepository _documents = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FixedTimeProvider _clock = FixedTimeProvider.Default;

    private ResetDocumentHandler Handler => new(_documents, _unitOfWork, _clock);

    [Fact]
    public async Task A_failed_document_returns_to_pending()
    {
        var document = TestData.FailedDocument();
        _documents.With(document);

        var result = await Handler.HandleAsync(
            new ResetDocumentCommand(TestData.Tenant, document.Id));

        result.Value.Status.ShouldBe(nameof(IngestionStatus.Pending));
        result.Value.ChunkCount.ShouldBe(0);
        _unitOfWork.SaveCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_reset_keeps_the_failure_count()
    {
        var document = TestData.FailedDocument();
        _documents.With(document);

        var result = await Handler.HandleAsync(
            new ResetDocumentCommand(TestData.Tenant, document.Id));

        // Deliberately not reset by Document.Reset: a document that fails five times needs
        // escalating, and a counter that forgets makes that impossible to notice.
        result.Value.FailureCount.ShouldBe(1);
    }

    [Fact]
    public async Task Resetting_a_document_that_never_failed_throws_rather_than_returning_a_result()
    {
        var document = TestData.Document(); // Pending
        _documents.With(document);

        // This is the deliberate exception to "Result, not exceptions" (ADR-0011). The
        // handler does not pre-check the status, because a pre-check would be a second
        // copy of the state machine that lives in Document. The API maps this exception to
        // 409 in one place.
        await Should.ThrowAsync<InvalidStateTransitionException>(() => Handler.HandleAsync(
            new ResetDocumentCommand(TestData.Tenant, document.Id)));

        _unitOfWork.SaveCount.ShouldBe(0);
    }

    [Fact]
    public async Task Resetting_a_document_in_another_tenant_is_not_found()
    {
        var document = TestData.FailedDocument(TestData.OtherTenant);
        _documents.With(document);

        var result = await Handler.HandleAsync(
            new ResetDocumentCommand(TestData.Tenant, document.Id));

        result.Error.Type.ShouldBe(ErrorType.NotFound);
        document.Status.ShouldBe(IngestionStatus.Failed);
    }
}

public sealed class DeleteDocumentHandlerTests
{
    private readonly FakeDocumentRepository _documents = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private DeleteDocumentHandler Handler => new(_documents, _unitOfWork);

    [Fact]
    public async Task Deleting_stages_the_removal_and_commits_once()
    {
        var document = TestData.Document();
        _documents.With(document);

        var result = await Handler.HandleAsync(
            new DeleteDocumentCommand(TestData.Tenant, document.Id));

        result.IsSuccess.ShouldBeTrue();
        _documents.Removed.ShouldContain(document);
        _unitOfWork.SaveCount.ShouldBe(1);
    }

    [Fact]
    public async Task Deleting_something_that_is_not_there_is_not_found_not_success()
    {
        var result = await Handler.HandleAsync(
            new DeleteDocumentCommand(TestData.Tenant, Guid.CreateVersion7()));

        // A cheerful 204 here would make a wrong-tenant delete indistinguishable from a
        // legitimate one in every log the system produces.
        result.Error.Type.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public async Task Deleting_another_tenants_document_leaves_it_alone()
    {
        var document = TestData.Document(TestData.OtherTenant);
        _documents.With(document);

        var result = await Handler.HandleAsync(
            new DeleteDocumentCommand(TestData.Tenant, document.Id));

        result.Error.Type.ShouldBe(ErrorType.NotFound);
        _documents.All.ShouldContain(document);
        _documents.Removed.ShouldBeEmpty();
        _unitOfWork.SaveCount.ShouldBe(0);
    }
}
