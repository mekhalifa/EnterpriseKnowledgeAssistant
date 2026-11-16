using EKA.AI.Configuration;
using EKA.Application.Abstractions.AI;
using EKA.Application.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EKA.AI;

/// <summary>
/// Adapter from <see cref="IEmbeddingService"/> to <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>,
/// plus the runtime half of the dimension guarantee.
/// </summary>
internal sealed partial class EmbeddingService(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IOptions<AiOptions> options,
    ILogger<EmbeddingService> logger) : IEmbeddingService
{
    private readonly int _dimensions = options.Value.EmbeddingDimensions;

    public int Dimensions => _dimensions;

    public string Model { get; } = options.Value.EmbeddingModel;

    public async Task<Result<EmbeddingBatch>> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (inputs.Count == 0)
        {
            throw new ArgumentException("At least one input is required.", nameof(inputs));
        }

        GeneratedEmbeddings<Embedding<float>> embeddings;
        try
        {
            // Dimensions is sent on every call, not only set as a client default: the
            // text-embedding-3 family truncates to the requested size (Matryoshka
            // representation), so asking explicitly is what makes 1536 a contract rather
            // than an assumption about the model's native size.
            embeddings = await generator
                .GenerateAsync(
                    inputs,
                    new EmbeddingGenerationOptions { Dimensions = _dimensions },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (ProviderFailure.TryTranslate(exception, cancellationToken, out var error))
        {
            LogProviderFailure(logger, error.Code, exception);
            return error;
        }

        // Count first: a short response means vectors and inputs are no longer index-aligned,
        // and every chunk after the gap would be stored against the wrong text.
        if (embeddings.Count != inputs.Count)
        {
            LogCountMismatch(logger, inputs.Count, embeddings.Count);
            return AiErrors.EmptyResponse;
        }

        // The runtime half of the dimension guarantee — see AiOptionsValidator for why
        // startup validation alone cannot provide it. A model that ignores the dimensions
        // parameter (some compatible servers do) is caught here, before a single vector of
        // the wrong shape reaches the vector store.
        foreach (var embedding in embeddings)
        {
            if (embedding.Vector.Length != _dimensions)
            {
                LogDimensionMismatch(logger, _dimensions, embedding.Vector.Length, embedding.ModelId);
                return AiErrors.DimensionMismatch;
            }
        }

        return new EmbeddingBatch(
            embeddings.Select(embedding => embedding.Vector).ToList(),
            embeddings.FirstOrDefault()?.ModelId,
            Tokens.From(embeddings.Usage?.InputTokenCount));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding generation failed with {ErrorCode}")]
    private static partial void LogProviderFailure(ILogger logger, string errorCode, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Requested {Expected} embeddings, received {Actual}")]
    private static partial void LogCountMismatch(ILogger logger, int expected, int actual);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Embedding model {Model} returned {Actual} dimensions; configuration requires {Expected}")]
    private static partial void LogDimensionMismatch(ILogger logger, int expected, int actual, string? model);
}
