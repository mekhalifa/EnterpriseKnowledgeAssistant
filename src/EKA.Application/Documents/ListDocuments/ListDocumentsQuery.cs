using EKA.Domain.Access;

namespace EKA.Application.Documents.ListDocuments;

/// <summary>One page of the documents the caller may read.</summary>
/// <param name="Caller">Who is asking. Tenant and access list are both applied inside the
/// query, so the page and its total count only ever cover readable documents.</param>
/// <param name="Status">
/// Ingestion status name to filter by, or null for all. A string rather than the enum
/// because it arrives from a query string and an unrecognised value should become a 400
/// naming the offending text, not a silent fall-back to the first enum member — which is
/// exactly what <c>Enum.TryParse</c> gives you when nobody checks its return value.
/// </param>
/// <param name="Page">One-based page number.</param>
/// <param name="PageSize">Requested page size; the server caps it.</param>
public sealed record ListDocumentsQuery(
    AccessScope Caller,
    string? Status,
    int Page,
    int PageSize);
