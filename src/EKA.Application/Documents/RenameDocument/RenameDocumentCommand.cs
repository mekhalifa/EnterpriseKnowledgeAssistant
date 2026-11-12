using EKA.Domain.Common;

namespace EKA.Application.Documents.RenameDocument;

/// <summary>Changes a document's display name.</summary>
/// <param name="TenantId">The caller's tenant.</param>
/// <param name="DocumentId">The document to rename.</param>
/// <param name="Title">The new title.</param>
public sealed record RenameDocumentCommand(TenantId TenantId, Guid DocumentId, string Title);
