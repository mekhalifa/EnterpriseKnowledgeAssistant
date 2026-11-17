using System.Diagnostics;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Documents.ExtractDocumentText;
using EKA.Application.Documents.FailExhaustedIngestion;
using EKA.Application.Documents.IndexDocument;
using EKA.Application.Documents.RecoverStalledDocuments;
using EKA.Domain.Common;
using EKA.Infrastructure.Observability;
using Hangfire;
using Hangfire.Server;
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
public sealed partial class IngestionJobs(
    ExtractDocumentTextHandler extract,
    IndexDocumentHandler index,
    RecoverStalledDocumentsHandler recover,
    FailExhaustedIngestionHandler failExhausted,
    ILogger<IngestionJobs> logger)
{
    public const string Queue = "ingestion";
    public const string RecoverStalledJobId = "recover-stalled-documents";

    /// <summary>Retries after the first attempt. Six runs in all.</summary>
    public const int MaxRetries = 5;

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
    /// <see cref="MaxRetries"/> retries with Hangfire's growing delay (about 30 s, 1 min,
    /// 2 min…). Retries cover <i>transient</i> failures only: both handlers record permanent
    /// ones on the document and return normally.
    /// </para>
    /// <para>
    /// <b>The last attempt (Step 14).</b> When it fails too, the document is marked Failed
    /// before the exception goes back to Hangfire, so its owner sees a failure with a reason
    /// instead of "in progress" for ever, and can reset it. Hangfire supplies
    /// <paramref name="context"/> (the caller passes <c>null</c>); its <c>RetryCount</c>
    /// parameter is how many retries have already run. Doing this in the job, rather than in a
    /// global Hangfire state filter, keeps it in DI and out of static, process-wide state.
    /// </para>
    /// </remarks>
    [Queue(Queue)]
    [AutomaticRetry(Attempts = MaxRetries, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task IngestAsync(Guid tenantId, Guid documentId, PerformContext? context, CancellationToken cancellationToken)
    {
        var retryCount = context?.GetJobParameter<int>("RetryCount") ?? 0;

        // One trace per job (ADR-0035). Consumer: it is the receiving end of a queued message,
        // and the sampler starts traces only at Server and Consumer spans. Everything the job
        // does (SQL, Qdrant, the embedding call) becomes a child span of this one.
        using var activity = Telemetry.IngestionSource.StartActivity("ingest document", ActivityKind.Consumer);
        activity?.SetTag("eka.document.id", documentId);
        activity?.SetTag("eka.tenant.id", tenantId);
        activity?.SetTag("eka.job.retry_count", retryCount);

        var tenant = new TenantId(tenantId);

        try
        {
            await extract.HandleAsync(new ExtractDocumentTextCommand(tenant, documentId), cancellationToken).ConfigureAwait(false);
            await index.HandleAsync(new IndexDocumentCommand(tenant, documentId), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);

            if (retryCount >= MaxRetries)
            {
                await MarkExhaustedAsync(tenant, documentId, exception).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Jobs enqueued before Step 14 have this three-argument signature. Hangfire resolves a
    /// stored job by type, method name and parameter types, so it stays as a forwarder until no
    /// job under it remains queued, scheduled or retrying. Those old jobs get no terminal
    /// handling (they carry no <c>PerformContext</c>); that is the price of not losing them.
    /// </summary>
    [Queue(Queue)]
    [AutomaticRetry(Attempts = MaxRetries, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task IngestAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken) =>
        IngestAsync(tenantId, documentId, null, cancellationToken);

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
    [AutomaticRetry(Attempts = MaxRetries, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task ExtractTextAsync(Guid tenantId, Guid documentId, CancellationToken cancellationToken) =>
        IngestAsync(tenantId, documentId, null, cancellationToken);

    /// <summary>
    /// Best effort: if marking fails as well (the database is what is down), the job still
    /// fails as before and the document keeps its intermediate status. The original exception
    /// is the one that matters and is the one rethrown.
    /// </summary>
    private async Task MarkExhaustedAsync(TenantId tenant, Guid documentId, Exception cause)
    {
        try
        {
            // The exception TYPE, not its message: the reason is shown to the document's owner,
            // and messages carry host names, SQL and provider responses. The message is in the log.
            await failExhausted.HandleAsync(
                    new FailExhaustedIngestionCommand(tenant, documentId, cause.GetType().Name),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCouldNotMarkFailed(logger, documentId, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingestion retries for document {DocumentId} are exhausted, and marking it Failed also failed; it keeps its intermediate status")]
    private static partial void LogCouldNotMarkFailed(ILogger logger, Guid documentId, Exception exception);

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
            client.Enqueue<IngestionJobs>(jobs => jobs.IngestAsync(tenantId.Value, documentId, null, CancellationToken.None));
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
