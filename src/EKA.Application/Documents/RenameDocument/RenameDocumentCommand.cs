using EKA.Domain.Access;

namespace EKA.Application.Documents.RenameDocument;

/// <summary>Changes a document's display name.</summary>
/// <param name="Caller">Who is asking. A document they may not read is not found.</param>
/// <param name="DocumentId">The document to rename.</param>
/// <param name="Title">The new title.</param>
public sealed record RenameDocumentCommand(AccessScope Caller, Guid DocumentId, string Title);
