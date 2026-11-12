using EKA.Application.Abstractions;
using EKA.Application.Common;
using EKA.Contracts.Common;
using EKA.Contracts.Documents;
using EKA.Domain.Ingestion;

namespace EKA.Application.Documents.ListDocuments;

/// <summary>
/// Returns one page of documents, newest first.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reject or clamp?</b> Both, for different inputs, and the line between them is whether
/// the value means the client is broken.
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <c>page=0</c> or <c>pageSize=-5</c> cannot be what anyone meant. Silently serving page 1
/// hides the bug until someone wonders why their pagination skips rows, so these are a 400.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>pageSize=100000</c> is a reasonable request meeting a server policy. Capping it to
/// <see cref="MaxPageSize"/> serves the caller something useful and protects the server,
/// and the response states the page size actually applied so the client can tell. This cap
/// is not politeness: without it, one query materialises every row a tenant owns.
/// </description>
/// </item>
/// </list>
/// </remarks>
public sealed class ListDocumentsHandler(IDocumentRepository documents)
{
    /// <summary>Largest page the server will serve, however large a page is asked for.</summary>
    public const int MaxPageSize = 100;

    public const int DefaultPageSize = 20;

    public async Task<Result<PagedResponse<DocumentSummaryResponse>>> HandleAsync(
        ListDocumentsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Page < 1)
        {
            return DocumentErrors.PageOutOfRange(query.Page);
        }

        if (query.PageSize < 1)
        {
            return DocumentErrors.PageSizeOutOfRange(query.PageSize);
        }

        IngestionStatus? status = null;

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            // The return value IS checked. Ignoring it leaves `parsed` at 0 — which is not
            // even a declared IngestionStatus, since the enum starts at 10 — and the filter
            // then matches nothing while reporting success.
            if (!Enum.TryParse<IngestionStatus>(query.Status, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                return DocumentErrors.UnknownStatus(query.Status);
            }

            status = parsed;
        }

        var pageSize = Math.Min(query.PageSize, MaxPageSize);

        var page = await documents
            .ListAsync(
                query.TenantId,
                status,
                skip: (query.Page - 1) * pageSize,
                take: pageSize,
                cancellationToken)
            .ConfigureAwait(false);

        return new PagedResponse<DocumentSummaryResponse>(
            [.. page.Items.Select(document => document.ToSummary())],
            query.Page,
            pageSize,
            page.TotalCount);
    }
}
