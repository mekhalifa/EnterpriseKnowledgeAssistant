namespace EKA.Contracts.Documents;

/// <summary>Replaces who may read a document.</summary>
/// <param name="Principals">
/// <c>["tenant"]</c> for everyone in the tenant, or a list of <c>"user:&lt;subject&gt;"</c> and
/// <c>"group:&lt;group id&gt;"</c>. Never empty, and <c>"tenant"</c> cannot be combined with
/// others.
/// </param>
/// <remarks>
/// A full replacement (PUT), not add/remove operations. An access list is small, and a client
/// that sends the whole list states the outcome it wants. Two concurrent "add" calls cannot
/// then leave a list that neither caller asked for.
/// </remarks>
public sealed record SetDocumentAccessRequest(IReadOnlyList<string> Principals);
