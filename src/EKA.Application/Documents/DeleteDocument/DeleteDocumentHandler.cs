using EKA.Application.Abstractions;
using EKA.Application.Common;

namespace EKA.Application.Documents.DeleteDocument;

/// <summary>
/// Deletes a document. Its chunks go with it by cascade.
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
/// <b>What this does not clean up, yet.</b> The stored file and the vectors in Qdrant.
/// Neither exists before Steps 7 and 8, and inventing a deletion protocol for stores that
/// have no writer would be guessing. Step 8 owns making this a complete removal; until
/// then, deleting a document leaves nothing orphaned because nothing else has been written.
/// Recorded as a known limitation in docs/PROGRESS.md so it cannot be forgotten.
/// </para>
/// </remarks>
public sealed class DeleteDocumentHandler(IDocumentRepository documents, IUnitOfWork unitOfWork)
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

        return Result.Success();
    }
}
