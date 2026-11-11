using EKA.Domain.Common;

namespace EKA.Domain.Ingestion;

/// <summary>
/// The one-based page span a chunk came from. Optional: plain text and HTML have no pages.
/// </summary>
/// <remarks>
/// This is what makes a citation verifiable rather than decorative. "See the employee
/// handbook" is unfalsifiable; "employee handbook, pages 12–13" can be opened and checked,
/// which is the entire value proposition of a grounded knowledge assistant.
/// <para>
/// A sealed record, not a struct: pages are one-based, so <c>default</c> would yield the
/// invalid range 0–0 while appearing to be a legitimate value. Since the property is
/// nullable anyway, a class costs nothing and removes the trap.
/// </para>
/// </remarks>
public sealed record PageRange
{
    public PageRange(int start, int end)
    {
        if (start < 1)
        {
            throw new DomainException($"Page numbers are one-based; got start page {start}.");
        }

        if (end < start)
        {
            throw new DomainException(
                $"Page range cannot end before it starts; got {start}-{end}.");
        }

        Start = start;
        End = end;
    }

    public int Start { get; }

    public int End { get; }

    public bool IsSinglePage => Start == End;

    public int PageCount => End - Start + 1;

    public static PageRange SinglePage(int page) => new(page, page);

    public override string ToString() => IsSinglePage ? $"p. {Start}" : $"pp. {Start}-{End}";
}
