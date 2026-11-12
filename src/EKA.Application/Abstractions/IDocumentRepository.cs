using EKA.Application.Common;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;

namespace EKA.Application.Abstractions;

/// <summary>
/// Collection-like access to the <see cref="Document"/> aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a repository at all, when EF Core's <c>DbSet</c> already is one.</b> That
/// objection is fair, and for many applications injecting <c>DbContext</c> into handlers is
/// the simpler, better answer. The reason to add the layer here is the security rule: this
/// system's worst failure mode is one tenant reading another's documents. Every method
/// below takes a <see cref="TenantId"/> and applies it <i>inside</i> the query, so the
/// filter is part of the method's identity rather than something a developer must remember
/// at each call site. One forgotten <c>.Where()</c> on a raw <c>IQueryable</c> is a data
/// breach.
/// </para>
/// <para>
/// No method returns <c>IQueryable</c>. Leaking one would hand query construction back to
/// the caller and reopen exactly the hole this interface closes.
/// </para>
/// </remarks>
public interface IDocumentRepository
{
    /// <summary>Loads a document without its chunks. Use for metadata and status.</summary>
    Task<Document?> GetByIdAsync(
        TenantId tenantId,
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a document <i>with</i> its chunk collection. Separate from
    /// <see cref="GetByIdAsync"/> because a large document has thousands of chunks and
    /// loading them by accident is a performance incident, not a convenience.
    /// </summary>
    Task<Document?> GetWithChunksAsync(
        TenantId tenantId,
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a document by its file content hash. This is the idempotency check: the same
    /// bytes uploaded twice must not become two indexed documents.
    /// </summary>
    Task<Document?> FindByContentHashAsync(
        TenantId tenantId,
        string contentHash,
        CancellationToken cancellationToken = default);

    /// <summary>Documents awaiting processing, oldest first, for the ingestion worker.</summary>
    Task<IReadOnlyList<Document>> GetPendingIngestionAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Document>> GetByStatusAsync(
        TenantId tenantId,
        IngestionStatus status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of a tenant's documents, newest first, optionally narrowed to a single
    /// ingestion status.
    /// </summary>
    /// <param name="tenantId">The owning tenant. Applied inside the query.</param>
    /// <param name="status">Filter to one status, or null for all of them.</param>
    /// <param name="skip">Rows to skip. Computed by the caller from page and page size.</param>
    /// <param name="take">Maximum rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// Returns the total alongside the page because a paged UI needs both and running two
    /// round trips from the handler would mean the count and the page could disagree about
    /// which rows exist.
    /// </para>
    /// <para>
    /// <see cref="GetByStatusAsync"/> survives alongside this because the ingestion worker
    /// wants every document in a status, not a page of them, and a method named for that
    /// intent is clearer at the call site than <c>ListAsync(tenant, status, 0, int.MaxValue)</c>.
    /// </para>
    /// </remarks>
    Task<PagedResult<Document>> ListAsync(
        TenantId tenantId,
        IngestionStatus? status,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    void Add(Document document);

    void Remove(Document document);
}
