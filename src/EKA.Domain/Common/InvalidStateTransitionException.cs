namespace EKA.Domain.Common;

/// <summary>
/// An entity was asked to move between states its lifecycle does not allow — for example
/// completing ingestion for a document that was never extracted.
/// </summary>
/// <remarks>
/// Separate from plain <see cref="DomainException"/> because the two mean different things
/// to a caller. A broken invariant is a bug (HTTP 500). An illegal transition is usually a
/// legitimate request that arrived at the wrong moment — a retry racing the original, or a
/// stale client — which is HTTP 409 Conflict, and the caller may reasonably re-read and
/// try again.
/// </remarks>
public sealed class InvalidStateTransitionException : DomainException
{
    public InvalidStateTransitionException(string entity, object from, object to)
        : base($"{entity} cannot move from {from} to {to}.")
    {
        Entity = entity;
        From = from.ToString() ?? "unknown";
        To = to.ToString() ?? "unknown";
    }

    public string Entity { get; }

    public string From { get; }

    public string To { get; }
}
