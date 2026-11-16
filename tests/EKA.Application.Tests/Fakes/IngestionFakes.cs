using EKA.Application.Abstractions.AI;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Common;
using EKA.Application.Retrieval;
using EKA.Domain.Common;
using EKA.Domain.Retrieval;

namespace EKA.Application.Tests.Fakes;

/// <summary>In-memory storage with the real adapter's atomic-write contract: a save that throws
/// stores nothing.</summary>
internal sealed class FakeFileStorage : IFileStorage
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        _files[key] = buffer.ToArray();
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
        _files.TryGetValue(key, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : throw new FileNotFoundException(key);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _files.Remove(key);
        return Task.CompletedTask;
    }

    public void Put(string key, byte[] bytes) => _files[key] = bytes;
}

internal sealed class FakeIngestionQueue : IIngestionQueue
{
    public List<(TenantId Tenant, Guid DocumentId)> Enqueued { get; } = [];

    /// <summary>Simulates the job store being unreachable.</summary>
    public bool Fail { get; set; }

    public Task<bool> EnqueueIngestionAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            return Task.FromResult(false);
        }

        Enqueued.Add((tenantId, documentId));
        return Task.FromResult(true);
    }
}

/// <summary>Returns a set verdict, or throws as an unreachable scanner would.</summary>
internal sealed class FakeMalwareScanner : IMalwareScanner
{
    public ScanVerdict Verdict { get; set; } = ScanVerdict.Clean;

    public bool Fail { get; set; }

    public Task<ScanVerdict> ScanAsync(Stream content, string fileName, CancellationToken cancellationToken = default) =>
        Fail ? throw new IOException("scanner unreachable") : Task.FromResult(Verdict);
}

/// <summary>Claims one type and returns a fixed result, or throws a given exception.</summary>
internal sealed class FakeExtractor(DocumentFileType handles, Func<ExtractedDocument> result) : ITextExtractor
{
    public int Calls { get; private set; }

    public bool CanExtract(DocumentFileType fileType) => fileType == handles;

    public Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(result());
    }

    public static FakeExtractor Returning(DocumentFileType handles, params string[] sectionTexts) =>
        new(handles, () => new ExtractedDocument([.. sectionTexts.Select((text, i) => new ExtractedSection(i + 1, null, text))]));

    public static FakeExtractor Throwing(DocumentFileType handles, Exception exception) =>
        new(handles, () => throw exception);
}

/// <summary>
/// An in-memory vector store keyed exactly as the real one: by (document, chunk index), so a
/// rewrite overwrites. Thread-safe, because the handler writes batches concurrently.
/// </summary>
internal sealed class FakeVectorStore : IVectorStore
{
    private readonly Dictionary<(Guid DocumentId, int Index), ChunkVector> _points = [];
    private readonly Lock _gate = new();

    public int UpsertCalls { get; private set; }

    public int DeleteCalls { get; private set; }

    /// <summary>When set, the given upsert call (1-based) throws, as an unreachable store would.</summary>
    public int? FailOnUpsertCall { get; set; }

    public IReadOnlyList<ChunkVector> Points
    {
        get
        {
            lock (_gate)
            {
                return [.. _points.Values.OrderBy(p => p.DocumentId).ThenBy(p => p.ChunkIndex.Value)];
            }
        }
    }

    public Task UpsertAsync(IReadOnlyList<ChunkVector> vectors, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            UpsertCalls++;
            if (UpsertCalls == FailOnUpsertCall)
            {
                throw new IOException("vector store unreachable");
            }

            foreach (var vector in vectors)
            {
                _points[(vector.DocumentId, vector.ChunkIndex.Value)] = vector;
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            DeleteCalls++;
            foreach (var key in _points.Where(p => p.Key.DocumentId == documentId && p.Value.TenantId == tenantId).Select(p => p.Key).ToList())
            {
                _points.Remove(key);
            }
        }

        return Task.CompletedTask;
    }

