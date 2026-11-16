using EKA.Application.Common;

namespace EKA.Application.Documents;

/// <summary>
/// Every expected failure the document use cases can produce, declared once.
/// </summary>
/// <remarks>
/// <para>
/// Error codes are part of the API contract — a client branches on
/// <c>document.not_found</c> the way it branches on a status code. Scattering the string
/// literals across six handlers guarantees that two of them eventually disagree about
/// spelling, and nothing fails when they do. Collected here, the whole vocabulary is one
/// screen and a rename is one edit.
/// </para>
/// <para>
/// The convention is <c>feature.reason</c>, lowercase with underscores: stable under
/// refactoring, readable in a log, and unambiguous when it reaches a non-.NET client.
/// </para>
/// </remarks>
internal static class DocumentErrors
{
    /// <summary>
    /// Returned both when no such document exists and when it belongs to another tenant.
    /// </summary>
    /// <remarks>
    /// The two cases are deliberately indistinguishable. A 403 for "exists but not yours"
    /// confirms that the id is real, which lets an attacker enumerate another tenant's
    /// document ids without ever reading one — the metadata is the leak.
    /// </remarks>
    public static Error NotFound(Guid documentId) => Error.NotFound(
        "document.not_found",
        $"No document with id {documentId} was found.");

    /// <summary>
    /// The uploaded bytes are already a document in this tenant, and the caller may not read
    /// it. Deliberately says nothing about which document.
    /// </summary>
    public static readonly Error DuplicateNotAccessible = Error.Conflict(
        "document.duplicate_not_accessible",
        "A document with identical contents already exists in this tenant, and you do not have access to it.");

    /// <summary>
    /// The caller can see the document but may not change it: only its owner or a tenant
    /// administrator may (Step 12).
    /// </summary>
    /// <remarks>
    /// 403, not 404, because the caller can already read the document, so its existence is no
    /// secret to them. A 404 here would be a lie that sends a legitimate user looking for a
    /// bug. A document the caller cannot see at all is still 404.
    /// </remarks>
    public static Error NotManageable(Guid documentId) => Error.Forbidden(
        "document.not_manageable",
        $"Only the owner of document {documentId} or an administrator can change it.");

    /// <summary>The access list in a request is not one the domain accepts.</summary>
    public static Error InvalidAccess(string reason) => Error.Validation(
        "document.invalid_access",
        reason);

    public static Error PageOutOfRange(int page) => Error.Validation(
        "document.page_out_of_range",
        $"Page must be 1 or greater; got {page}.");

    public static Error PageSizeOutOfRange(int pageSize) => Error.Validation(
        "document.page_size_out_of_range",
        $"Page size must be 1 or greater; got {pageSize}.");

    public static Error UnknownStatus(string status) => Error.Validation(
        "document.unknown_status",
        $"'{status}' is not a known ingestion status.");

    public static readonly Error EmptyFile = Error.Validation(
        "document.empty_file",
        "The uploaded file is empty.");

    /// <summary>
    /// The bytes are not a supported format, or they disagree with the file's extension (a
    /// ZIP named <c>.pdf</c>, a PDF named <c>.docx</c>). The message names the formats
    /// accepted, not the format detected: telling a caller what we think their file "really"
    /// is helps nobody but someone probing the detector.
    /// </summary>
    public static Error UnsupportedFileType(string fileName) => Error.Validation(
        "document.unsupported_file_type",
        $"'{fileName}' is not a supported document. Accepted: PDF (.pdf), Word (.docx), text (.txt), Markdown (.md).");

    /// <remarks>
    /// Usually rejected earlier by the request-size limit with a bare 413. This is the
    /// limit enforced by the use case itself, for callers that are not the HTTP endpoint.
    /// </remarks>
    public static Error FileTooLarge(long maxBytes) => Error.Validation(
        "document.file_too_large",
        $"The file exceeds the {maxBytes / (1024 * 1024)} MB upload limit.");
}
