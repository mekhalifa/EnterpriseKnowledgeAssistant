using EKA.Application.Abstractions;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Documents.Ingestion;
using Microsoft.Extensions.Options;

namespace EKA.Application.Documents.RecoverStalledDocuments;

/// <summary>
/// Re-enqueues documents that have sat in Pending longer than a hand-off should take: the
/// recovery half of the best-effort enqueue (ADR-0020).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> An upload writes the document row and then enqueues a job, two
/// writes with no transaction spanning them. If the enqueue fails (job storage not yet
/// created, a network blip, the process killed between the two), the document would wait
/// forever. This sweep, run on a schedule by the worker, turns "forever" into
/// "<c>StalledAfter</c>".
/// </para>
/// <para>
/// <b>The one cross-tenant read in the application layer,</b> and deliberately so: a system
/// job serves every tenant. It reads ids only, then hands each document to a job that
/// reloads it <i>tenant-scoped</i>. No document content crosses a tenant boundary here.
/// </para>
/// <para>
/// A duplicate enqueue, of a document whose original job was merely slow, is harmless:
/// extraction is idempotent. That is the trade the whole design rests on. At-least-once plus
/// idempotent replaces exactly-once, which a database and a queue cannot give together
/// without a distributed transaction.
/// </para>
/// </remarks>
public sealed class RecoverStalledDocumentsHandler(
    IDocumentRepository documents,
    IIngestionQueue queue,
    IOptions<IngestionOptions> options,
    TimeProvider timeProvider)
{
    private readonly IngestionOptions _options = options.Value;

    /// <returns>How many stalled documents were re-enqueued.</returns>
    public async Task<int> HandleAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = timeProvider.GetUtcNow() - _options.StalledAfter;

        var stalled = await documents
            .GetPendingIngestionAsync(cutoff, _options.SweepBatchSize, cancellationToken)
            .ConfigureAwait(false);

        var requeued = 0;
        foreach (var document in stalled)
        {
            if (await queue.EnqueueExtractionAsync(document.TenantId, document.Id, cancellationToken).ConfigureAwait(false))
            {
                requeued++;
            }
        }

        return requeued;
    }
}
