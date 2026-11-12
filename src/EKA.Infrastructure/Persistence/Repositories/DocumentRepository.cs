using EKA.Application.Abstractions;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace EKA.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IDocumentRepository"/>.
/// </summary>
/// <remarks>
/// Note what every query has in common: the tenant filter is written <b>here</b>, once per
/// method, and no method hands an <c>IQueryable</c> back to the caller. That is the entire
/// reason this class exists rather than injecting <c>EkaDbContext</c> into use cases — a
/// caller cannot forget a filter it was never able to write.
/// <para>
/// No method calls <c>SaveChangesAsync</c>. Writes are staged; the use case commits once
/// through <see cref="IUnitOfWork"/>, which is what lets one transaction span two
/// aggregates.
/// </para>
/// </remarks>
internal sealed class DocumentRepository : IDocumentRepository
{
    private readonly EkaDbContext _context;

    public DocumentRepository(EkaDbContext context)
    {
        _context = context;
    }

    public Task<Document?> GetByIdAsync(
        TenantId tenantId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        _context.Documents
            .FirstOrDefaultAsync(
                document => document.TenantId == tenantId && document.Id == documentId,
                cancellationToken);

    public Task<Document?> GetWithChunksAsync(
        TenantId tenantId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        _context.Documents
            .Include(document => document.Chunks)
            .FirstOrDefaultAsync(
                document => document.TenantId == tenantId && document.Id == documentId,
                cancellationToken);

    public Task<Document?> FindByContentHashAsync(
        TenantId tenantId,
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        // Normalized the same way FileMetadata normalizes it, so a caller passing an
        // uppercase digest still matches. Otherwise the idempotency check silently misses
        // and the document is ingested twice.
        var normalized = contentHash?.Trim().ToLowerInvariant();

        return _context.Documents
            .FirstOrDefaultAsync(
                document => document.TenantId == tenantId
                            && document.File.ContentHash == normalized,
                cancellationToken);
    }

    /// <remarks>
    /// Deliberately NOT tenant-scoped: the ingestion worker processes the queue across all
    /// tenants. This is the one method here without a tenant filter, and it returns work
    /// items rather than content, so it leaks nothing between tenants. Oldest first, so a
    /// steady stream of uploads cannot starve an earlier document.
    /// </remarks>
    public async Task<IReadOnlyList<Document>> GetPendingIngestionAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        await _context.Documents
            .Where(document => document.Status == IngestionStatus.Pending)
            .OrderBy(document => document.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Document>> GetByStatusAsync(
        TenantId tenantId,
        IngestionStatus status,
        CancellationToken cancellationToken = default) =>
        await _context.Documents
            .Where(document => document.TenantId == tenantId && document.Status == status)
            .OrderByDescending(document => document.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void Add(Document document) => _context.Documents.Add(document);

    public void Remove(Document document) => _context.Documents.Remove(document);
}
