using EKA.Domain.Common;

namespace EKA.Application.Documents.ResetDocument;

/// <summary>Returns a failed document to <c>Pending</c> so ingestion can be retried.</summary>
/// <param name="TenantId">The caller's tenant.</param>
/// <param name="DocumentId">The document to reset.</param>
public sealed record ResetDocumentCommand(TenantId TenantId, Guid DocumentId);
