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

    public static FileMetadata File(string? contentHash = null) =>
        new("policy.pdf", "application/pdf", 1024, contentHash ?? Hash);

    public static Document Document(
        TenantId? tenantId = null,
        Guid? id = null,
        string title = "Employee handbook",
        string? contentHash = null,
        DateTimeOffset? createdAt = null)
    {
        var documentId = id ?? Guid.CreateVersion7();

        return new Document(
            documentId,
            tenantId ?? Tenant,
            new DocumentTitle(title),
            File(contentHash),
            $"{(tenantId ?? Tenant).Value:N}/{documentId:N}.pdf",
            createdAt ?? Fakes.FixedTimeProvider.DefaultNow);
    }

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
