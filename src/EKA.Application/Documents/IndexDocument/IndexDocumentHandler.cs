using System.Runtime.ExceptionServices;
using System.Text.Json;
using EKA.Application.Abstractions;
using EKA.Application.Abstractions.AI;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Common;
using EKA.Application.Documents.Indexing;
using EKA.Application.Documents.Ingestion;
using EKA.Application.Usage;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EKA.Application.Documents.IndexDocument;

public sealed record IndexDocumentCommand(TenantId TenantId, Guid DocumentId);

/// <summary>What one run did. For logs and tests, never shown to a user.</summary>
public enum IndexingOutcome
{
    /// <summary>Every chunk has its vector; the document is Completed and searchable.</summary>
    Indexed = 1,

    /// <summary>Nothing to do: deleted, not yet extracted, finished, or failed earlier.</summary>
    NothingOwed = 2,

    /// <summary>A permanent problem; the document is now Failed with the reason.</summary>
    Failed = 3,
}

/// <summary>
/// The worker's second and third stages: Chunking → Embedding → Completed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resumable at every point, like extraction.</b> The aggregate says which stage is owed
/// (<c>AwaitsChunking</c>, <c>AwaitsEmbedding</c>) and which chunks still lack a vector
/// (<c>ChunksAwaitingEmbedding</c>). A run that dies anywhere leaves a state the next delivery
/// continues from, without redoing finished batches.
/// </para>
/// <para>
/// <b>Three stores, one order.</b> SQL Server holds chunk rows and progress, Qdrant holds
/// vectors, and nothing spans both in a transaction. So the order is chosen to make every
/// crash harmless:
/// </para>
/// <list type="number">
/// <item><description>Before new chunks are saved, the document's old vectors are deleted. A
/// reset document may once have had more chunks than it has now, and deterministic ids only
/// overwrite the ones that still exist.</description></item>
/// <item><description>A batch's vectors are written <i>before</i> its chunks are recorded as
/// embedded. A crash between the two re-embeds that batch on retry and overwrites the same
/// points: wasted tokens, never a chunk marked embedded with no vector.</description></item>
/// <item><description><c>CompleteIngestion</c> comes last, and the domain refuses it while any
/// chunk lacks a vector.</description></item>
/// </list>
/// <para>
/// <b>Backpressure.</b> At most <see cref="IndexingOptions.MaxConcurrentBatches"/> embedding
/// requests are in flight, however many chunks the document has. They run in <i>waves</i>:
/// a wave's batches run concurrently, then the successful ones are recorded and saved
/// before the next wave starts. Waves leave a little throughput unused (a wave waits for its
/// slowest batch), and in exchange the <c>DbContext</c>, which is not thread-safe, is only
/// ever touched between waves, by one thread.
/// </para>
/// </remarks>
public sealed partial class IndexDocumentHandler(
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    ExtractedTextStore extractedText,
    IChunkingStrategy chunker,
    IEmbeddingService embeddings,
    IVectorStore vectors,
    TokenBudget budget,
    IOptions<IndexingOptions> options,
    TimeProvider timeProvider,
    ILogger<IndexDocumentHandler> logger)
{
    private readonly IndexingOptions _options = options.Value;

    public async Task<IndexingOutcome> HandleAsync(IndexDocumentCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // With chunks: the embedding stage needs them, and it needs to see which are done.
        var document = await documents
            .GetWithChunksAsync(command.TenantId, command.DocumentId, cancellationToken)
            .ConfigureAwait(false);

        if (document is null || !(document.AwaitsChunking || document.AwaitsEmbedding))
        {
            LogNothingOwed(logger, command.DocumentId, document?.Status.ToString() ?? "deleted");
            return IndexingOutcome.NothingOwed;
        }

        if (document.AwaitsChunking)
        {
            var problem = await ChunkAsync(document, cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                return await FailAsync(document, problem, cancellationToken).ConfigureAwait(false);
            }
        }

        return await EmbedAsync(document, cancellationToken).ConfigureAwait(false);
    }

    // ── Chunking ───────────────────────────────────────────────────────────────────

    /// <returns>Null on success; otherwise why the document can never be chunked as it is.</returns>
    private async Task<string?> ChunkAsync(Document document, CancellationToken cancellationToken)
    {
        ExtractedTextArtifact artifact;
        try
        {
            artifact = await extractedText.LoadAsync(document, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return "The extracted text is missing from storage; reset the document to extract it again.";
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return "The extracted text is unreadable; reset the document to extract it again.";
        }

        // Both checks are why the artifact carries these fields (ADR-0023). Reading an
        // artifact in a format this build does not know, or one extracted from other bytes,
        // would index the wrong text with full confidence.
        if (artifact.FormatVersion != ExtractedTextStore.CurrentFormatVersion)
        {
            return $"The extracted text has format version {artifact.FormatVersion}, and this version reads "
                + $"{ExtractedTextStore.CurrentFormatVersion}; reset the document to extract it again.";
        }

        if (!string.Equals(artifact.ContentHash, document.File.ContentHash, StringComparison.Ordinal))
        {
            return "The extracted text belongs to a different file; reset the document to extract it again.";
        }

        var drafts = chunker.Chunk(artifact.Sections);
        if (drafts.Count == 0)
        {
            return "The document contains no text to index.";
        }

        // Vectors from an earlier attempt go first (step 1 in the remarks above).
        await vectors.DeleteDocumentAsync(document.TenantId, document.Id, cancellationToken).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        document.AddChunks(
            drafts.Select((draft, i) => new DocumentChunk(
                Guid.CreateVersion7(now),
                document.Id,
                new ChunkIndex(i),
                draft.Content,
                draft.Tokens,
                now,
                draft.Pages,
                draft.SectionPath)),
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogChunked(logger, document.Id, drafts.Count);

        return null;
    }

    // ── Embedding ──────────────────────────────────────────────────────────────────

    private async Task<IndexingOutcome> EmbedAsync(Document document, CancellationToken cancellationToken)
    {
        var batches = document.ChunksAwaitingEmbedding.Chunk(_options.EmbeddingBatchSize).ToList();

        foreach (var wave in batches.Chunk(_options.MaxConcurrentBatches))
        {
            var results = await Task
                .WhenAll(wave.Select(batch => EmbedBatchAsync(document, batch, cancellationToken)))
                .ConfigureAwait(false);

            // Progress first, failures second: whatever succeeded in this wave is kept even
            // when its sibling failed, so the retry starts from here and not from zero.
            var embedded = results.Where(r => r.Succeeded).SelectMany(r => r.Batch.Select(chunk => chunk.Index)).ToList();
            if (embedded.Count > 0)
            {
                document.RecordEmbeddings(embedded, embeddings.Model, timeProvider.GetUtcNow());
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            var failed = Array.Find(results, r => !r.Succeeded);
            if (failed is null)
            {
                continue;
            }

            if (failed.Exception is not null)
            {
                // The vector store (or anything unexpected) failed: transient until proven
                // otherwise. Rethrown with its original stack, for the job engine to retry.
                ExceptionDispatchInfo.Throw(failed.Exception);
            }

            if (IsPermanent(failed.Error!))
            {
                return await FailAsync(document, $"Embedding was refused: {failed.Error!.Message}", cancellationToken).ConfigureAwait(false);
            }

            throw new EmbeddingUnavailableException(failed.Error!);
        }

        document.CompleteIngestion(timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogIndexed(logger, document.Id, document.ChunkCount, embeddings.Model);
        return IndexingOutcome.Indexed;
    }

    /// <summary>
    /// One batch: embed, then store. Never throws (except on cancellation), so one failed
    /// batch cannot hide the outcome of its siblings in the same wave.
    /// </summary>
    private async Task<BatchResult> EmbedBatchAsync(Document document, DocumentChunk[] batch, CancellationToken cancellationToken)
    {
        try
        {
            var inputs = batch.Select(chunk => EmbeddingInput.For(document, chunk)).ToList();

            var result = await embeddings.EmbedAsync(inputs, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return new BatchResult(batch, result.Error, null);
            }

            // Ingestion is recorded but never refused (Step 12, ADR-0032): stopping it would
            // leave documents half indexed. The tenant's next question meets the budget check.
            await budget.RecordEmbeddingAsync(document.TenantId, result.Value, inputs, cancellationToken).ConfigureAwait(false);

            var points = batch
                .Select((chunk, i) => new ChunkVector(
                    document.TenantId,
                    document.Id,
                    chunk.Index,
                    result.Value.Vectors[i],
                    embeddings.Model,
                    chunk.PageRange,
                    chunk.SectionPath,
                    document.AccessKeys))
                .ToList();

            await vectors.UpsertAsync(points, cancellationToken).ConfigureAwait(false);
            return new BatchResult(batch, null, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new BatchResult(batch, null, exception);
        }
    }

    /// <summary>
    /// The provider refused this input, or answers in a shape the index cannot hold. Retrying
    /// gives the same answer. Everything else (timeouts, rate limits, an open circuit, even a
    /// bad key) can heal without touching the document, so it is left to the job's retries.
    /// </summary>
    private static bool IsPermanent(Error error) =>
        error.Code == AiErrors.Rejected.Code || error.Code == AiErrors.DimensionMismatch.Code;

    private async Task<IndexingOutcome> FailAsync(Document document, string reason, CancellationToken cancellationToken)
    {
        // A failed document must not leave vectors behind that a search could return.
        await vectors.DeleteDocumentAsync(document.TenantId, document.Id, cancellationToken).ConfigureAwait(false);

        document.Fail(reason, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogFailed(logger, document.Id, reason);
        return IndexingOutcome.Failed;
    }

    private sealed record BatchResult(DocumentChunk[] Batch, Error? Error, Exception? Exception)
    {
        public bool Succeeded => Error is null && Exception is null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Document {DocumentId}: no indexing owed ({State})")]
    private static partial void LogNothingOwed(ILogger logger, Guid documentId, string state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Document {DocumentId}: cut into {Chunks} chunks")]
    private static partial void LogChunked(ILogger logger, Guid documentId, int chunks);

    [LoggerMessage(Level = LogLevel.Information, Message = "Document {DocumentId}: {Chunks} chunks embedded with {Model}; ingestion complete")]
    private static partial void LogIndexed(ILogger logger, Guid documentId, int chunks, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} cannot be indexed: {Reason}")]
    private static partial void LogFailed(ILogger logger, Guid documentId, string reason);
}

/// <summary>
/// The embedding provider failed in a way that may heal (timeout, rate limit, open circuit).
/// Thrown so the job engine retries; the document keeps its progress meanwhile.
/// </summary>
public sealed class EmbeddingUnavailableException(Error error)
    : Exception($"Embedding is temporarily unavailable: {error.Code}: {error.Message}")
{
    public Error Error { get; } = error;
}
