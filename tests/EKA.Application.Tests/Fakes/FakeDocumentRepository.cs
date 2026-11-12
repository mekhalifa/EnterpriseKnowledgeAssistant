using EKA.Application.Abstractions;
using EKA.Application.Common;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;

namespace EKA.Application.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IDocumentRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a hand-written fake and not a mocking library.</b> A mock is configured per test
/// — "when GetByIdAsync is called with this id, return that" — which means each test
/// restates the repository's behaviour, and a test can accidentally describe a repository
/// that could not exist. This fake behaves like the real one once, here, and every test
/// gets that behaviour for free. It also fails loudly when the interface grows, instead of
/// silently returning null for the new method.
/// </para>
/// <para>
/// <b>It enforces the tenant filter, deliberately.</b> A fake that ignored the tenant would
/// make a handler that forgot to pass one pass its tests, and the bug would ship. The
/// filtering below mirrors what the SQL implementation does so the two cannot disagree
/// about the one rule that matters most here.
/// </para>
/// <para>
/// What it does not mirror: unique indexes, cascade deletes and concurrency. Those are
/// database behaviours and are tested against a real SQL Server in
/// <c>EKA.IntegrationTests</c>. A fake pretending to have them would be the worst of both
/// — confident and wrong.
/// </para>
/// </remarks>
internal sealed class FakeDocumentRepository : IDocumentRepository
{
    private readonly List<Document> _documents = [];

    /// <summary>Everything currently stored, for assertions.</summary>
    public IReadOnlyList<Document> All => _documents;

    /// <summary>
    /// Documents staged for removal. Separate from <see cref="All"/> because the real
    /// repository only stages — the delete happens on commit — and a test that asserts
    /// removal before <c>SaveChangesAsync</c> would otherwise pass against a repository
    /// that deleted immediately.
    /// </summary>
    public List<Document> Removed { get; } = [];

    /// <summary>Seeds a document without going through a handler.</summary>
    public FakeDocumentRepository With(Document document)
    {
        _documents.Add(document);
        return this;
    }

    public Task<Document?> GetByIdAsync(
        TenantId tenantId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_documents.Find(
            document => document.TenantId == tenantId && document.Id == documentId));

    public Task<Document?> GetWithChunksAsync(
        TenantId tenantId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        GetByIdAsync(tenantId, documentId, cancellationToken);

    public Task<Document?> FindByContentHashAsync(
        TenantId tenantId,
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        var normalized = contentHash?.Trim().ToLowerInvariant();

        return Task.FromResult(_documents.Find(
            document => document.TenantId == tenantId
                        && document.File.ContentHash == normalized));
    }

    public Task<IReadOnlyList<Document>> GetPendingIngestionAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Document>>(
            [.. _documents
                .Where(document => document.Status == IngestionStatus.Pending)
                .OrderBy(document => document.CreatedAt)
                .Take(limit)]);

    public Task<IReadOnlyList<Document>> GetByStatusAsync(
        TenantId tenantId,
        IngestionStatus status,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Document>>(
            [.. _documents.Where(
                document => document.TenantId == tenantId && document.Status == status)]);

    public Task<PagedResult<Document>> ListAsync(
        TenantId tenantId,
        IngestionStatus? status,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var matching = _documents
            .Where(document => document.TenantId == tenantId)
            .Where(document => status is null || document.Status == status)
            .ToList();

        var page = matching
            .OrderByDescending(document => document.CreatedAt)
            .ThenBy(document => document.Id)
            .Skip(skip)
            .Take(take)
            .ToList();

        return Task.FromResult(new PagedResult<Document>(page, matching.Count));
    }

    public void Add(Document document) => _documents.Add(document);

    public void Remove(Document document)
    {
        _documents.Remove(document);
        Removed.Add(document);
    }
}

/// <summary>
/// Counts commits so a test can assert that a handler saved exactly once.
/// </summary>
/// <remarks>
/// The count is the point. "The handler did the right thing and also committed three times"
/// is a bug this fake catches and a mocking library's loose verification usually does not.
/// </remarks>
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.FromResult(1);
    }
}
