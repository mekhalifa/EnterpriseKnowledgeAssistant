using EKA.Domain.Access;

namespace EKA.Application.Documents.ResetDocument;

/// <summary>Returns a failed document to <c>Pending</c> so ingestion can be retried.</summary>
/// <param name="Caller">Who is asking. A document they may not read is not found.</param>
/// <param name="DocumentId">The document to reset.</param>
public sealed record ResetDocumentCommand(AccessScope Caller, Guid DocumentId);
