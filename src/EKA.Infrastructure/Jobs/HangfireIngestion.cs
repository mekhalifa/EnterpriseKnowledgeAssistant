using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Documents.ExtractDocumentText;
using EKA.Application.Documents.IndexDocument;
using EKA.Application.Documents.RecoverStalledDocuments;
using EKA.Domain.Common;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EKA.Infrastructure.Jobs;

/// <summary>
/// The job methods Hangfire invokes in the worker. Thin by design: unwrap primitives, call
/// the use case. Everything that matters is in the handlers, which know nothing of Hangfire.
/// </summary>
/// <remarks>
/// <para>
/// <b>Primitive arguments only.</b> Hangfire serializes arguments to JSON in its own tables,
/// and they are deserialized later, possibly by a newer build. A <c>Guid</c> survives that
/// unchanged. A domain type like <c>TenantId</c> would tie stored jobs to its serialized
/// shape forever.
/// </para>
/// <para>
/// <b>Public</b> because Hangfire records the job's type and method by name and resolves
/// them by reflection. Instances come from the container, one scope per job, so each run
/// gets its own <c>DbContext</c>.
/// </para>
/// </remarks>
public sealed class IngestionJobs(
    ExtractDocumentTextHandler extract,
    IndexDocumentHandler index,
    RecoverStalledDocumentsHandler recover)
{
    public const string Queue = "ingestion";
    public const string RecoverStalledJobId = "recover-stalled-documents";

    /// <summary>
    /// The whole pipeline for one document: extract, then chunk and embed. Each stage asks the
    /// document whether it is owed, so one method serves a fresh upload, a redelivery, and a
    /// retry that resumes halfway through embedding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One job, not one per stage.</b> Separate jobs chained by continuations would let each
    /// stage have its own retry policy, at the price of a hand-off between stages that can be
    /// lost like the upload's can, and a sweep for every intermediate status. With stages
    /// keyed on the document's state, a single job and the existing Pending sweep cover it.
    /// </para>
    /// <para>
    /// Five attempts with Hangfire's growing delay (about 30 s, 1 min, 2 min…). Retries cover
    /// <i>transient</i> failures only: both handlers record permanent ones on the document and
    /// return normally. After the last attempt the job sits in Hangfire's Failed list and the
    /// document keeps its intermediate status: a known gap (PROGRESS).
    /// </para>
    /// </remarks>
    [Queue(Queue)]
    [AutomaticRetry(Attempts = 5, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task IngestAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(tenantId);

        await extract.HandleAsync(new ExtractDocumentTextCommand(tenant, documentId), cancellationToken).ConfigureAwait(false);
        await index.HandleAsync(new IndexDocumentCommand(tenant, documentId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The Step 7 job, kept so jobs already stored under this name still run after an upgrade.
    /// </summary>
    /// <remarks>
    /// Hangfire stores a job as type name, method name and arguments, and resolves them when
    /// the job runs, possibly days later on a newer build. Renaming or deleting a job method is
    /// therefore a breaking change to stored data, just like a column rename. This forwarder can
    /// be removed once no job under the old name remains queued, scheduled or retrying.
    /// </remarks>
    [Queue(Queue)]
    [AutomaticRetry(Attempts = 5, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task ExtractTextAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken) =>
        IngestAsync(tenantId, documentId, cancellationToken);

    /// <summary>
    /// The sweep. No retry: it runs again on schedule anyway. Overlap is prevented, because
    /// two sweeps at once would double-enqueue every stalled document.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task<int> RecoverStalledAsync(CancellationToken cancellationToken) =>
        recover.HandleAsync(cancellationToken);
}

/// <summary>
/// <see cref="IIngestionQueue"/> over Hangfire: the API's half of the hand-off (ADR-0020).
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolves the job client inside the try,</b> through the service provider, rather than
/// taking it as a constructor dependency. Building Hangfire's SQL storage touches the
/// database. As a constructor dependency, a job store that is down or not yet installed
/// would fail the whole upload request <i>before</i> this class could apply its contract:
/// the document is saved, the hand-off is best effort, and the sweep recovers. This is the
/// one place a service locator is the honest shape.
/// </para>
/// </remarks>
internal sealed partial class HangfireIngestionQueue(
    IServiceProvider services,
    ILogger<HangfireIngestionQueue> logger) : IIngestionQueue
{
    public Task<bool> EnqueueIngestionAsync(TenantId tenantId, Guid documentId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = services.GetRequiredService<IBackgroundJobClient>();
            client.Enqueue<IngestionJobs>(jobs => jobs.IngestAsync(tenantId.Value, documentId, CancellationToken.None));
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogEnqueueFailed(logger, documentId, exception);
            return Task.FromResult(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not enqueue ingestion for document {DocumentId}; the stalled-document sweep will retry it")]
    private static partial void LogEnqueueFailed(ILogger logger, Guid documentId, Exception exception);
}
