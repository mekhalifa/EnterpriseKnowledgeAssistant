using EKA.Application.Abstractions.AI;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Abstractions.Retrieval;
using EKA.Application.Common;
using EKA.Application.Documents.Indexing;
using EKA.Application.Usage;
using EKA.Domain.Access;
using EKA.Domain.Retrieval;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EKA.Application.Retrieval;

/// <summary>One retrieved chunk: its text and provenance, and how well it matched.</summary>
public sealed record RetrievedChunk(ChunkPassage Passage, SimilarityScore Score);

/// <summary>
/// Finds the chunks a caller may read that best match a question: embed, search with the
/// access filter inside the query, re-check against SQL, rank.
/// </summary>
/// <remarks>
/// <para>
/// <b>A service, not only a handler,</b> because two use cases need it: the search endpoint
/// (this step) and answer generation (Step 10). Both must retrieve in exactly the same way, or
/// the evaluation of one says nothing about the other.
/// </para>
/// <para>
/// <b>The flow, and what each stage guarantees:</b>
/// </para>
/// <list type="number">
/// <item><description><b>Embed</b> the question with the configured model. A provider failure is
/// returned as its <c>ai.*</c> error (502), never as "no results".</description></item>
/// <item><description><b>Search</b> with <see cref="ChunkFilter.For"/>: tenant, access keys and
/// embedding model, applied <i>inside</i> the nearest-neighbour search. Over-fetches by
/// <see cref="RetrievalOptions.CandidateMultiplier"/>.</description></item>
/// <item><description><b>Re-check</b> every hit against SQL Server (<see cref="IChunkReader"/>):
/// current access, same tenant, Completed. A hit that fails is dropped, never returned.</description></item>
/// <item><description><b>Rank</b> by score, then document id, then chunk index, and keep K. Ties
/// are broken by identity so two identical requests always return the same list, which
/// evaluation (Step 11) and caching both depend on.</description></item>
/// </list>
/// <para>
/// <b>"No evidence" is a success with an empty list,</b> not an error. Whether to refuse to
/// answer is Step 10's decision, and it needs to tell "nothing relevant" (empty) apart from
/// "could not search" (an error).
/// </para>
/// </remarks>
public sealed partial class ChunkRetriever(
    IEmbeddingService embeddings,
    IVectorStore vectors,
    IChunkReader chunks,
    TokenBudget budget,
    IOptions<RetrievalOptions> options,
    ILogger<ChunkRetriever> logger)
{
    private readonly RetrievalOptions _options = options.Value;

    /// <summary>The number of chunks a request for <paramref name="requested"/> actually gets.</summary>
    public int EffectiveTopK(int? requested) => Math.Clamp(requested ?? _options.DefaultTopK, 1, _options.MaxTopK);

    public async Task<Result<IReadOnlyList<RetrievedChunk>>> RetrieveAsync(
        AccessScope caller,
        string question,
        int? topK = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (string.IsNullOrWhiteSpace(question))
        {
            return RetrievalErrors.EmptyQuery;
        }

        var wanted = EffectiveTopK(topK);

        string[] input = [EmbeddingInput.ForQuery(question)];
        var embedded = await embeddings.EmbedAsync(input, cancellationToken).ConfigureAwait(false);

        if (embedded.IsFailure)
        {
            return embedded.Error;
        }

        // Paid for, so counted (Step 12). The budget CHECK belongs to the use cases that call
        // this: whether to spend is their decision, recording what was spent is not.
        await budget.RecordEmbeddingAsync(caller.TenantId, embedded.Value, input, cancellationToken).ConfigureAwait(false);

        var hits = await vectors
            .SearchAsync(
                embedded.Value.Vectors[0],
                ChunkFilter.For(caller, embeddings.Model),
                limit: wanted * _options.CandidateMultiplier,
                new SimilarityScore(_options.MinimumScore),
                cancellationToken)
            .ConfigureAwait(false);

        if (hits.Count == 0)
        {
            LogRetrieved(logger, caller.TenantId.Value, 0, 0, 0);
            return Result<IReadOnlyList<RetrievedChunk>>.Success([]);
        }

        var passages = await chunks
            .ReadSearchableAsync(caller, [.. hits.Select(hit => new ChunkKey(hit.DocumentId, hit.ChunkIndex))], cancellationToken)
            .ConfigureAwait(false);

        var byKey = passages.ToDictionary(passage => passage.Key);

        IReadOnlyList<RetrievedChunk> ranked =
        [
            .. hits
                .Where(hit => byKey.ContainsKey(new ChunkKey(hit.DocumentId, hit.ChunkIndex)))
                .Select(hit => new RetrievedChunk(byKey[new ChunkKey(hit.DocumentId, hit.ChunkIndex)], hit.Score))
                .OrderByDescending(chunk => chunk.Score)
                .ThenBy(chunk => chunk.Passage.DocumentId)
                .ThenBy(chunk => chunk.Passage.Index)
                .Take(wanted),
        ];

        // Dropped hits are normal in small numbers (a re-ingestion in progress). Many of them
        // mean the vector store's copy of access or status has drifted from SQL, which is safe
        // for readers but worth an operator's attention. Never log the question: it is user
        // content, and can be as sensitive as the documents.
        LogRetrieved(logger, caller.TenantId.Value, hits.Count, hits.Count - byKey.Count, ranked.Count);

        return Result<IReadOnlyList<RetrievedChunk>>.Success(ranked);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retrieval for tenant {TenantId}: {Hits} vector hits, {Dropped} dropped by the SQL re-check, {Returned} returned")]
    private static partial void LogRetrieved(ILogger logger, Guid tenantId, int hits, int dropped, int returned);
}

/// <summary>Expected retrieval failures. Provider failures arrive as the <c>ai.*</c> errors.</summary>
internal static class RetrievalErrors
{
    public static readonly Error EmptyQuery = Error.Validation(
        "retrieval.empty_query",
        "A search needs a question.");
}
