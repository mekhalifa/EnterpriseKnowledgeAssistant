using EKA.Application.Abstractions.Ingestion;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using EKA.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Shouldly;

namespace EKA.IntegrationTests.Persistence;

/// <summary>
/// The Qdrant adapter against a real Qdrant. The properties Step 8 rests on are properties of
/// the server's behaviour (overwrite by id, filtered delete, payload indexes), so a fake
/// would only prove the fake.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class QdrantVectorStoreTests(SqlServerFixture fixture)
{
    private readonly string _collection = $"adapter_{Guid.NewGuid():N}";
    private readonly TenantId _tenant = new(Guid.NewGuid());

    /// <summary>
    /// The store exactly as a host gets it: from <c>AddInfrastructure</c>. A fresh provider is a
    /// fresh process as far as the adapter's "collection already checked" memory goes.
    /// </summary>
    private ServiceProvider NewHost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:EkaDatabase"] = fixture.ConnectionString,
                ["VectorStore:Endpoint"] = fixture.QdrantEndpoint.ToString(),
                ["VectorStore:CollectionName"] = _collection,
            })
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddInfrastructure(configuration)
            .BuildServiceProvider();
    }

    private ChunkVector Vector(Guid documentId, int index, int dimensions = 4, TenantId? tenant = null) =>
        new(
            tenant ?? _tenant,
            documentId,
            new ChunkIndex(index),
            Enumerable.Range(1, dimensions).Select(i => (float)(i + index)).ToArray(),
            "test-model",
            PageRange.SinglePage(index + 1),
            "Policy / Leave");

    [Fact]
    public async Task Writing_the_same_chunks_again_overwrites_them_instead_of_adding_duplicates()
    {
        await using var host = NewHost();
        var store = host.GetRequiredService<IVectorStore>();
        var document = Guid.NewGuid();
        ChunkVector[] chunks = [Vector(document, 0), Vector(document, 1), Vector(document, 2)];

        await store.UpsertAsync(chunks);
        await store.UpsertAsync(chunks);           // a retried batch
        await store.UpsertAsync([Vector(document, 1)]); // a redelivered job

        (await store.CountDocumentAsync(_tenant, document)).ShouldBe(3);
    }

    [Fact]
    public async Task Deleting_needs_the_right_tenant_and_touches_only_that_document()
    {
        await using var host = NewHost();
        var store = host.GetRequiredService<IVectorStore>();
        var document = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        await store.UpsertAsync([Vector(document, 0), Vector(document, 1), Vector(neighbour, 0)]);

        await store.DeleteDocumentAsync(new TenantId(Guid.NewGuid()), document);
        (await store.CountDocumentAsync(_tenant, document)).ShouldBe(2);

        await store.DeleteDocumentAsync(_tenant, document);
        (await store.CountDocumentAsync(_tenant, document)).ShouldBe(0);
        (await store.CountDocumentAsync(_tenant, neighbour)).ShouldBe(1);
    }

    [Fact]
    public async Task The_collection_is_created_on_first_write_with_cosine_distance_and_tenant_indexes()
    {
        await using var host = NewHost();
        await host.GetRequiredService<IVectorStore>().UpsertAsync([Vector(Guid.NewGuid(), 0, dimensions: 6)]);

        var info = await host.GetRequiredService<QdrantClient>().GetCollectionInfoAsync(_collection);

        info.Config.Params.VectorsConfig.Params.Size.ShouldBe(6UL);
        info.Config.Params.VectorsConfig.Params.Distance.ShouldBe(Distance.Cosine);
        info.PayloadSchema.Keys.ShouldBe(["document_id", "tenant_id"], ignoreOrder: true);
        info.PayloadSchema["tenant_id"].Params.KeywordIndexParams.IsTenant.ShouldBeTrue();
    }

    [Fact]
    public async Task Vectors_of_another_size_are_refused_rather_than_mixed_into_the_collection()
    {
        await using (var first = NewHost())
        {
            await first.GetRequiredService<IVectorStore>().UpsertAsync([Vector(Guid.NewGuid(), 0, dimensions: 4)]);
        }

        // A later process with a different embedding model, same collection.
        await using var second = NewHost();
        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => second.GetRequiredService<IVectorStore>().UpsertAsync([Vector(Guid.NewGuid(), 0, dimensions: 5)]));

        thrown.Message.ShouldContain("re-index");
    }

    [Fact]
    public async Task Deleting_and_counting_before_anything_was_written_are_not_errors()
    {
        await using var host = NewHost();
        var store = host.GetRequiredService<IVectorStore>();

        await store.DeleteDocumentAsync(_tenant, Guid.NewGuid());
        (await store.CountDocumentAsync(_tenant, Guid.NewGuid())).ShouldBe(0);
    }
}
