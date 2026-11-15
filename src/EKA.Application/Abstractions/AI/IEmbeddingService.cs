using EKA.Application.Common;
using EKA.Domain.Common;

namespace EKA.Application.Abstractions.AI;

/// <summary>
/// Turns text into vectors with the configured embedding model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Batch-shaped on purpose.</b> Step 8 embeds documents in batches, and every provider
/// prices and rate-limits per request as well as per token. A single-string method would
/// invite a loop of one-input calls; this signature makes the efficient call the obvious one.
/// </para>
/// <para>
/// <b>The dimension guarantee.</b> Every returned vector has exactly
/// <see cref="Dimensions"/> components, or the call fails with
/// <see cref="AiErrors.DimensionMismatch"/>. A vector of the wrong length cannot be compared
/// with the ones already stored; writing it to Qdrant would either be rejected there or —
/// worse — land in a collection where similarity scores quietly mean nothing.
/// </para>
/// <para>Same failure contract as <see cref="IChatCompletionService"/>.</para>
/// </remarks>
public interface IEmbeddingService
{
    /// <summary>The vector length this service is configured to produce.</summary>
    int Dimensions { get; }

    /// <param name="inputs">One or more texts. An empty list is a programming error and
    /// throws <see cref="ArgumentException"/>, not a <see cref="Result"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<EmbeddingBatch>> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default);
}

/// <summary>Vectors in the same order as the inputs that produced them.</summary>
/// <param name="Vectors">One per input, index-aligned.</param>
/// <param name="Model">The model the provider reports having used.</param>
/// <param name="InputTokens">Tokens billed, or null if unreported.</param>
public sealed record EmbeddingBatch(
    IReadOnlyList<ReadOnlyMemory<float>> Vectors,
    string? Model,
    TokenCount? InputTokens);
