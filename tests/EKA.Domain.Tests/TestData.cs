using EKA.Domain.Common;
using EKA.Domain.Ingestion;

namespace EKA.Domain.Tests;

/// <summary>
/// Shared fixtures. Keeps each test's body about the rule being asserted rather than
/// about constructing five valid objects first.
/// </summary>
internal static class TestData
{
    /// <summary>
    /// A fixed instant. Every domain method takes time as a parameter, so tests can assert
    /// on an exact timestamp instead of "roughly now" — which is the whole reason the
    /// domain never reads the clock itself.
    /// </summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset Later = Now.AddMinutes(5);

    public static readonly TenantId Tenant =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    /// <summary>A valid 64-character lowercase hex SHA-256 digest.</summary>
    public const string Sha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static FileMetadata Pdf(string fileName = "handbook.pdf", long sizeBytes = 2048) =>
        new(fileName, "application/pdf", sizeBytes, Sha256);

    public static DocumentTitle Title(string value = "Employee Handbook") => new(value);

    public static Document NewDocument(
        Guid? id = null,
        DocumentTitle? title = null,
        AuthorityScore? authority = null) =>
        new(
            id ?? Guid.NewGuid(),
            Tenant,
            title ?? Title(),
            Pdf(),
            storageKey: "tenant-1/documents/handbook.pdf",
            Now,
            authority);

    public static DocumentChunk Chunk(
        Guid documentId,
        int index,
        string? content = null,
        int tokens = 100) =>
        new(
            Guid.NewGuid(),
            documentId,
            new ChunkIndex(index),
            content ?? $"Content of chunk {index}.",
            new TokenCount(tokens),
            Now);

    public static List<DocumentChunk> Chunks(Guid documentId, int count) =>
        [.. Enumerable.Range(0, count).Select(i => Chunk(documentId, i))];

    /// <summary>
    /// Drives a fresh document along the happy path until it reaches
    /// <paramref name="status"/>, so transition tests can start from any state without
    /// repeating the walk.
    /// </summary>
    public static Document DocumentIn(IngestionStatus status)
    {
        var document = NewDocument();

        if (status is IngestionStatus.Pending)
        {
            return document;
        }

        document.BeginExtraction(Now);

        if (status is IngestionStatus.Extracting)
        {
            return document;
        }

        document.CompleteExtraction(Now);

        if (status is IngestionStatus.Chunking)
        {
            return document;
        }

        document.AddChunks(Chunks(document.Id, 3), Now);

        if (status is IngestionStatus.Embedding)
        {
            return document;
        }

        if (status is IngestionStatus.Failed)
        {
            document.Fail("stage failed during test setup", Now);
            return document;
        }

        document.CompleteIngestion(Now);
        document.ClearDomainEvents();

        return status is IngestionStatus.Completed
            ? document
            : throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled status.");
    }
}
