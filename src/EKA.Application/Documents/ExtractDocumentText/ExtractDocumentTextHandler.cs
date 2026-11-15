using EKA.Application.Abstractions;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Documents.Ingestion;
using EKA.Domain.Common;
using Microsoft.Extensions.Logging;

namespace EKA.Application.Documents.ExtractDocumentText;

public sealed record ExtractDocumentTextCommand(TenantId TenantId, Guid DocumentId);

/// <summary>What one run of the job did. For logs and tests, never shown to a user.</summary>
public enum ExtractionOutcome
{
    /// <summary>Text extracted and saved; the document moved to Chunking.</summary>
    Extracted = 1,

    /// <summary>Nothing to do: deleted, already extracted, or failed earlier. A redelivered
    /// job lands here.</summary>
    NothingOwed = 2,

    /// <summary>The file cannot be read; the document is now Failed with the reason.</summary>
    Unreadable = 3,
}

/// <summary>
/// The worker's first stage: Pending → Extracting → Chunking, with the text saved as an
/// artifact for Step 8.
/// </summary>
/// <remarks>
/// <para>
/// <b>Safe to run any number of times for the same document.</b> Jobs are delivered at least
/// once: the upload enqueues one, the sweep may enqueue another, and the job engine re-runs
/// a job whose worker died. The aggregate decides whether extraction is still owed
/// (<c>AwaitsExtraction</c>), the artifact write replaces the previous one whole, and the
/// status change happens last. A run that dies at any point leaves a state the next run
/// completes.
/// </para>
/// <para>
/// <b>Two kinds of failure, deliberately treated differently:</b>
/// </para>
/// <list type="bullet">
/// <item><description><b>Permanent</b>: <see cref="UnreadableDocumentException"/> (corrupt,
/// encrypted, scanned, unsupported). Recorded on the document with <c>Fail</c>, and the
/// job <i>succeeds</i>. Retrying returns the same answer, so the job engine must not
/// retry it.</description></item>
/// <item><description><b>Transient</b>: anything else (storage unavailable, database blip).
/// Propagates, so the job engine retries with backoff. The document stays Extracting, and
/// <c>StartOrResumeExtraction</c> lets the retry pick it up.</description></item>
/// </list>
/// </remarks>
public sealed partial class ExtractDocumentTextHandler(
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    IFileStorage storage,
    TextExtractionPipeline pipeline,
    ExtractedTextStore extractedText,
    TimeProvider timeProvider,
    ILogger<ExtractDocumentTextHandler> logger)
{
    public async Task<ExtractionOutcome> HandleAsync(
        ExtractDocumentTextCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var document = await documents
            .GetByIdAsync(command.TenantId, command.DocumentId, cancellationToken)
            .ConfigureAwait(false);

        if (document is null || !document.AwaitsExtraction)
        {
            // Deleted while queued, or a duplicate delivery of finished work.
            LogNothingOwed(logger, command.DocumentId, document?.Status.ToString() ?? "deleted");
            return ExtractionOutcome.NothingOwed;
        }

        // Persisted before the slow part, so the status a user sees while the worker is busy
        // is "Extracting", not "Pending".
        document.StartOrResumeExtraction(timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var fileType = FileTypeDetector.FromContentType(document.File.ContentType);

            await using var content = await storage
                .OpenReadAsync(document.StorageKey, cancellationToken)
                .ConfigureAwait(false);

            var extracted = await pipeline.ExtractAsync(fileType, content, cancellationToken).ConfigureAwait(false);
            var extractedAt = timeProvider.GetUtcNow();

            // Artifact first, status second: a Chunking document always has its text. The
            // reverse order would let a crash leave a document Step 8 cannot process.
            await extractedText.SaveAsync(document, extracted, extractedAt, cancellationToken).ConfigureAwait(false);

            document.CompleteExtraction(extractedAt);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            LogExtracted(logger, document.Id, extracted.Sections.Count, extracted.CharacterCount);
            return ExtractionOutcome.Extracted;
        }
        catch (FileNotFoundException)
        {
            // The row says the file exists and storage says it does not. No retry brings it
            // back, so this is permanent, unlike most IOExceptions.
            return await FailAsync(document, "The stored file is missing; upload the document again.", cancellationToken).ConfigureAwait(false);
        }
        catch (UnreadableDocumentException exception)
        {
            return await FailAsync(document, exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ExtractionOutcome> FailAsync(Domain.Ingestion.Document document, string reason, CancellationToken cancellationToken)
    {
        document.Fail(reason, timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogUnreadable(logger, document.Id, reason);
        return ExtractionOutcome.Unreadable;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Document {DocumentId}: no extraction owed ({State})")]
    private static partial void LogNothingOwed(ILogger logger, Guid documentId, string state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Document {DocumentId}: extracted {Sections} sections, {Characters} characters")]
    private static partial void LogExtracted(ILogger logger, Guid documentId, int sections, int characters);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} is unreadable: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, Guid documentId, string reason);
}
