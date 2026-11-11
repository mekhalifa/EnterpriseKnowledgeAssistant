namespace EKA.Domain.Common;

/// <summary>
/// Something that happened in the domain, expressed in past tense
/// (<c>DocumentIngestedEvent</c>, not <c>IngestDocumentCommand</c>).
/// </summary>
/// <remarks>
/// Events are raised by aggregates and dispatched <b>after</b> the transaction commits,
/// so no handler ever acts on state that then rolls back. In this monolith they are
/// in-process handlers; the same events become message-bus messages if a context is ever
/// extracted into its own service.
/// </remarks>
public interface IDomainEvent
{
    /// <summary>When the thing happened. Supplied by the caller, never read from the
    /// system clock inside the domain — see <see cref="EntityAudit{TId}"/>.</summary>
    DateTimeOffset OccurredAt { get; }
}
