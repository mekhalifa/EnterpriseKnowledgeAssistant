using EKA.Application.Abstractions;
using EKA.Application.Common;
using EKA.Contracts.Documents;
using EKA.Domain.Ingestion;

namespace EKA.Application.Documents.RenameDocument;

/// <summary>
/// Renames a document. Legal in every ingestion state.
/// </summary>
/// <remarks>
/// Note what this handler does <b>not</b> do: it does not assign to a property. It loads
/// the aggregate and calls <see cref="Document.Rename"/>, which is the only way the title
/// can change, because the entity has no public setter. The handler orchestrates; the
/// domain decides. That is the whole division of labour between these two layers, and it is
/// what stops business rules from spreading into application code where nobody thinks to
/// look for them.
/// </remarks>
public sealed class RenameDocumentHandler(
    IDocumentRepository documents,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<DocumentResponse>> HandleAsync(
        RenameDocumentCommand command,
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

        document.Rename(new DocumentTitle(command.Title), timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return document.ToResponse();
    }
}
