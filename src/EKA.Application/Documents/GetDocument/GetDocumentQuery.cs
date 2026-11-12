using EKA.Domain.Common;

namespace EKA.Application.Documents.GetDocument;

/// <summary>Reads one document.</summary>
/// <param name="TenantId">The caller's tenant. Applied inside the query, not after it.</param>
/// <param name="DocumentId">The document to read.</param>
public sealed record GetDocumentQuery(TenantId TenantId, Guid DocumentId);
