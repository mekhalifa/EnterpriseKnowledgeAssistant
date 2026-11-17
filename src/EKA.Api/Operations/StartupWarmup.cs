using System.Diagnostics;
using EKA.Application.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace EKA.Api.Operations;

/// <summary>
/// Pays the API's first-request costs before it reports ready (Step 14).
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem.</b> Step 13 measured ~20 s for the first authenticated request after a
/// start: the identity provider's metadata and signing keys are fetched on first use, the EF
/// model is built on the first DbContext, and the SQL and Qdrant connections open on first use.
/// A user, or a load balancer that marked the instance ready the moment the process
/// listened, paid all of it on one request, and the first e2e run timed out on it.
/// </para>
/// <para>
/// <b>The fix.</b> Do that work once at startup, and keep <c>/health/ready</c> failing until it
/// is done, so traffic arrives only at a warm instance. Liveness is untouched: a warming
/// instance is alive, and restarting it would only start the warm-up again.
/// </para>
/// <para>
/// <b>Best effort, one pass.</b> A failure is logged and does not block readiness for ever:
/// the readiness checks themselves (SQL Server, Qdrant) still decide whether the instance can
/// serve, and the JWT handler fetches keys again on demand if the identity provider was down.
/// Warm-up removes latency; it is not a second set of health checks.
/// </para>
/// </remarks>
internal sealed partial class StartupWarmup(
    IServiceProvider services,
    WarmupState state,
    ILogger<StartupWarmup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var total = Stopwatch.StartNew();

        await StepAsync("signing keys", async cancellationToken =>
        {
            // The JWT handler's ConfigurationManager caches metadata and keys; filling it now is
            // exactly the fetch the first request would otherwise make.
            var options = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme);
            if (options.ConfigurationManager is { } manager)
            {
                await manager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            }
        }, stoppingToken).ConfigureAwait(false);

        await StepAsync("dependencies", async cancellationToken =>
        {
            // Runs the readiness checks once: the SQL check creates a DbContext (building the EF
            // model) and opens a pooled connection; the Qdrant check opens the gRPC channel.
            var health = services.GetRequiredService<HealthCheckService>();
            await health.CheckHealthAsync(
                    registration => registration.Tags.Contains(HealthTags.Ready) && registration.Name != WarmupHealthCheck.Name,
                    cancellationToken)
                .ConfigureAwait(false);
        }, stoppingToken).ConfigureAwait(false);

        state.MarkComplete();
        LogWarm(logger, total.ElapsedMilliseconds);
    }

    private async Task StepAsync(string name, Func<CancellationToken, Task> step, CancellationToken stoppingToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await step(timeout.Token).ConfigureAwait(false);
            LogStep(logger, name, watch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            LogStepFailed(logger, name, watch.ElapsedMilliseconds, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Warm-up: {Step} in {ElapsedMs} ms")]
    private static partial void LogStep(ILogger logger, string step, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Warm-up: {Step} failed after {ElapsedMs} ms; the first request will pay for it")]
    private static partial void LogStepFailed(ILogger logger, string step, long elapsedMs, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Warm-up complete in {ElapsedMs} ms; reporting ready")]
    private static partial void LogWarm(ILogger logger, long elapsedMs);
}

/// <summary>Whether warm-up has finished. A singleton shared by the warm-up and its check.</summary>
internal sealed class WarmupState
{
    private volatile bool _complete;

    public bool IsComplete => _complete;

    public void MarkComplete() => _complete = true;
}

/// <summary>Not ready until warm-up has run once.</summary>
internal sealed class WarmupHealthCheck(WarmupState state) : IHealthCheck
{
    public const string Name = "warm-up";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.IsComplete
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus, "Warming up."));
}

internal static class WarmupRegistration
{
    public static IServiceCollection AddStartupWarmup(this IServiceCollection services)
    {
        services.AddSingleton<WarmupState>();
        services.AddHostedService<StartupWarmup>();
        services.AddHealthChecks().AddCheck<WarmupHealthCheck>(WarmupHealthCheck.Name, tags: [HealthTags.Ready]);
        return services;
    }
}
