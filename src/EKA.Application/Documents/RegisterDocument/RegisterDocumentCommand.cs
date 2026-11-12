using EKA.Contracts.Documents;
using EKA.Domain.Common;

namespace EKA.Application.Documents.RegisterDocument;

/// <summary>
/// Registers a document's metadata so ingestion can pick it up.
/// </summary>
/// <param name="TenantId">
/// The owning tenant, taken from the caller's authenticated identity.
/// </param>
/// <param name="Title">Display name.</param>
/// <param name="FileName">Original file name.</param>
/// <param name="ContentType">MIME type.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="ContentHash">Lowercase hex SHA-256 of the file's bytes.</param>
/// <param name="Authority">Trust score 0–100, or null for the default of 50.</param>
/// <remarks>
/// <para>
/// <b>Why a command type at all, when <see cref="RegisterDocumentRequest"/> already exists.</b>
/// Because of the first parameter. <c>TenantId</c> is not in the request and must never be:
/// a tenant id a client can type is a tenant id a client can change. The command is the
/// request plus what only the server knows, and keeping them as separate types makes it
/// impossible to bind the tenant from a request body by accident.
/// </para>
/// <para>
/// The second reason is direction. The request is a published contract that the Angular
/// client in Step 13 generates types from; the command is internal and free to change. One
/// type serving both roles means every internal refactor is a breaking API change.
/// </para>
/// </remarks>
public sealed record RegisterDocumentCommand(
    TenantId TenantId,
    string Title,
    string FileName,
    string ContentType,
    long SizeBytes,
    string ContentHash,
    int? Authority)
{
    /// <summary>
    /// Builds the command from a validated request and the caller's tenant.
    /// </summary>
    /// <remarks>
    /// Written here rather than inline in the endpoint so that the one place a tenant
    /// enters a command is greppable. Every slice follows the same shape.
    /// </remarks>
    public static RegisterDocumentCommand From(RegisterDocumentRequest request, TenantId tenantId)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new RegisterDocumentCommand(
            tenantId,
            request.Title,
            request.FileName,
            request.ContentType,
            request.SizeBytes,
            request.ContentHash,
            request.Authority);
    }
}
