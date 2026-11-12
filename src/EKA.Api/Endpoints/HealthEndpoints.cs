using System.Text.Json;
using EKA.Application.Common;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EKA.Api.Endpoints;

/// <summary>
/// Liveness and readiness probes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two probes, because they answer different questions and the orchestrator does
/// different things with the answers.</b>
/// </para>
/// <list type="table">
/// <item>
/// <term><c>/health/live</c></term>
/// <description>
/// "Is this process alive?" Checks nothing but its own ability to answer. A failure here
/// means <b>restart the container</b>.
/// </description>
/// </item>
/// <item>
/// <term><c>/health/ready</c></term>
/// <description>
/// "Can this instance serve traffic?" Checks the dependencies it cannot work without —
/// today SQL Server; Qdrant and Redis as they arrive. A failure means <b>take it out of
/// the load balancer and leave it running</b>.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>Why the distinction is not pedantry.</b> Put the database check on the liveness probe
/// and a brief database outage becomes a restart loop across every instance: each one is
/// killed for a fault it did not cause and cannot fix, and the stampede of reconnecting
/// instances then keeps the database down. Readiness sheds the traffic and waits; liveness
/// pulls the trigger. Conflating them turns a recoverable incident into an outage.
/// </para>
/// <para>
/// <b>Both are anonymous.</b> A probe that needs a token is a probe that reports "unhealthy"
/// the day the token expires. The responses carry check names and durations — no
/// connection strings, no exception text — so there is nothing here worth authenticating
/// for. Step 14 binds them to the internal port instead.
/// </para>
/// </remarks>
internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            // Runs no registered check. Reaching this endpoint at all is the answer.
            Predicate = _ => false,
        })
        .WithTags("Health")
        .AllowAnonymous();

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(HealthTags.Ready),
            ResponseWriter = WriteReadinessAsync,
        })
        .WithTags("Health")
        .AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Writes which dependency failed, not just that something did.
    /// </summary>
    /// <remarks>
    /// The default writer returns the single word "Unhealthy". At three in the morning the
    /// useful question is <i>which</i> dependency, and answering it should not require
    /// attaching a debugger to a container that is about to be recycled. The exception
    /// message is deliberately omitted — it is in the logs, and a health endpoint is the
    /// one endpoint most likely to be reachable from outside.
    /// </remarks>
    private static Task WriteReadinessAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = entry.Value.Duration.TotalMilliseconds,
                description = entry.Value.Description,
            }),
        }));
    }
}
