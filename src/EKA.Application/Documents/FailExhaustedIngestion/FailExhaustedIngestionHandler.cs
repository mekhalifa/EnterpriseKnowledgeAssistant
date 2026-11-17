using EKA.Application.Abstractions;
using EKA.Application.Abstractions.Ingestion;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;
using Microsoft.Extensions.Logging;

namespace EKA.Application.Documents.FailExhaustedIngestion;

/// <param name="Reason">Why the last attempt failed, as the job runner saw it. Stored on the
/// document, so it must not carry anything a reader of the document should not see.</param>
public sealed record FailExhaustedIngestionCommand(TenantId TenantId, Guid DocumentId, string Reason);

/// <summary>
/// Marks a document Failed after its ingestion job has used up every retry (Step 14).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> Retries cover transient failures: a provider outage, Qdrant
/// restarting. When every attempt fails the same way, the job lands in the runner's failed list
/// and, until now, the document stayed in <c>Extracting</c>, <c>Chunking</c> or <c>Embedding</c>
/// for ever: shown as "in progress" to its owner, never finished, and impossible to reset
/// because <see cref="Document.Reset"/> only accepts a failed document. Now it fails, says why,
/// and the owner can reset it once the cause is fixed.
/// </para>
/// <para>
/// <b>No status pre-check</b> (ADR-0011): <see cref="Document.Fail"/> decides. The only case it
/// refuses is a completed document, which a late, stale failure must not overwrite: that is
/// caught and logged, not rethrown, because the outcome is already correct. A document that is
/// already Failed records a second failure, which is true: the attempt failed.
/// </para>
/// <para>
/// Vectors are deleted first, as in <c>IndexDocumentHandler</c>: a failed document must leave
/// nothing a search could return. If that delete throws, the caller retries the whole command.
/// </para>
/// </remarks>
public sealed partial class FailExhaustedIngestionHandler(
    IDocumentRepository documents,
    IVectorStore vectors,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<FailExhaustedIngestionHandler> logger)
{
    /// <summary>The longest reason stored. Exception messages can be long and are not for users.</summary>
    public const int MaxReasonLength = 500;

    /// <returns>True when the document is now Failed; false when it was deleted or already completed.</returns>
    public async Task<bool> HandleAsync(FailExhaustedIngestionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var document = await documents
            .GetByIdAsync(command.TenantId, command.DocumentId, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            // Deleted while its job was retrying. Nothing is left to mark.
            return false;
        }

        if (document.Status is IngestionStatus.Completed)
        {
            LogAlreadyCompleted(logger, command.DocumentId);
            return false;
        }

        await vectors.DeleteDocumentAsync(document.TenantId, document.Id, cancellationToken).ConfigureAwait(false);

        document.Fail(Describe(command.Reason), timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogMarkedFailed(logger, command.DocumentId);
        return true;
    }

    private static string Describe(string reason)
    {
        var detail = string.IsNullOrWhiteSpace(reason) ? "unknown error" : reason.Trim();
        var text = $"Ingestion gave up after repeated failures: {detail}";
        return text.Length <= MaxReasonLength ? text : text[..MaxReasonLength];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} marked Failed after its ingestion retries were exhausted")]
    private static partial void LogMarkedFailed(ILogger logger, Guid documentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingestion of document {DocumentId} was reported exhausted, but it is already completed; nothing changed")]
    private static partial void LogAlreadyCompleted(ILogger logger, Guid documentId);
}
