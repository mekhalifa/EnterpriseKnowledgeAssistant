using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace EKA.Infrastructure.Observability;

/// <summary>
/// Logs, traces and metrics for either host, exported over OTLP (Step 14, ADR-0035).
/// </summary>
/// <remarks>
/// <para>
/// <b>Three pillars, three questions.</b> A <i>log</i> says what one piece of code decided. A
/// <i>trace</i> says where one request or job spent its time, across processes: the API's
/// request, its SQL queries, its call to Qdrant and the model. A <i>metric</i> says how the
/// whole system behaves over time: request rate, latency percentiles, GC pauses, tokens spent.
/// Each is cheap where the others are expensive, which is why all three exist.
/// </para>
/// <para>
/// <b>Export is configuration, not code</b> (twelve-factor). With
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> set, all three go to that collector; without it nothing
/// leaves the process and the logs still reach the console. The same build runs with a
/// collector in Compose and without one on a laptop.
/// </para>
/// <para>
/// <b>Logs stay on Microsoft.Extensions.Logging.</b> OpenTelemetry is added as one more
/// provider, so the <c>Logging:LogLevel</c> filters still decide what is written. One of them
/// keeps Microsoft.Extensions.AI at Information, which is what keeps prompts and document text
/// out of every log sink, the OTLP one included. The console format is configuration too:
/// <c>Logging:Console:FormatterName</c> is <c>json</c> in appsettings.json and <c>simple</c>
/// in Development.
/// </para>
/// </remarks>
public static class Telemetry
{
    /// <summary>The standard OpenTelemetry variable naming the collector.</summary>
    public const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>The ingestion job spans (one per Hangfire job), started by <c>JobTelemetryFilter</c>.</summary>
    public const string IngestionSourceName = "EKA.Ingestion";

    internal static readonly ActivitySource IngestionSource = new(IngestionSourceName);

    /// <param name="builder">The host.</param>
    /// <param name="serviceName">How this process appears in traces (<c>service.name</c>).</param>
    /// <param name="configureTracing">Host-specific instrumentation, e.g. ASP.NET Core in the API.</param>
    /// <param name="additionalSources">ActivitySource and Meter names from other layers (EKA.AI).</param>
    public static IHostApplicationBuilder AddEkaTelemetry(
        this IHostApplicationBuilder builder,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null,
        params string[] additionalSources)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName,
                    serviceVersion: typeof(Telemetry).Assembly.GetName().Version?.ToString(),
                    serviceInstanceId: Environment.MachineName)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new ParentBasedSampler(new EntryPointSampler()))
                    .AddSource(IngestionSourceName)
                    .AddSource(additionalSources)
                    .AddHttpClientInstrumentation()
                    .AddSqlClientInstrumentation();

                configureTracing?.Invoke(tracing);
            })
            .WithMetrics(metrics => metrics
                .AddRuntimeInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(additionalSources)
                .AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.RateLimiting"));

        if (!string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointVariable]))
        {
            // Reads OTEL_EXPORTER_OTLP_ENDPOINT / _PROTOCOL / _HEADERS itself, for all three signals.
            telemetry.UseOtlpExporter();
        }

        return builder;
    }
}

/// <summary>
/// Starts a trace only at an entry point: an incoming request (Server) or a dequeued job
/// (Consumer). Wrapped in <see cref="ParentBasedSampler"/>, so everything inside one follows it.
/// </summary>
/// <remarks>
/// Without it, every SQL command with no request around it becomes a trace of its own, and the
/// worker has one every second: Hangfire polling its queue. Thousands of one-span traces a hour
/// would bury the ones that matter and cost real storage in a paid backend. A Client span with
/// no parent is, by definition, nobody's request.
/// </remarks>
internal sealed class EntryPointSampler : Sampler
{
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
        samplingParameters.Kind is ActivityKind.Server or ActivityKind.Consumer
            ? new SamplingResult(SamplingDecision.RecordAndSample)
            : new SamplingResult(SamplingDecision.Drop);
}
