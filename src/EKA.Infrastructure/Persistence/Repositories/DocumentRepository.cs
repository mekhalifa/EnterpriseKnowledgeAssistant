using EKA.Application.Abstractions;
using EKA.Application.Common;
using EKA.Domain.Access;
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
        DateTimeOffset olderThan,
        int limit,
        CancellationToken cancellationToken = default) =>
        await _context.Documents
            // ModifiedAt, falling back to CreatedAt: a document Reset to Pending has an old
            // CreatedAt and must not be swept the instant it is reset.
            .Where(document => document.Status == IngestionStatus.Pending
                               && (document.ModifiedAt ?? document.CreatedAt) < olderThan)
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

    public async Task<PagedResult<Document>> ListAsync(
        AccessScope caller,
        IngestionStatus? status,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var tenantId = caller.TenantId;
        var callerKeys = caller.Keys.ToList();
        var subject = caller.SubjectId;
        var isAdministrator = caller.IsAdministrator;

        // Built once and reused for both the count and the page, so the two cannot drift
        // apart as the filter grows. Tenant and visibility are applied before anything else and
        // are not optional. Visibility is the domain rule (Document.IsVisibleTo) written in SQL:
        // the document's keys and the caller's share an entry, or the caller owns it, or
        // administers the tenant. Content (search, answers) is stricter: access list only.
        var query = _context.Documents.Where(document =>
            document.TenantId == tenantId
            && (isAdministrator
                || document.OwnerSubject == subject
                || document.AccessKeys.Any(key => callerKeys.Contains(key))));

        if (status is not null)
        {
            query = query.Where(document => document.Status == status);
        }

        // Counted first, in the same request, so "437 documents" and the rows on the page
        // come from one consistent read of the table.
        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await query
            // Id is the tie-breaker: two documents registered in the same millisecond
            // would otherwise order non-deterministically, and a row can then appear on
            // two consecutive pages or on neither.
            .OrderByDescending(document => document.CreatedAt)
            .ThenBy(document => document.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<Document>(items, totalCount);
    }

    public void Add(Document document) => _context.Documents.Add(document);

    public void Remove(Document document) => _context.Documents.Remove(document);
}
