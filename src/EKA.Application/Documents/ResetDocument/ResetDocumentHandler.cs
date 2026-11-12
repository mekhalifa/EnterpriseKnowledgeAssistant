using EKA.Application.Abstractions;
using EKA.Application.Common;
using EKA.Contracts.Documents;
using EKA.Domain.Common;
using EKA.Domain.Ingestion;

namespace EKA.Application.Documents.ResetDocument;

/// <summary>
/// Requeues a failed document for ingestion.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this handler does not check the status first.</b> It would be easy to write
/// <c>if (document.Status is not IngestionStatus.Failed) return Error.Conflict(…)</c> and
/// return a tidier message. That line is a second copy of a rule that already lives in
/// <see cref="Document.Reset"/>, and copies drift: when Step 7 decides that a document
/// stuck in <c>Extracting</c> for an hour may also be reset, the domain changes and this
/// check silently keeps rejecting it.
/// </para>
/// <para>
/// So the domain stays the only authority on which transitions are legal. It throws
/// <see cref="InvalidStateTransitionException"/>, and the API maps that exception to
/// <c>409 Conflict</c> in exactly one place. This is the deliberate exception to the
/// "<c>Result</c>, not exceptions" rule, and the reason is in ADR-0011: the alternative is
/// not "fewer exceptions", it is "the state machine written down twice".
/// </para>
/// <para>
/// It is also the honest status code. An illegal transition here usually means a legitimate
/// request arrived at the wrong moment — two operators clicking retry, or a client acting
/// on a stale document — and 409 tells the caller to re-read and decide, which is precisely
/// what they should do.
/// </para>
/// </remarks>
public sealed class ResetDocumentHandler(
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<DocumentResponse>> HandleAsync(
        ResetDocumentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Loaded WITH chunks, unlike every other handler here. Reset clears the chunk
        // collection to make re-ingestion idempotent, and a collection that was never
        // loaded cannot be cleared — EF would leave the old rows behind and the retry
        // would index the document twice.
        var document = await documents
            .GetWithChunksAsync(command.TenantId, command.DocumentId, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return DocumentErrors.NotFound(command.DocumentId);
        }

        document.Reset(timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return document.ToResponse();
    }
}
