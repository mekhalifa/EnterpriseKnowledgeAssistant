namespace EKA.Application.Common;

/// <summary>
/// A slice of a larger set, plus how large that set is.
/// </summary>
/// <typeparam name="T">The item type — a domain entity on this side of the boundary.</typeparam>
/// <param name="Items">The items in this slice.</param>
/// <param name="TotalCount">Total matching items, ignoring the slice.</param>
/// <remarks>
/// The repository-side counterpart of <c>EKA.Contracts.Common.PagedResponse&lt;T&gt;</c>.
/// They look similar and are deliberately not the same type: this one carries entities and
/// knows nothing about page numbers, while the contract carries DTOs and is a published
/// wire format. Merging them would mean either entities on the wire or page arithmetic in a
/// repository.
/// </remarks>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);
