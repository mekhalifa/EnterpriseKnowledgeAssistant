using EKA.Application.Retrieval;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using EKA.Domain.Retrieval;

namespace EKA.Application.Abstractions.Ingestion;

/// <summary>
/// Where chunk vectors live, so retrieval (Step 9) can find the chunks nearest a question.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owned by the application, implemented by an adapter</b> (Qdrant, ADR-0025), for the same
/// reason as <c>IFileStorage</c>: the use cases decide <i>when</i> vectors are written and
/// removed, and the adapter decides <i>how</i>. Nothing above this interface knows about
/// collections, gRPC, point ids or payload indexes.
/// </para>
/// <para>
/// <b>Idempotent by contract.</b> Writing the same chunk twice leaves one vector, because a
/// point's identity is derived from <c>(DocumentId, ChunkIndex)</c> (ADR-0024). Jobs are
/// delivered at least once, so "write twice" is a normal event, not an edge case.
/// </para>
/// <para>
/// <b>Every method takes the tenant.</b> Like the repositories (ADR-0009), the tenant is part
/// of the operation's identity, applied inside the adapter's filter, so a document id from
/// one tenant can never touch another tenant's vectors.
/// </para>
/// </remarks>
public interface IVectorStore
{
    /// <summary>
    /// Writes the vectors, replacing any already stored for the same chunks. Returns once the
    /// store has durably accepted them, because the caller records progress straight after.
    /// </summary>
    Task UpsertAsync(IReadOnlyList<ChunkVector> vectors, CancellationToken cancellationToken = default);

    /// <summary>Removes every vector of one document. Not an error if there are none.</summary>
    Task DeleteDocumentAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>How many vectors the store holds for one document. For verification and tests.</summary>
    Task<long> CountDocumentAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The chunks nearest <paramref name="query"/> that satisfy <paramref name="filter"/>, best
    /// first, applying the filter <b>inside</b> the search rather than to its results (Step 9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Inside, not after.</b> Filtering the top 10 afterwards returns fewer than 10 whenever
    /// other tenants' chunks are nearer, and at worst nothing, while an attacker learns from the
    /// gap. Filtering inside returns the caller's 10 nearest.
    /// </para>
    /// <para>
    /// Returns identifiers and scores only. The text comes from SQL Server, which re-checks
    /// access (ADR-0025, ADR-0027). An empty or missing collection is an empty result.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The filter does not pin a tenant
    /// (<see cref="ChunkFilter.PinnedTenant"/>). A search across tenants is a bug, refused.</exception>
    Task<IReadOnlyList<VectorHit>> SearchAsync(
        ReadOnlyMemory<float> query,
        ChunkFilter filter,
        int limit,
        SimilarityScore minimumScore,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the access keys stored with every vector of one document. Not an error if there
    /// are none.
    /// </summary>
    Task SetDocumentAccessAsync(
        TenantId tenantId,
        Guid documentId,
        IReadOnlyList<string> accessKeys,
        CancellationToken cancellationToken = default);
}

/// <summary>One search result: which chunk, and how close.</summary>
public sealed record VectorHit(Guid DocumentId, ChunkIndex ChunkIndex, SimilarityScore Score);

/// <summary>
/// One chunk's vector and the metadata retrieval needs to filter and cite it.
/// </summary>
/// <remarks>
/// No chunk text. The vector store holds identifiers and filterable metadata only; the text
/// stays in SQL Server, its single source of truth, and Step 9 reads it from there for the
/// chunks a search returns (ADR-0025).
/// </remarks>
/// <param name="AccessKeys">The document's access keys (<c>tenant</c>, <c>user:…</c>,
/// <c>group:…</c>), copied so the search can filter on them (ADR-0027). A copy: SQL Server
/// stays the authority.</param>
public sealed record ChunkVector(
    TenantId TenantId,
    Guid DocumentId,
    ChunkIndex ChunkIndex,
    ReadOnlyMemory<float> Vector,
    string EmbeddingModel,
    PageRange? Pages,
    string? SectionPath,
    IReadOnlyList<string> AccessKeys);
