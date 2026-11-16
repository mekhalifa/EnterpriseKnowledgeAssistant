using System.Security.Cryptography;
using System.Text;
using EKA.Application.Abstractions.Ingestion;
using EKA.Domain.Access;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using Microsoft.Extensions.DependencyInjection;

namespace EKA.IntegrationTests.Api;

/// <summary>
/// Puts a one-chunk, Completed document straight into SQL Server and Qdrant, as the worker
/// would leave it, for tests about what happens <i>after</i> ingestion.
/// </summary>
/// <remarks>
/// Driven through the real domain lifecycle (the aggregate has no setters to shortcut), and
/// the vector comes from the same embedding stand-in the API embeds questions with, so the
/// search finds it. The whole pipeline, upload to answer, is proven separately in
/// <c>RetrievalEndToEndTests</c>.
/// </remarks>
internal static class SearchableDocuments
{
    public static async Task<Document> SeedAsync(
        SqlServerFixture sqlServer,
        EkaApiFactory api,
        Guid tenant,
        string text,
        string title = "Leave policy",
        DocumentAccess? access = null,
        string? owner = "owner")
    {
        var tenantId = new TenantId(tenant);
        var now = DateTimeOffset.UtcNow;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text + Guid.NewGuid())));

        var document = new Document(
            Guid.CreateVersion7(), tenantId, new DocumentTitle(title),
            new FileMetadata("policy.md", "text/markdown", text.Length, hash), $"seeded/{hash}", now, access: access, ownerSubject: owner);
        document.BeginExtraction(now);
        document.CompleteExtraction(now);
        document.AddChunks([new DocumentChunk(Guid.CreateVersion7(), document.Id, ChunkIndex.First, text, new TokenCount(20), now, sectionPath: "Leave")], now);
        document.RecordEmbeddings([ChunkIndex.First], api.Embeddings.Model, now);
        document.CompleteIngestion(now);

        await using (var context = sqlServer.CreateContext())
        {
            context.Documents.Add(document);
            await context.SaveChangesAsync();
        }

        var vector = (await api.Embeddings.EmbedAsync([text])).Value.Vectors[0];
        await api.Services.GetRequiredService<IVectorStore>().UpsertAsync(
        [
            new ChunkVector(tenantId, document.Id, ChunkIndex.First, vector, api.Embeddings.Model, null, "Leave", document.AccessKeys),
        ]);

        return document;
    }
}