    public Task<long> CountDocumentAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult((long)_points.Values.Count(p => p.DocumentId == documentId && p.TenantId == tenantId));
        }
    }

    /// <summary>The filters searches were given, for assertions about what was asked.</summary>
    public List<ChunkFilter> SearchFilters { get; } = [];

    /// <summary>
    /// Exact cosine search with the filter evaluated in memory (<see cref="ChunkFilter.IsSatisfiedBy"/>),
    /// and the real adapter's refusal of a filter without a tenant.
    /// </summary>
    public Task<IReadOnlyList<VectorHit>> SearchAsync(
        ReadOnlyMemory<float> query,
        ChunkFilter filter,
        int limit,
        SimilarityScore minimumScore,
        CancellationToken cancellationToken = default)
    {
        if (filter.PinnedTenant is null)
        {
            throw new ArgumentException("A vector search must be restricted to one tenant.", nameof(filter));
        }

        lock (_gate)
        {
            SearchFilters.Add(filter);

            IReadOnlyList<VectorHit> hits =
            [
                .. _points.Values
                    .Where(filter.IsSatisfiedBy)
                    .Select(point => new VectorHit(point.DocumentId, point.ChunkIndex, SimilarityScore.FromCosineSimilarity(Cosine(query.Span, point.Vector.Span))))
                    .Where(hit => hit.Score >= minimumScore)
                    .OrderByDescending(hit => hit.Score)
                    .Take(limit),
            ];

            return Task.FromResult(hits);
        }
    }

    public Task SetDocumentAccessAsync(TenantId tenantId, Guid documentId, IReadOnlyList<string> accessKeys, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (FailAccessUpdate)
            {
                throw new IOException("vector store unreachable");
            }

            foreach (var (key, point) in _points.Where(p => p.Key.DocumentId == documentId && p.Value.TenantId == tenantId).ToList())
            {
                _points[key] = point with { AccessKeys = [.. accessKeys] };
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>When set, <see cref="SetDocumentAccessAsync"/> throws, as an unreachable store would.</summary>
    public bool FailAccessUpdate { get; set; }

    /// <summary>Puts a point in directly, as an earlier run would have left it.</summary>
    public void Seed(ChunkVector vector)
    {
        lock (_gate)
        {
            _points[(vector.DocumentId, vector.ChunkIndex.Value)] = vector;
        }
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

/// <summary>
/// Returns small deterministic vectors and records every batch. Can fail a chosen call, and
/// tracks how many calls were in flight at once, which is what the backpressure test reads.
/// </summary>
internal sealed class FakeEmbeddingService(int dimensions = 4) : IEmbeddingService
{
    private readonly Lock _gate = new();
    private readonly List<IReadOnlyList<string>> _batches = [];
    private int _inFlight;

    public int Dimensions => dimensions;

    public string Model => "fake-embedding-model";

    public int MaxInFlight { get; private set; }

    /// <summary>When set, the given call (1-based) fails with this error.</summary>
    public (int Call, Error Error)? FailOnCall { get; set; }

    /// <summary>How long each call takes, so concurrent calls actually overlap.</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(20);

    public IReadOnlyList<IReadOnlyList<string>> Batches
    {
        get
        {
            lock (_gate)
            {
                return [.. _batches];
            }
        }
    }

    public async Task<Result<EmbeddingBatch>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        int call;
        lock (_gate)
        {
            _batches.Add(inputs);
            call = _batches.Count;
            _inFlight++;
            MaxInFlight = Math.Max(MaxInFlight, _inFlight);
        }

        try
        {
            await Task.Delay(Latency, cancellationToken);

            if (FailOnCall is { } failure && failure.Call == call)
            {
                return failure.Error;
            }

            var vectors = inputs
                .Select(text => (ReadOnlyMemory<float>)Enumerable.Range(0, dimensions).Select(i => (float)(text.Length + i)).ToArray())
                .ToList();

            return new EmbeddingBatch(vectors, Model, new TokenCount(inputs.Count * 10));
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
            }
        }
    }
}
