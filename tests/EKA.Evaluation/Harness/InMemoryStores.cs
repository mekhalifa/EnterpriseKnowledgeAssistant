using System.Collections.Concurrent;
using EKA.Application.Abstractions;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Abstractions.Retrieval;
using EKA.Application.Common;
using EKA.Application.Retrieval;
using EKA.Domain.Access;
using EKA.Domain.Common;
using EKA.Domain.Conversations;
using EKA.Domain.Identity;
using EKA.Domain.Ingestion;
using EKA.Domain.Prompting;
using EKA.Domain.Retrieval;

namespace EKA.Evaluation.Harness;

// In-memory implementations of the ports the real use cases need. They exist so the
// evaluation measures retrieval and answering, not SQL Server and Qdrant, which have their own
// integration tests. Each applies the same rules as its production adapter, because an
// evaluation over a store that ignored access would score a system that does not exist.

/// <summary>Documents, with the tenant filter and the domain's access rule.</summary>
internal sealed class InMemoryDocumentRepository : IDocumentRepository
{
    private readonly List<Document> _documents = [];

    public IReadOnlyList<Document> All => _documents;

    public Task<Document?> GetByIdAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_documents.Find(d => d.TenantId == tenantId && d.Id == documentId));

    public Task<Document?> GetWithChunksAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default) =>
        GetByIdAsync(tenantId, documentId, cancellationToken);

    public Task<Document?> FindByContentHashAsync(TenantId tenantId, string contentHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(_documents.Find(d => d.TenantId == tenantId && d.File.ContentHash == contentHash));

    public Task<IReadOnlyList<Document>> GetPendingIngestionAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Document>>([]);

    public Task<IReadOnlyList<Document>> GetByStatusAsync(TenantId tenantId, IngestionStatus status, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Document>>([.. _documents.Where(d => d.TenantId == tenantId && d.Status == status)]);

    public Task<PagedResult<Document>> ListAsync(AccessScope caller, IngestionStatus? status, int skip, int take, CancellationToken cancellationToken = default)
    {
        var matching = _documents.Where(d => d.IsAccessibleTo(caller) && (status is null || d.Status == status)).ToList();
        return Task.FromResult(new PagedResult<Document>([.. matching.Skip(skip).Take(take)], matching.Count));
    }

    public void Add(Document document) => _documents.Add(document);

    public void Remove(Document document) => _documents.Remove(document);
}

/// <summary>The SQL re-check, over the same documents: tenant, current access, Completed, exact keys.</summary>
internal sealed class InMemoryChunkReader(InMemoryDocumentRepository documents) : IChunkReader
{
    public Task<IReadOnlyList<ChunkPassage>> ReadSearchableAsync(AccessScope caller, IReadOnlyCollection<ChunkKey> keys, CancellationToken cancellationToken = default)
    {
        var wanted = keys.ToHashSet();

        IReadOnlyList<ChunkPassage> passages =
        [
            .. documents.All
                .Where(d => d.IsAccessibleTo(caller) && d.Status is IngestionStatus.Completed)
                .SelectMany(d => d.Chunks.Select(c => (Document: d, Chunk: c)))
                .Where(pair => wanted.Contains(new ChunkKey(pair.Document.Id, pair.Chunk.Index)))
                .Select(pair => new ChunkPassage(
                    pair.Document.Id, pair.Document.Title.Value, pair.Chunk.Index, pair.Chunk.Content, pair.Chunk.PageRange, pair.Chunk.SectionPath)),
        ];

        return Task.FromResult(passages);
    }
}

/// <summary>
/// Exact cosine search. The filter is evaluated with the Specification's in-memory
/// interpretation (<see cref="ChunkFilter.IsSatisfiedBy"/>), and a filter without a tenant is
/// refused, as the Qdrant adapter refuses it.
/// </summary>
/// <remarks>
/// Exact, where Qdrant is approximate (HNSW). At this corpus size Qdrant searches exhaustively
/// too, so the two agree. On a large corpus an evaluation should run against Qdrant itself, to
/// measure the approximation as well.
/// </remarks>
internal sealed class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<(Guid DocumentId, int Index), ChunkVector> _points = new();

    public int Count => _points.Count;

    public Task UpsertAsync(IReadOnlyList<ChunkVector> vectors, CancellationToken cancellationToken = default)
    {
        foreach (var vector in vectors)
        {
            _points[(vector.DocumentId, vector.ChunkIndex.Value)] = vector;
        }

        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default)
    {
        foreach (var key in _points.Where(p => p.Key.DocumentId == documentId && p.Value.TenantId == tenantId).Select(p => p.Key).ToList())
        {
            _points.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public Task<long> CountDocumentAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default) =>
        Task.FromResult((long)_points.Values.Count(p => p.DocumentId == documentId && p.TenantId == tenantId));

    public Task<IReadOnlyList<VectorHit>> SearchAsync(ReadOnlyMemory<float> query, ChunkFilter filter, int limit, SimilarityScore minimumScore, CancellationToken cancellationToken = default)
    {
        if (filter.PinnedTenant is null)
        {
            throw new ArgumentException("A vector search must be restricted to one tenant.", nameof(filter));
        }

        IReadOnlyList<VectorHit> hits =
        [
            .. _points.Values
                .Where(filter.IsSatisfiedBy)
                .Select(p => new VectorHit(p.DocumentId, p.ChunkIndex, SimilarityScore.FromCosineSimilarity(Cosine(query.Span, p.Vector.Span))))
                .Where(hit => hit.Score >= minimumScore)
                .OrderByDescending(hit => hit.Score)
                .Take(limit),
        ];

        return Task.FromResult(hits);
    }

    public Task SetDocumentAccessAsync(TenantId tenantId, Guid documentId, IReadOnlyList<string> accessKeys, CancellationToken cancellationToken = default)
    {
        foreach (var (key, point) in _points.Where(p => p.Key.DocumentId == documentId && p.Value.TenantId == tenantId).ToList())
        {
            _points[key] = point with { AccessKeys = [.. accessKeys] };
        }

        return Task.CompletedTask;
    }

    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}

internal sealed class InMemoryFileStorage : IFileStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        _files[key] = buffer.ToArray();
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
        _files.TryGetValue(key, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : throw new FileNotFoundException(key);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _files.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryUserRepository : IUserRepository
{
    private readonly List<User> _users = [];

    public Task<User?> GetByIdAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_users.Find(u => u.TenantId == tenantId && u.Id == userId));

    public Task<User?> FindByExternalIdAsync(TenantId tenantId, string externalId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_users.Find(u => u.TenantId == tenantId && u.ExternalId == externalId));

    public void Add(User user) => _users.Add(user);
}

internal sealed class InMemoryConversationRepository : IConversationRepository
{
    private readonly List<Conversation> _conversations = [];

    public Task<Conversation?> GetByIdAsync(TenantId tenantId, Guid userId, Guid conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Find(tenantId, userId, conversationId));

    public Task<Conversation?> GetWithMessagesAsync(TenantId tenantId, Guid userId, Guid conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Find(tenantId, userId, conversationId));

    public Task<IReadOnlyList<Conversation>> GetActiveForUserAsync(TenantId tenantId, Guid userId, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Conversation>>([.. _conversations.Where(c => c.TenantId == tenantId && c.UserId == userId).Take(limit)]);

    public void Add(Conversation conversation) => _conversations.Add(conversation);

    public void Remove(Conversation conversation) => _conversations.Remove(conversation);

    private Conversation? Find(TenantId tenantId, Guid userId, Guid conversationId) =>
        _conversations.Find(c => c.TenantId == tenantId && c.UserId == userId && c.Id == conversationId);
}

internal sealed class InMemoryPromptTemplateRepository : IPromptTemplateRepository
{
    private readonly List<PromptTemplate> _templates = [];

    public Task<PromptTemplate?> GetActiveAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_templates.Find(t => t.Name == name && t.IsActive));

    public Task<PromptTemplate?> GetVersionAsync(string name, int version, CancellationToken cancellationToken = default) =>
        Task.FromResult(_templates.Find(t => t.Name == name && t.Version == version));

    public Task<IReadOnlyList<PromptTemplate>> GetVersionsAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PromptTemplate>>([.. _templates.Where(t => t.Name == name)]);

    public void Add(PromptTemplate template) => _templates.Add(template);
}

/// <summary>Everything is already "saved": the stores above hold the live objects.</summary>
internal sealed class InMemoryUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
}

internal sealed class InMemoryTokenUsageLedger : Application.Abstractions.Usage.ITokenUsageLedger
{
    public Task AddAsync(TenantId tenantId, Application.Abstractions.Usage.UsagePeriod period, Application.Abstractions.Usage.TokenUsage usage, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<Application.Abstractions.Usage.TokenUsage> GetAsync(TenantId tenantId, Application.Abstractions.Usage.UsagePeriod period, CancellationToken cancellationToken = default) =>
        Task.FromResult(Application.Abstractions.Usage.TokenUsage.None);
}

/// <summary>The evaluation corpus is ours and clean.</summary>
internal sealed class CleanScanner : IMalwareScanner
{
    public Task<ScanVerdict> ScanAsync(Stream content, string fileName, CancellationToken cancellationToken = default) =>
        Task.FromResult(ScanVerdict.Clean);
}

internal sealed class NoIngestionQueue : IIngestionQueue
{
    public Task<bool> EnqueueIngestionAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
