using Microsoft.Extensions.Diagnostics.HealthChecks;
using Qdrant.Client;

namespace EKA.Infrastructure.VectorStore;

/// <summary>
/// Readiness: can this process reach Qdrant (Step 14)?
/// </summary>
/// <remarks>
/// An API that cannot reach Qdrant cannot search, so it should not receive traffic. Its health
/// RPC is cheap and reads no data, which suits a probe that runs every few seconds.
/// The response says only "unhealthy"; the exception goes to the log, not to whoever asks.
/// </remarks>
internal sealed class QdrantHealthCheck(QdrantClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.HealthAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Qdrant is unreachable.", exception);
        }
    }
}
