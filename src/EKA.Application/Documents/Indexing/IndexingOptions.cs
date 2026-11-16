using Microsoft.Extensions.Options;

namespace EKA.Application.Documents.Indexing;

/// <summary>
/// The <c>Indexing</c> configuration section: how text becomes chunks and chunks become
/// vectors. Read by the worker only.
/// </summary>
public sealed class IndexingOptions
{
    public const string SectionName = "Indexing";

    /// <summary>
    /// The size a chunk aims for, in estimated tokens. About 500 is the usual starting point:
    /// large enough to hold a complete thought (a policy clause with its conditions), small
    /// enough that one vector still means one thing and several chunks fit in a prompt.
    /// Step 11's evaluation is where this number gets tuned with evidence.
    /// </summary>
    public int ChunkTargetTokens { get; set; } = 500;

    /// <summary>
    /// How much of the end of one chunk is repeated at the start of the next, in estimated
    /// tokens. About 15% of the target. Without overlap, a sentence that answers a question
    /// can be cut in half at a window boundary and match neither half well.
    /// </summary>
    public int ChunkOverlapTokens { get; set; } = 75;

    /// <summary>
    /// Texts per embedding request. Providers price per token but rate-limit per request, so
    /// batching cuts request count by this factor. 64 inputs of about 500 tokens stays far
    /// below the per-request limits of current embedding APIs.
    /// </summary>
    public int EmbeddingBatchSize { get; set; } = 64;

    /// <summary>
    /// Embedding requests in flight at once, per document. This is the backpressure: the
    /// worker never has more than this many batches waiting on the provider, however large
    /// the document. Two keeps a large document moving without tripping rate limits that are
    /// shared with every other job and with the API's question embedding.
    /// </summary>
    public int MaxConcurrentBatches { get; set; } = 2;
}

internal sealed class IndexingOptionsValidator : IValidateOptions<IndexingOptions>
{
    public ValidateOptionsResult Validate(string? name, IndexingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var section = IndexingOptions.SectionName;

        if (options.ChunkTargetTokens is < 50 or > 4000)
        {
            failures.Add($"{section}:ChunkTargetTokens must be between 50 and 4000.");
        }

        // At most half the target: with more, each window advances by less than half its own
        // size, and the index fills with near-duplicates.
        if (options.ChunkOverlapTokens < 0 || options.ChunkOverlapTokens > options.ChunkTargetTokens / 2)
        {
            failures.Add($"{section}:ChunkOverlapTokens must be between 0 and half of ChunkTargetTokens.");
        }

        if (options.EmbeddingBatchSize is < 1 or > 256)
        {
            failures.Add($"{section}:EmbeddingBatchSize must be between 1 and 256.");
        }

        if (options.MaxConcurrentBatches is < 1 or > 16)
        {
            failures.Add($"{section}:MaxConcurrentBatches must be between 1 and 16.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
