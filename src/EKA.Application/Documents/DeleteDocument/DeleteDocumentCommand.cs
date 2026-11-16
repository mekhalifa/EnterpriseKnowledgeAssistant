using EKA.Domain.Access;

namespace EKA.Application.Documents.DeleteDocument;

/// <summary>Removes a document and its chunks.</summary>
/// <param name="Caller">Who is asking. A document they may not read is not found.</param>
/// <param name="DocumentId">The document to delete.</param>
public sealed record DeleteDocumentCommand(AccessScope Caller, Guid DocumentId);
