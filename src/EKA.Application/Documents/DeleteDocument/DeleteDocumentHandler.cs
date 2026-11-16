using EKA.Application.Abstractions;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Common;
using EKA.Application.Documents.Ingestion;
using Microsoft.Extensions.Logging;

namespace EKA.Application.Documents.DeleteDocument;

/// <summary>
/// Deletes a document: its row and chunks (by cascade), its files, and its vectors.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not idempotent on purpose.</b> Deleting a document that is not there returns 404, not
/// 204. HTTP permits either, and "already gone is fine" is a defensible reading — but a
/// client deleting an id it does not own would then get the same cheerful 204 as a client
/// deleting its own document, and nobody would ever find out. The 404 keeps a wrong-tenant
/// delete visible in logs and in tests.
/// </para>
/// <para>
/// <b>Three stores, and the row goes first.</b> Once the row is gone the document no longer
/// exists for any reader: retrieval resolves every vector it finds against SQL Server
/// (ADR-0025), so a leftover vector points at nothing and is never shown. The reverse order
/// would leave a "Completed" document whose vectors are gone, which is a silent hole in
/// every answer. So the row commits first, and the files and vectors are cleaned up after.
/// </para>
/// <para>
/// <b>Clean-up is best effort, and says so.</b> When it fails, the delete still succeeded:
/// the caller's document is gone. Reporting 500 would invite a retry that 404s. The failure
/// is logged with the ids an operator needs, and the leftovers are the orphans ADR-0021
/// already accepts until a reconciliation job exists.
/// </para>
/// </remarks>
public sealed partial class DeleteDocumentHandler(
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    IFileStorage storage,
    IVectorStore vectors,
    ILogger<DeleteDocumentHandler> logger)
{
    public async Task<Result> HandleAsync(
        DeleteDocumentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var document = await documents
            .GetByIdAsync(command.TenantId, command.DocumentId, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return DocumentErrors.NotFound(command.DocumentId);
        }

        documents.Remove(document);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await vectors.DeleteDocumentAsync(document.TenantId, document.Id, cancellationToken).ConfigureAwait(false);
            await storage.DeleteAsync(document.StorageKey, cancellationToken).ConfigureAwait(false);
            await storage.DeleteAsync(ExtractedTextStore.KeyFor(document), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCleanupFailed(logger, document.Id, document.StorageKey, exception);
        }

        return Result.Success();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} was deleted, but its vectors or files under {StorageKey} could not all be removed")]
    private static partial void LogCleanupFailed(ILogger logger, Guid documentId, string storageKey, Exception exception);
}
