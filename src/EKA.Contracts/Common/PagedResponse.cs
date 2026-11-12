namespace EKA.Contracts.Common;

/// <summary>
/// One page of results plus enough information for a client to request the next.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Items">The items on this page, possibly empty.</param>
/// <param name="Page">One-based page number that produced <paramref name="Items"/>.</param>
/// <param name="PageSize">Maximum items per page, as applied by the server.</param>
/// <param name="TotalCount">Total matching items across all pages.</param>
/// <remarks>
/// <para>
/// <b>Offset paging, chosen knowingly.</b> It is what a page-numbered UI needs, and the
/// document list is that. Its two costs are real: deep pages make SQL Server scan and
/// discard everything before the offset, and an insert between two requests shifts items
/// across the page boundary so a row can be seen twice or missed.
/// </para>
/// <para>
/// Keyset ("seek") paging fixes both and is the right answer for an infinite-scroll feed or
/// a large export. It cannot answer "page 47 of 300", which is why it is not here. If the
/// document list ever grows past a few thousand rows per tenant, revisit this.
/// </para>
/// <para>
/// <see cref="TotalCount"/> costs a second <c>COUNT(*)</c> query. That is the price of
/// rendering "1–20 of 437", and it is worth paying at this scale; it is the first thing to
/// drop if the list ever gets slow.
/// </para>
/// </remarks>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasNextPage => Page < TotalPages;
}
