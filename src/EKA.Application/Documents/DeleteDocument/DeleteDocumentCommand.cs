using EKA.Domain.Common;

namespace EKA.Application.Documents.DeleteDocument;

/// <summary>Removes a document and its chunks.</summary>
/// <param name="TenantId">The caller's tenant.</param>
/// <param name="DocumentId">The document to delete.</param>
public sealed record DeleteDocumentCommand(TenantId TenantId, Guid DocumentId);
