using EKA.Application.Abstractions;
using EKA.Application.Common;
using EKA.Contracts.Documents;

namespace EKA.Application.Documents.GetDocument;

/// <summary>
/// Returns one document, or a not-found error.
/// </summary>
/// <remarks>
/// <para>
/// A query, not a command: it stages nothing and never calls
/// <see cref="IUnitOfWork.SaveChangesAsync"/>. That separation is the "CQRS-lite" in this
/// step — not two databases and an event bus, just the discipline that a read path and a
/// write path are different kinds of thing with different dependencies. This handler does
/// not take <see cref="IUnitOfWork"/> at all, so it cannot accidentally write.
/// </para>
/// <para>
/// Chunks are deliberately not loaded. A 300-page PDF has thousands of them and a metadata
/// read has no use for any.
/// </para>
/// </remarks>
public sealed class GetDocumentHandler(IDocumentRepository documents)
{
    public async Task<Result<DocumentResponse>> HandleAsync(
        GetDocumentQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var document = await documents
            .GetByIdAsync(query.Caller.TenantId, query.DocumentId, cancellationToken)
            .ConfigureAwait(false);

        // A document the caller may neither read nor manage is "not found", exactly like one in
        // another tenant: a 403 would confirm that the id exists (see DocumentErrors.NotFound).
        return DocumentAuthorization.CannotSee(document, query.Caller, query.DocumentId, out var error)
            ? error
            : document.ToResponse();
    }
}
