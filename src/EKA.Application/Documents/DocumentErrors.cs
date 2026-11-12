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

    public static Error PageOutOfRange(int page) => Error.Validation(
        "document.page_out_of_range",
        $"Page must be 1 or greater; got {page}.");

    public static Error PageSizeOutOfRange(int pageSize) => Error.Validation(
        "document.page_size_out_of_range",
        $"Page size must be 1 or greater; got {pageSize}.");

    public static Error UnknownStatus(string status) => Error.Validation(
        "document.unknown_status",
        $"'{status}' is not a known ingestion status.");
}
