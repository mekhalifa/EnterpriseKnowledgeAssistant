namespace EKA.Domain.Common;

/// <summary>
/// A domain invariant was broken — the code tried to create or mutate an entity into a
/// state the business rules forbid.
/// </summary>
/// <remarks>
/// <para>
/// This is for <b>programmer error</b>, not expected outcomes. "Document not found" and
/// "file type unsupported" are ordinary results that a use case returns as a
/// <c>Result</c> failure; they are not exceptions. A <see cref="DomainException"/> means
/// something upstream failed to validate and reached the domain with nonsense, so it
/// should surface loudly rather than be handled.
/// </para>
/// <para>
/// Step 4 maps this to HTTP 500 (a bug), while <see cref="InvalidStateTransitionException"/>
/// maps to 409 Conflict (a legitimate request arriving at the wrong moment).
/// </para>
/// </remarks>
public class DomainException : Exception
{
    public DomainException()
        : base("A domain invariant was violated.")
    {
    }

    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
