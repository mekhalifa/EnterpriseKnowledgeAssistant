using EKA.Application.Abstractions;
using EKA.Domain.Common;
using Microsoft.Extensions.Logging;

namespace EKA.Infrastructure.Persistence;

/// <summary>
/// Interim dispatcher: records that events were raised, but invokes no handlers.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a placeholder, and it is deliberately loud about it.</b> Real handlers arrive
/// in Step 7, when ingestion needs post-commit side effects (notify the uploader,
/// invalidate a cached document list, trigger re-evaluation). Until then nothing subscribes
/// to domain events.
/// </para>
/// <para>
/// A silent no-op was the alternative and was rejected: events would vanish with no trace,
/// and the first real handler would be debugged against a dispatcher that never ran. Logging
/// each event at information level means the wiring is visibly working — and visibly
/// incomplete — from the first integration test onward.
/// </para>
/// <para>
/// Tracked in docs/PROGRESS.md under known limitations.
/// </para>
/// </remarks>
public sealed class LoggingDomainEventDispatcher : IDomainEventDispatcher
{
    private readonly ILogger<LoggingDomainEventDispatcher> _logger;

    public LoggingDomainEventDispatcher(ILogger<LoggingDomainEventDispatcher> logger)
    {
        _logger = logger;
    }

    public Task DispatchAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (var domainEvent in domainEvents)
        {
            _logger.LogInformation(
                "Domain event {DomainEvent} occurred at {OccurredAt}. No handler is "
                + "registered yet; handlers arrive in Step 7.",
                domainEvent.GetType().Name,
                domainEvent.OccurredAt);
        }

        return Task.CompletedTask;
    }
}
