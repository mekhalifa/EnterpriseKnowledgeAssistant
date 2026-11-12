using EKA.Domain.Common;

namespace EKA.Application.Documents.ListDocuments;

/// <summary>One page of a tenant's documents.</summary>
/// <param name="TenantId">The caller's tenant.</param>
/// <param name="Status">
/// Ingestion status name to filter by, or null for all. A string rather than the enum
/// because it arrives from a query string and an unrecognised value should become a 400
/// naming the offending text, not a silent fall-back to the first enum member — which is
/// exactly what <c>Enum.TryParse</c> gives you when nobody checks its return value.
/// </param>
/// <param name="Page">One-based page number.</param>
/// <param name="PageSize">Requested page size; the server caps it.</param>
public sealed record ListDocumentsQuery(
    TenantId TenantId,
    string? Status,
    int Page,
    int PageSize);
