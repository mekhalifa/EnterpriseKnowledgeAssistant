using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using EKA.Domain.Prompting;
using EKA.Domain.Providers;

namespace EKA.IntegrationTests;

/// <summary>Builders for valid aggregates, so tests read as the rule being checked.</summary>
internal static class TestFixtures
{
    public const string Sha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A fresh tenant per test, so rows written by one cannot affect another.</summary>
    public static TenantId NewTenant() => TenantId.New();

    public static Document NewDocument(
        TenantId tenant,
        string title = "Employee Handbook",
        string contentHash = Sha256) =>
        new(
            Guid.NewGuid(),
            tenant,
            new DocumentTitle(title),
            new FileMetadata("handbook.pdf", "application/pdf", 2048, contentHash),
            storageKey: "tenant/documents/handbook.pdf",
            Now);

    public static DocumentChunk NewChunk(Guid documentId, int index, string? content = null) =>
        new(
            Guid.NewGuid(),
            documentId,
            new ChunkIndex(index),
            content ?? $"Chunk {index} content.",
            new TokenCount(120),
            Now,
            new PageRange(index + 1, index + 2),
            sectionPath: "Benefits / Leave");

    /// <summary>Drives a document to Completed so it can be round-tripped in full.</summary>
    public static Document CompletedDocument(TenantId tenant, int chunkCount = 3)
    {
        var document = NewDocument(tenant);

        document.BeginExtraction(Now);
        document.CompleteExtraction(Now);
        document.AddChunks(
            Enumerable.Range(0, chunkCount).Select(i => NewChunk(document.Id, i)),
            Now);
        document.CompleteIngestion(Now);

        return document;
    }

    public static AIProvider NewProvider(string name = "openai") =>
        new(
            Guid.NewGuid(),
            name,
            AIProviderKind.OpenAI,
            chatModel: "gpt-5",
            embeddingModel: "text-embedding-3-large",
            embeddingDimensions: 3072,
            secretReference: "Eka:Providers:OpenAI:ApiKey",
            Now);

    public static PromptTemplate NewTemplate(string name, int version, Guid providerId) =>
        new(
            Guid.NewGuid(),
            name,
            version,
            systemPrompt: "You are a careful enterprise assistant.",
            userPromptTemplate: "Context:\n{{context}}\n\nQuestion: {{question}}",
            providerId,
            Now);
}
