using EKA.Application.Abstractions.AI;
using EKA.Application.Abstractions.Retrieval;
using EKA.Application.Common;
using EKA.Domain.Access;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;

namespace EKA.Application.Tests.Fakes;

/// <summary>
/// <see cref="IChunkReader"/> over in-memory documents, applying the same rules as the SQL
/// query: the domain's access rule, Completed only, exact (document, index) keys.
/// </summary>
internal sealed class FakeChunkReader : IChunkReader
{
    private readonly List<Document> _documents = [];

    public int Calls { get; private set; }

    public FakeChunkReader With(params Document[] documents)
    {
        _documents.AddRange(documents);
        return this;
    }

    public Task<IReadOnlyList<ChunkPassage>> ReadSearchableAsync(AccessScope caller, IReadOnlyCollection<ChunkKey> keys, CancellationToken cancellationToken = default)
    {
        Calls++;
        var wanted = keys.ToHashSet();

        IReadOnlyList<ChunkPassage> passages =
        [
            .. _documents
                .Where(document => document.IsAccessibleTo(caller) && document.Status is IngestionStatus.Completed)
                .SelectMany(document => document.Chunks.Select(chunk => (document, chunk)))
                .Where(pair => wanted.Contains(new ChunkKey(pair.document.Id, pair.chunk.Index)))
                .Select(pair => new ChunkPassage(
                    pair.document.Id, pair.document.Title.Value, pair.chunk.Index, pair.chunk.Content, pair.chunk.PageRange, pair.chunk.SectionPath)),
        ];

        return Task.FromResult(passages);
    }
}

/// <summary>Embeds every question as the same vector, so a test chooses the scores by choosing the chunks' vectors.</summary>
internal sealed class FixedQueryEmbedding(float[] vector, string model = "test-model") : IEmbeddingService
{
    public int Dimensions => vector.Length;

    public string Model => model;

    public int Calls { get; private set; }

    public List<string> Inputs { get; } = [];

    public Error? FailWith { get; set; }

    public Task<Result<EmbeddingBatch>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        Calls++;
        Inputs.AddRange(inputs);

        Result<EmbeddingBatch> result = FailWith is { } error
            ? error
            : new EmbeddingBatch([.. inputs.Select(_ => (ReadOnlyMemory<float>)vector)], model, new TokenCount(1));

        return Task.FromResult(result);
    }
}
