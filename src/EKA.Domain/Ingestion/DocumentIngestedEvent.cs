using EKA.Domain.Common;

namespace EKA.Domain.Ingestion;

/// <summary>
/// A document finished ingestion and is now searchable.
/// </summary>
/// <remarks>
/// Several unrelated things should happen at this moment — notify the uploader, invalidate
/// a cached document list, trigger re-evaluation of the question set. Calling them from the
/// pipeline would make ingestion depend on all of them; raising an event means the pipeline
/// depends on none.
/// </remarks>
public sealed record DocumentIngestedEvent(
    Guid DocumentId,
    TenantId TenantId,
    int ChunkCount,
    DateTimeOffset OccurredAt) : IDomainEvent;
