using EKA.Domain.Common;

namespace EKA.Domain.Ingestion;

/// <summary>
/// A document's ingestion failed at some stage.
/// </summary>
/// <remarks>
/// Carries <see cref="FailureCount"/> so a handler can distinguish a first transient
/// failure worth retrying automatically from a document that has failed repeatedly and
/// needs a human to look at it.
/// </remarks>
public sealed record DocumentIngestionFailedEvent(
    Guid DocumentId,
    TenantId TenantId,
    IngestionStatus FailedAt,
    string Reason,
    int FailureCount,
    DateTimeOffset OccurredAt) : IDomainEvent;
