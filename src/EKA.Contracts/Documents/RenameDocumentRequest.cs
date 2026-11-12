namespace EKA.Contracts.Documents;

/// <summary>Changes a document's display name. Legal in any ingestion state.</summary>
/// <param name="Title">The new display name.</param>
public sealed record RenameDocumentRequest(string Title);
