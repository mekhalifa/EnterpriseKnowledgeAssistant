using EKA.Contracts.Documents;
using EKA.Domain.Access;
using EKA.Domain.Ingestion;

namespace EKA.Application.Documents;

/// <summary>
/// Turns a <see cref="Document"/> into the shapes the API publishes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-written, not AutoMapper.</b> The mapping is the boundary between an internal
/// model and a published contract, and it is exactly where a leak happens — the day someone
/// adds <c>StorageKey</c> to the entity, a convention-based mapper may start emitting it
/// without anyone writing a line of code. Here, a new field appears on the wire only when
/// somebody types it into this file, which a reviewer can see.
/// </para>
/// <para>
/// The usual argument for a mapping library is volume. Six properties across two shapes is
/// not volume; it is thirty lines that never need debugging at four in the morning.
/// </para>
/// <para>
/// This lives in <c>EKA.Application</c> rather than <c>EKA.Api</c> because the handler
/// returns the contract type directly. The alternative — handlers returning entities and
/// endpoints mapping them — puts domain objects in the endpoint signature, one careless
/// <c>Results.Ok(document)</c> away from serialising the entity.
/// </para>
/// </remarks>
/// <para>
/// <b><c>CanManage</c> is computed for the caller (Step 13).</b> It is the aggregate's own rule
/// (<see cref="Document.CanBeManagedBy"/>), answered for whoever asked, so the client can hide an
/// access editor or a delete button it would only get a 403 from. It is an affordance, not a
/// control: the handlers still check, and a client that ignores it still gets the 403.
/// </para>
internal static class DocumentMapping
{
    public static DocumentResponse ToResponse(this Document document, AccessScope caller) =>
        new(
            document.Id,
            document.Title.Value,
            document.File.FileName,
            document.File.ContentType,
            document.File.SizeBytes,
            document.File.ContentHash,
            document.Status.ToString(),
            document.Authority.Value,
            document.ChunkCount,
            document.IsSearchable,
            document.FailureReason,
            document.FailureCount,
            document.CreatedAt,
            document.ModifiedAt,
            document.ExtractedAt,
            document.IngestedAt,
            [.. document.AccessKeys],
            document.CanBeManagedBy(caller));

    public static DocumentSummaryResponse ToSummary(this Document document, AccessScope caller) =>
        new(
            document.Id,
            document.Title.Value,
            document.File.FileName,
            document.File.ContentType,
            document.File.SizeBytes,
            document.Status.ToString(),
            document.ChunkCount,
            document.CreatedAt,
            document.CanBeManagedBy(caller));
}
