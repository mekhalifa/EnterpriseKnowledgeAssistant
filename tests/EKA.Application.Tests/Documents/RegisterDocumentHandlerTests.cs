using EKA.Application.Common;
using EKA.Application.Documents.RegisterDocument;
using EKA.Application.Tests.Fakes;
using EKA.Domain.Ingestion;
using Shouldly;

namespace EKA.Application.Tests.Documents;

/// <summary>
/// Tests named after the rule they protect, not the method they call.
/// </summary>
public sealed class RegisterDocumentHandlerTests
{
    private readonly FakeDocumentRepository _documents = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FixedTimeProvider _clock = FixedTimeProvider.Default;

    private RegisterDocumentHandler Handler => new(_documents, _unitOfWork, _clock);

    private static RegisterDocumentCommand Command(string? hash = null, int? authority = null) =>
        new(
            TestData.Tenant,
            "Employee handbook",
            "handbook.pdf",
            "application/pdf",
            2048,
            hash ?? TestData.Hash,
            authority);

    [Fact]
    public async Task A_registered_document_starts_pending_and_is_not_searchable()
    {
        var result = await Handler.HandleAsync(Command());

        result.IsSuccess.ShouldBeTrue();
        result.Value.AlreadyRegistered.ShouldBeFalse();
        result.Value.Document.Status.ShouldBe(nameof(IngestionStatus.Pending));

        // The one property that decides whether retrieval may return this document. It must
        // be false until ingestion actually completes, or an unindexed document becomes a
        // source for an answer that cites nothing.
        result.Value.Document.IsSearchable.ShouldBeFalse();
    }

    [Fact]
    public async Task Registration_records_the_time_from_the_injected_clock()
    {
        var result = await Handler.HandleAsync(Command());

        // Exact equality, not "close to now". That is only possible because the domain
        // never reads the clock (ADR-0008).
        result.Value.Document.CreatedAt.ShouldBe(FixedTimeProvider.DefaultNow);
    }

    [Fact]
    public async Task Registering_the_same_bytes_twice_returns_the_first_document()
    {
        var first = await Handler.HandleAsync(Command());
        var second = await Handler.HandleAsync(Command());

        second.Value.AlreadyRegistered.ShouldBeTrue();
        second.Value.Document.Id.ShouldBe(first.Value.Document.Id);

        // The point of idempotency: one document, not two. A second row here means every
        // future search returns each passage twice and the embedding bill doubles.
        _documents.All.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_duplicate_registration_does_not_commit_anything()
    {
        await Handler.HandleAsync(Command());
        await Handler.HandleAsync(Command());

        // Two calls, one commit. A handler that returns early but still saves would hold a
        // transaction open and write nothing, which is invisible until it is a deadlock.
        _unitOfWork.SaveCount.ShouldBe(1);
    }

    [Fact]
    public async Task The_same_bytes_in_another_tenant_are_a_different_document()
    {
        await Handler.HandleAsync(Command());

        var otherTenant = Command() with { TenantId = TestData.OtherTenant };
        var result = await Handler.HandleAsync(otherTenant);

        // Idempotency is scoped to the tenant. Deduplicating across tenants would mean one
        // customer's upload silently resolving to another customer's document — the worst
        // failure mode in this system, arrived at by way of a performance optimisation.
        result.Value.AlreadyRegistered.ShouldBeFalse();
        _documents.All.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_storage_key_is_derived_from_the_tenant_and_never_from_the_file_name()
    {
        var command = Command() with { FileName = "../../other-tenant/secret.pdf" };

        await Handler.HandleAsync(command);

        var stored = _documents.All.Single();

        stored.StorageKey.ShouldStartWith($"{TestData.Tenant.Value:N}/");
        stored.StorageKey.ShouldNotContain("..");
        stored.StorageKey.ShouldNotContain("secret");
    }

    [Fact]
    public async Task An_omitted_authority_falls_back_to_standard()
    {
        var result = await Handler.HandleAsync(Command(authority: null));

        result.Value.Document.Authority.ShouldBe(AuthorityScore.Standard.Value);
    }

    [Fact]
    public async Task A_supplied_authority_is_kept()
    {
        var result = await Handler.HandleAsync(Command(authority: 100));

        result.Value.Document.Authority.ShouldBe(100);
    }

    [Fact]
    public async Task The_response_never_carries_the_storage_key_or_the_tenant()
    {
        var result = await Handler.HandleAsync(Command());

        // Asserted against the contract type's shape rather than a serialized string: if
        // somebody adds StorageKey to DocumentResponse, this stops compiling, which is a
        // better failure than a test that still passes while the key goes out on the wire.
        var properties = typeof(EKA.Contracts.Documents.DocumentResponse)
            .GetProperties()
            .Select(property => property.Name);

        properties.ShouldNotContain("StorageKey");
        properties.ShouldNotContain("TenantId");
        properties.ShouldNotContain("CreatedBy");
    }
}
