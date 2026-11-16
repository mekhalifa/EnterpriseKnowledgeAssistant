using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EKA.Application.Abstractions.Ingestion;
using EKA.Domain.Common;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using static Qdrant.Client.Grpc.Conditions;

namespace EKA.Infrastructure.VectorStore;

/// <summary>The <c>VectorStore</c> configuration section.</summary>
public sealed class VectorStoreOptions
{
    public const string SectionName = "VectorStore";

    /// <summary>
    /// Qdrant's <b>gRPC</b> endpoint (port 6334), not the REST one (6333). The official client
    /// speaks gRPC only. The default matches deploy/docker-compose.yml, which binds to
    /// 127.0.0.1, so the database is never reachable from the network.
    /// </summary>
    public Uri Endpoint { get; set; } = new("http://127.0.0.1:6334");

    /// <summary>
    /// Empty locally, where the port is bound to loopback. Required for any shared or
    /// hosted Qdrant. A secret, so never in appsettings.json: User Secrets, an environment
    /// variable, or Key Vault, like <c>Ai:ApiKey</c>.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>One collection for every tenant, partitioned by the <c>tenant_id</c> payload (ADR-0025).</summary>
    public string CollectionName { get; set; } = "eka_chunks";
}

/// <summary>
/// <see cref="IVectorStore"/> over Qdrant (ADR-0024, ADR-0025).
/// </summary>
/// <remarks>
/// <para>
/// <b>One collection, partitioned by payload.</b> Every point carries <c>tenant_id</c> and
/// <c>document_id</c>, both with payload indexes; <c>tenant_id</c> is marked as Qdrant's
/// <i>tenant</i> field, which tells it to lay vectors out per tenant so that filtered search
/// stays fast. Every delete and count filters on both. Step 9's search will too, which is
/// what "authorization in retrieval" means at this layer.
/// </para>
/// <para>
/// <b>The collection is created on first write</b>, sized from the vectors themselves. The API
/// never writes vectors (it only deletes them), so it never needs to know the embedding
/// dimension, and it starts even if Qdrant is not running. If the collection already exists
/// with a <i>different</i> size, that is refused loudly: it means the embedding model changed
/// without re-indexing, and every similarity score would quietly become meaningless.
/// </para>
/// </remarks>
internal sealed partial class QdrantVectorStore(
    QdrantClient client,
    IOptions<VectorStoreOptions> options,
    ILogger<QdrantVectorStore> logger) : IVectorStore
{
    internal const string TenantField = "tenant_id";
    internal const string DocumentField = "document_id";

    private readonly string _collection = options.Value.CollectionName;
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private volatile bool _collectionReady;

    public async Task UpsertAsync(IReadOnlyList<ChunkVector> vectors, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        if (vectors.Count == 0)
        {
            return;
        }

        await EnsureCollectionAsync((ulong)vectors[0].Vector.Length, cancellationToken).ConfigureAwait(false);

        var points = vectors.Select(ToPoint).ToList();

        // wait: true — return only once the points are applied, because the caller records the
        // chunks as embedded straight after. Acknowledged-but-not-applied would let a crash
        // mark a chunk embedded whose vector was never written.
        await client.UpsertAsync(_collection, points, wait: true, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDocumentAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.DeleteAsync(_collection, DocumentFilter(tenantId, documentId), wait: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RpcException exception) when (exception.StatusCode is StatusCode.NotFound)
        {
            // No collection yet means no vectors yet. Deleting nothing is not an error.
        }
    }

    public async Task<long> CountDocumentAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default)
    {
        try
        {
            var count = await client.CountAsync(_collection, DocumentFilter(tenantId, documentId), exact: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return (long)count;
        }
        catch (RpcException exception) when (exception.StatusCode is StatusCode.NotFound)
        {
            return 0;
        }
    }

    /// <summary>
    /// The point id for a chunk: a UUID derived from <c>(DocumentId, ChunkIndex)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why derived and not the chunk row's id</b> (ADR-0024). Re-chunking a reset document
    /// creates new chunk rows with new ids, but chunk 7 of document D is still chunk 7 of
    /// document D. A derived id makes every rewrite of it an overwrite: a retried batch, a
    /// redelivered job, a re-ingestion. A random id would make each of those a duplicate,
    /// and a search would return the same passage twice.
    /// </para>
    /// <para>
    /// A version 8 UUID (RFC 9562, "custom"), from the first 16 bytes of a SHA-256. Version 5
    /// is the name-based standard, but it is defined on SHA-1, which analyzers flag and
    /// reviewers have to stop and justify. Collisions are not a concern at 122 random bits.
    /// </para>
    /// </remarks>
    internal static Guid PointIdFor(Guid documentId, int chunkIndex)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"eka-chunk:{documentId:N}:{chunkIndex}");

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(name), hash);

        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant

        return new Guid(bytes, bigEndian: true);
    }

    private static PointStruct ToPoint(ChunkVector vector)
    {
        var point = new PointStruct
        {
            Id = PointIdFor(vector.DocumentId, vector.ChunkIndex.Value),
            Vectors = vector.Vector.ToArray(),
            Payload =
            {
                [TenantField] = vector.TenantId.Value.ToString(),
                [DocumentField] = vector.DocumentId.ToString(),
                ["chunk_index"] = vector.ChunkIndex.Value,
                ["embedding_model"] = vector.EmbeddingModel,
            },
        };

        if (vector.Pages is { } pages)
        {
            point.Payload["page_start"] = pages.Start;
            point.Payload["page_end"] = pages.End;
        }

        if (vector.SectionPath is { } section)
        {
            point.Payload["section_path"] = section;
        }

        return point;
    }

    /// <summary>Tenant AND document, always both: a document id alone must never be enough.</summary>
    private static Filter DocumentFilter(TenantId tenantId, Guid documentId) =>
        MatchKeyword(TenantField, tenantId.Value.ToString()) & MatchKeyword(DocumentField, documentId.ToString());

    private async Task EnsureCollectionAsync(ulong dimensions, CancellationToken cancellationToken)
    {
        if (_collectionReady)
        {
            return;
        }

        await _ensureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_collectionReady)
            {
                return;
            }

            if (!await client.CollectionExistsAsync(_collection, cancellationToken).ConfigureAwait(false))
            {
                await CreateCollectionAsync(dimensions, cancellationToken).ConfigureAwait(false);
            }

            var info = await client.GetCollectionInfoAsync(_collection, cancellationToken).ConfigureAwait(false);
            var existing = info.Config.Params.VectorsConfig.Params.Size;
            if (existing != dimensions)
            {
                throw new InvalidOperationException(
                    $"Qdrant collection '{_collection}' holds {existing}-dimension vectors, and the embedding model "
                    + $"produces {dimensions}. The model changed without re-indexing; use a new collection name "
                    + "and re-ingest, or restore the previous model.");
            }

            await EnsurePayloadIndexesAsync(cancellationToken).ConfigureAwait(false);
            _collectionReady = true;
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    private async Task CreateCollectionAsync(ulong dimensions, CancellationToken cancellationToken)
    {
        try
        {
            // Cosine: OpenAI-style embeddings are normalized, and cosine is the distance they
            // are trained and documented for.
            await client.CreateCollectionAsync(
                _collection,
                new VectorParams { Size = dimensions, Distance = Distance.Cosine },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException exception) when (exception.StatusCode is StatusCode.AlreadyExists)
        {
            // Another worker created it between our check and our create. Same outcome.
            return;
        }


        LogCollectionCreated(logger, _collection, dimensions);
    }

    /// <summary>
    /// Run on every first check, not only after creating the collection: a process that died
    /// between "create collection" and "create indexes" would otherwise leave the tenant
    /// filter unindexed for good. Qdrant accepts re-creating an existing index.
    /// </summary>
    private async Task EnsurePayloadIndexesAsync(CancellationToken cancellationToken)
    {
        await client.CreatePayloadIndexAsync(
            _collection,
            TenantField,
            PayloadSchemaType.Keyword,
            new PayloadIndexParams { KeywordIndexParams = new KeywordIndexParams { IsTenant = true } },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await client.CreatePayloadIndexAsync(
            _collection,
            DocumentField,
            PayloadSchemaType.Keyword,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created Qdrant collection {Collection} with {Dimensions} dimensions, cosine distance")]
    private static partial void LogCollectionCreated(ILogger logger, string collection, ulong dimensions);
}
