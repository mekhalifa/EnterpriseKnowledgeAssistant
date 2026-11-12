using EKA.Domain.Common;

namespace EKA.Application.Abstractions;

/// <summary>
/// Delivers domain events to their handlers.
/// </summary>
/// <remarks>
/// Called <b>after</b> the transaction commits, never before. An event dispatched inside
/// the transaction can trigger a handler that acts on state which then rolls back — a user
/// notified that their document is searchable when it is not.
/// </remarks>
public interface IDomainEventDispatcher
{
    Task DispatchAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default);
}
