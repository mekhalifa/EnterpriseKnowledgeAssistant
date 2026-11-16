using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Retrieval;
using EKA.Domain.Access;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using EKA.Domain.Retrieval;
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

    private ChunkVector Vector(
        Guid documentId,
        int index,
        int dimensions = 4,
        TenantId? tenant = null,
        float[]? values = null,
        string[]? access = null,
        string model = "test-model") =>
        new(
            tenant ?? _tenant,
            documentId,
            new ChunkIndex(index),
            values ?? Enumerable.Range(1, dimensions).Select(i => (float)(i + index)).ToArray(),
            model,
            PageRange.SinglePage(index + 1),
            "Policy / Leave",
            access ?? ["tenant"]);

    private static readonly float[] North = [1, 0, 0, 0];
    private static readonly float[] NorthEast = [1, 1, 0, 0];
    private static readonly float[] South = [-1, 0, 0, 0];

    private AccessScope Caller(TenantId? tenant = null, string subject = "alice", params string[] groups) =>
        AccessScope.For(tenant ?? _tenant, subject, groups);

    private static Task<IReadOnlyList<VectorHit>> Search(IVectorStore store, AccessScope caller, float[]? query = null, double minimum = 0, string model = "test-model") =>
        store.SearchAsync(query ?? North, ChunkFilter.For(caller, model), limit: 10, new SimilarityScore(minimum));

    // ── Search (Step 9) ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Identical_vectors_in_another_tenant_are_never_returned()
    {
        // The strongest possible lure: tenant B's point is exactly the query vector, so it
        // would be the top hit of an unfiltered search. It must not appear at all.
        await using var host = NewHost();
        var store = host.GetRequiredService<IVectorStore>();
        var other = new TenantId(Guid.NewGuid());
        var mine = Guid.NewGuid();
        await store.UpsertAsync([Vector(mine, 0, values: NorthEast), Vector(Guid.NewGuid(), 0, tenant: other, values: North)]);

        var hits = await Search(store, Caller());

        hits.ShouldHaveSingleItem().DocumentId.ShouldBe(mine);
    }

    [Fact]
    public async Task A_restricted_chunk_is_found_only_by_callers_holding_a_granted_principal()
    {
        await using var host = NewHost();
        var store = host.GetRequiredService<IVectorStore>();
        var hrOnly = Guid.NewGuid();
        await store.UpsertAsync([Vector(hrOnly, 0, values: North, access: ["group:hr"])]);

        (await Search(store, Caller(subject: "bob"))).ShouldBeEmpty();
        (await Search(store, Caller(subject: "carol", groups: "hr"))).ShouldHaveSingleItem().DocumentId.ShouldBe(hrOnly);

        // Same group id, other tenant: group ids are only unique inside one identity provider.
        (await Search(store, Caller(new TenantId(Guid.NewGuid()), "carol", "hr"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_grant_to_one_user_admits_that_user_only()
    {
        await using var host = NewHost();
        var store = host.GetRequiredService<IVectorStore>();
        await store.UpsertAsync([Vector(Guid.NewGuid(), 0, values: North, access: ["user:alice"])]);

        (await Search(store, Caller(subject: "alice"))).Count.ShouldBe(1);
        (await Search(store, Caller(subject: "Alice"))).ShouldBeEmpty(); // subjects are case-sensitive
    }

    [Fact]
    public async Task Changing_access_moves_every_chunk_of_the_document_and_no_other()
    {
        await using var host = NewHost();
        var store = host.GetRequiredService<IVectorStore>();
        var document = Guid.NewGuid();
        var neighbour = Guid.NewGuid();
        await store.UpsertAsync([Vector(document, 0, values: North), Vector(document, 1, values: NorthEast), Vector(neighbour, 0, values: North)]);

        await store.SetDocumentAccessAsync(_tenant, document, ["group:finance"]);

        (await Search(store, Caller())).ShouldHaveSingleItem().DocumentId.ShouldBe(neighbour);
        (await Search(store, Caller(groups: "finance"))).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Changing_access_needs_the_right_tenant()
    {
        await using var host = NewHost();
        var store = host.GetRequiredService<IVectorStore>();
        var document = Guid.NewGuid();
        await store.UpsertAsync([Vector(document, 0, values: North)]);

        await store.SetDocumentAccessAsync(new TenantId(Guid.NewGuid()), document, ["user:mallory"]);

        (await Search(store, Caller())).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Vectors_from_another_embedding_model_are_not_compared_with_the_query()
    {
        await using var host = NewHost();
        var store = host.GetRequiredService<IVectorStore>();
        await store.UpsertAsync([Vector(Guid.NewGuid(), 0, values: North, model: "old-model")]);

        (await Search(store, Caller(), model: "test-model")).ShouldBeEmpty();
        (await Search(store, Caller(), model: "old-model")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Scores_are_normalized_ranked_best_first_and_cut_at_the_threshold()
    {
        await using var host = NewHost();
        var store = host.GetRequiredService<IVectorStore>();
        var same = Guid.NewGuid();
        var close = Guid.NewGuid();
        var opposite = Guid.NewGuid();
        await store.UpsertAsync([Vector(opposite, 0, values: South), Vector(close, 0, values: NorthEast), Vector(same, 0, values: North)]);

        var all = await Search(store, Caller());
        all.Select(hit => hit.DocumentId).ShouldBe([same, close, opposite]);
        all[0].Score.Value.ShouldBe(1.0, tolerance: 1e-4);                         // cosine 1
        all[1].Score.Value.ShouldBe((Math.Sqrt(0.5) + 1) / 2, tolerance: 1e-4);   // cosine 0.707
        all[2].Score.Value.ShouldBe(0.0, tolerance: 1e-4);                         // cosine -1

        // 0.6 normalized = cosine 0.2: the opposite vector is cut by Qdrant, not by us.
        (await Search(store, Caller(), minimum: 0.6)).Select(hit => hit.DocumentId).ShouldBe([same, close]);
    }

    [Fact]
    public async Task Hits_carry_the_chunk_identity_the_SQL_re_check_needs()
    {
        await using var host = NewHost();
        var store = host.GetRequiredService<IVectorStore>();
        var document = Guid.NewGuid();
        await store.UpsertAsync([Vector(document, 7, values: North)]);

        var hit = (await Search(store, Caller())).ShouldHaveSingleItem();

        hit.DocumentId.ShouldBe(document);
        hit.ChunkIndex.ShouldBe(new ChunkIndex(7));
    }

    [Fact]
    public async Task Searching_before_anything_was_written_returns_nothing()
    {
        await using var host = NewHost();

        (await Search(host.GetRequiredService<IVectorStore>(), Caller())).ShouldBeEmpty();
        await host.GetRequiredService<IVectorStore>().SetDocumentAccessAsync(_tenant, Guid.NewGuid(), ["tenant"]);
    }

    [Fact]
    public async Task A_search_without_a_tenant_is_refused_before_it_reaches_Qdrant()
    {
        await using var host = NewHost();

        await Should.ThrowAsync<ArgumentException>(() => host.GetRequiredService<IVectorStore>()
            .SearchAsync(North, ChunkFilter.EmbeddedWith("test-model"), 10, SimilarityScore.None));
    }

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
        info.PayloadSchema.Keys.ShouldBe(["access", "document_id", "embedding_model", "tenant_id"], ignoreOrder: true);
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
