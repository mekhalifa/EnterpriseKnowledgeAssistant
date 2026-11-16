using EKA.Domain.Access;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;

namespace EKA.Application.Tests;

/// <summary>
/// Builders for the objects every test here needs, so a test states only what it is about.
/// </summary>
/// <remarks>
/// A test that spends eight lines constructing a valid <c>Document</c> before asserting one
/// thing has buried its subject. Every parameter below has a working default and every one
/// can be overridden, so <c>Document(status: IngestionStatus.Failed)</c> reads as exactly
/// what the test cares about.
/// </remarks>
internal static class TestData
{
    public static readonly TenantId Tenant = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));

    /// <summary>A second tenant, for proving that queries do not cross the boundary.</summary>
    public static readonly TenantId OtherTenant =
        new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));

    /// <summary>A valid lowercase hex SHA-256 digest.</summary>
    public const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static string HashOf(char filler) => new(filler, 64);

    /// <summary>
    /// A caller in <paramref name="tenantId"/> (default <see cref="Tenant"/>), as the API builds
    /// one from a token. Every tenant-wide document is readable to it.
    /// </summary>
    public static AccessScope Caller(TenantId? tenantId = null, string subject = "alice", bool isAdministrator = false, params string[] groups) =>
        AccessScope.For(tenantId ?? Tenant, subject, groups, isAdministrator);

    public static FileMetadata File(string? contentHash = null) =>
        new("policy.pdf", "application/pdf", 1024, contentHash ?? Hash);

    public static Document Document(
        TenantId? tenantId = null,
        Guid? id = null,
        string title = "Employee handbook",
        string? contentHash = null,
        DateTimeOffset? createdAt = null,
        DocumentAccess? access = null,
        string? owner = "alice")
    {
        var documentId = id ?? Guid.CreateVersion7();

        // Owned by alice, the default Caller(), as every uploaded document is owned by its
        // uploader (Step 12). Tests about non-owners say so explicitly.
        return new Document(
            documentId,
            tenantId ?? Tenant,
            new DocumentTitle(title),
            File(contentHash),
            $"{(tenantId ?? Tenant).Value:N}/{documentId:N}.pdf",
            createdAt ?? Fakes.FixedTimeProvider.DefaultNow,
            access: access,
            ownerSubject: owner);
    }

    /// <summary>
    /// A searchable document: driven through the real lifecycle to Completed, one chunk per
    /// text, every chunk recorded as embedded with <paramref name="model"/>.
    /// </summary>
    public static Document Completed(
        TenantId? tenantId = null,
        DocumentAccess? access = null,
        string model = "test-model",
        params string[] chunkTexts)
    {
        var document = Document(tenantId, access: access);
        var at = Fakes.FixedTimeProvider.DefaultNow;
        var texts = chunkTexts.Length == 0 ? ["Annual leave is 25 days."] : chunkTexts;

        document.BeginExtraction(at);
        document.CompleteExtraction(at);
        document.AddChunks(
            texts.Select((text, i) => new DocumentChunk(Guid.CreateVersion7(), document.Id, new ChunkIndex(i), text, new TokenCount(10), at, sectionPath: "Leave")),
            at);
        document.RecordEmbeddings(document.Chunks.Select(chunk => chunk.Index), model, at);
        document.CompleteIngestion(at);

        return document;
    }

    /// <summary>A document readable only by the given principals, e.g. <c>Restricted("group:hr")</c>.</summary>
    public static Document Restricted(params string[] principalKeys) =>
        Document(access: DocumentAccess.Of(principalKeys.Select(AccessPrincipal.Parse)));

    /// <summary>
    /// A document that has been through extraction and failed, so it is eligible for reset.
    /// </summary>
    /// <remarks>
    /// Built by driving the real lifecycle rather than by assigning a status, because there
    /// is no way to assign one — the entity has no public setter. That constraint is what
    /// makes a "failed" document in a test necessarily a document that could exist in
    /// production.
    /// </remarks>
    public static Document FailedDocument(TenantId? tenantId = null, Guid? id = null)
    {
        var document = Document(tenantId, id);
        var at = Fakes.FixedTimeProvider.DefaultNow;

        document.BeginExtraction(at);
        document.Fail("Extraction timed out.", at);

        return document;
    }
}
