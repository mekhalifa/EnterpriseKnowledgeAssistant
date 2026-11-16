using EKA.Domain.Access;

namespace EKA.Application.Documents.GetDocument;

/// <summary>Reads one document.</summary>
/// <param name="Caller">Who is asking. The tenant is applied inside the query; the document's
/// access list is checked before anything is returned (Step 9).</param>
/// <param name="DocumentId">The document to read.</param>
public sealed record GetDocumentQuery(AccessScope Caller, Guid DocumentId);
