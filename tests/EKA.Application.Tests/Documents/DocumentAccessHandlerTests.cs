using System.Text;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Common;
using EKA.Application.Documents.DeleteDocument;
using EKA.Application.Documents.GetDocument;
using EKA.Application.Documents.Ingestion;
using EKA.Application.Documents.ListDocuments;
using EKA.Application.Documents.RenameDocument;
using EKA.Application.Documents.SetDocumentAccess;
using EKA.Application.Documents.UploadDocument;
using EKA.Application.Tests.Fakes;
using EKA.Contracts.Documents;
using EKA.Domain.Access;
using EKA.Domain.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace EKA.Application.Tests.Documents;

public sealed class SetDocumentAccessHandlerTests
{
    private readonly FakeDocumentRepository _documents = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeVectorStore _vectors = new();

    private SetDocumentAccessHandler Handler() => new(_documents, _unitOfWork, _vectors, new FixedTimeProvider(FixedTimeProvider.DefaultNow));

    private void SeedVector(Document document) =>
        _vectors.Seed(new ChunkVector(document.TenantId, document.Id, new ChunkIndex(0), new float[4], "m", null, null, document.AccessKeys));

    [Fact]
    public async Task Access_changes_in_SQL_and_in_the_vector_payload()
    {
        var document = TestData.Document();
        _documents.With(document);
        SeedVector(document);

        var result = await Handler().HandleAsync(new SetDocumentAccessCommand(TestData.Caller(), document.Id, ["user:olivia", "group:hr"]));

        result.Value.Access.ShouldBe(["group:hr", "user:olivia"]);
        document.AccessKeys.ShouldBe(["group:hr", "user:olivia"]);
        _vectors.Points.ShouldHaveSingleItem().AccessKeys.ShouldBe(["group:hr", "user:olivia"]);
        _unitOfWork.SaveCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_caller_who_cannot_read_the_document_cannot_grant_themselves_access()
    {
        var document = TestData.Restricted("user:olivia");
        _documents.With(document);

        var result = await Handler().HandleAsync(new SetDocumentAccessCommand(TestData.Caller(subject: "bob"), document.Id, ["tenant"]));

        result.Error.Code.ShouldBe("document.not_found");
        document.AccessKeys.ShouldBe(["user:olivia"]);
        _unitOfWork.SaveCount.ShouldBe(0);
    }

    [Fact]
    public async Task Another_tenants_document_is_not_found()
    {
        var document = TestData.Document(TestData.OtherTenant);
        _documents.With(document);

        (await Handler().HandleAsync(new SetDocumentAccessCommand(TestData.Caller(), document.Id, ["tenant"])))
            .Error.Code.ShouldBe("document.not_found");
    }

    [Theory]
    [InlineData("")]
    [InlineData("tenant,user:bob")]
    [InlineData("everyone")]
    public async Task A_policy_the_domain_refuses_is_a_validation_error_not_a_crash(string principals)
    {
        var document = TestData.Document();
        _documents.With(document);

        var result = await Handler().HandleAsync(new SetDocumentAccessCommand(
            TestData.Caller(), document.Id, principals.Split(',', StringSplitOptions.RemoveEmptyEntries)));

        result.Error.Type.ShouldBe(ErrorType.Validation);
        _unitOfWork.SaveCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_the_vector_store_is_unreachable_nothing_changes_and_the_caller_sees_the_failure()
    {
        // The payload is written first. If it cannot be, SQL is never touched: no state in which
        // SQL grants Carol while her search could never find the document, and nothing reports
        // success for a change that only half happened.
        var document = TestData.Document();
        _documents.With(document);
        _vectors.FailAccessUpdate = true;

        await Should.ThrowAsync<IOException>(() =>
            Handler().HandleAsync(new SetDocumentAccessCommand(TestData.Caller(), document.Id, ["user:carol"])));

        document.AccessKeys.ShouldBe(["tenant"]);
        _unitOfWork.SaveCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(new[] { "tenant" }, true)]
    [InlineData(new[] { "user:alice", "group:hr" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "tenant", "group:hr" }, false)]
    [InlineData(new[] { "Tenant" }, false)]
    [InlineData(new[] { "user: alice" }, false)]
    public void Requests_are_checked_for_shape_with_the_domains_own_rules(string[] principals, bool valid)
    {
        new SetDocumentAccessRequestValidator().Validate(new SetDocumentAccessRequest(principals)).IsValid.ShouldBe(valid);
    }

    [Fact]
    public void Too_many_principals_are_refused_at_the_edge()
    {
        var principals = Enumerable.Range(0, DocumentAccess.MaxPrincipals + 1).Select(i => $"user:u{i}").ToArray();

        new SetDocumentAccessRequestValidator().Validate(new SetDocumentAccessRequest(principals)).IsValid.ShouldBeFalse();
    }
}

/// <summary>
/// Step 9 extended access control from retrieval to every document use case: a document the
/// caller may not read does not exist for them, whatever they try to do with it.
/// </summary>
public sealed class DocumentUseCaseAccessTests
{
    private readonly FakeDocumentRepository _documents = new();
    private readonly AccessScope _bob = TestData.Caller(subject: "bob");
    private readonly AccessScope _carolInHr = TestData.Caller(subject: "carol", groups: "hr");

    [Fact]
    public async Task Get_hides_a_document_from_callers_outside_its_access_list()
    {
        var document = TestData.Restricted("group:hr");
        _documents.With(document);
        var handler = new GetDocumentHandler(_documents);

        (await handler.HandleAsync(new GetDocumentQuery(_bob, document.Id))).Error.Code.ShouldBe("document.not_found");
        (await handler.HandleAsync(new GetDocumentQuery(_carolInHr, document.Id))).Value.Access.ShouldBe(["group:hr"]);
    }

    [Fact]
    public async Task List_counts_and_returns_only_readable_documents()
    {
        var open = TestData.Document(contentHash: TestData.HashOf('1'));
        _documents.With(open).With(TestData.Restricted("group:hr")).With(TestData.Restricted("user:olivia"));

        var page = (await new ListDocumentsHandler(_documents).HandleAsync(new ListDocumentsQuery(_bob, null, 1, 20))).Value;

        page.Items.Select(item => item.Id).ShouldBe([open.Id]);
        page.TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task Rename_and_delete_treat_an_unreadable_document_as_missing()
    {
        var document = TestData.Restricted("group:hr");
        _documents.With(document);

        (await new RenameDocumentHandler(_documents, new FakeUnitOfWork(), new FixedTimeProvider(FixedTimeProvider.DefaultNow))
                .HandleAsync(new RenameDocumentCommand(_bob, document.Id, "Mine now")))
            .Error.Code.ShouldBe("document.not_found");

        (await new DeleteDocumentHandler(_documents, new FakeUnitOfWork(), new FakeFileStorage(), new FakeVectorStore(), NullLogger<DeleteDocumentHandler>.Instance)
                .HandleAsync(new DeleteDocumentCommand(_bob, document.Id)))
            .Error.Code.ShouldBe("document.not_found");

        _documents.Removed.ShouldBeEmpty();
        document.Title.Value.ShouldBe("Employee handbook");
    }

    [Fact]
    public async Task Uploading_bytes_already_held_in_a_restricted_document_is_a_conflict_that_names_nothing()
    {
        var bytes = Encoding.UTF8.GetBytes("Board minutes, 2026.");
        var handler = new UploadDocumentHandler(
            _documents, new FakeUnitOfWork(), new FakeFileStorage(), new FakeIngestionQueue(),
            Options.Create(new IngestionOptions()), new FixedTimeProvider(FixedTimeProvider.DefaultNow));

        var first = await handler.HandleAsync(new UploadDocumentCommand(TestData.Caller(subject: "olivia"), "minutes.txt", new MemoryStream(bytes), null, null));
        _documents.All.Single().ChangeAccess(DocumentAccess.Of([AccessPrincipal.User("olivia")]), DateTimeOffset.UtcNow);

        var second = await handler.HandleAsync(new UploadDocumentCommand(_bob, "copy.txt", new MemoryStream(bytes), null, null));

        second.Error.Code.ShouldBe("document.duplicate_not_accessible");
        second.Error.Message.ShouldNotContain(first.Value.Document.Id.ToString());
        _documents.All.Count.ShouldBe(1);

        // Olivia re-uploading is the ordinary idempotent 200.
        (await handler.HandleAsync(new UploadDocumentCommand(TestData.Caller(subject: "olivia"), "minutes.txt", new MemoryStream(bytes), null, null)))
            .Value.AlreadyRegistered.ShouldBeTrue();
    }
}
