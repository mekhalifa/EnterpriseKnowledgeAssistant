using EKA.AI;
using EKA.Application;
using EKA.Application.Abstractions;
using EKA.Application.Documents.Indexing;
using EKA.Application.Documents.Ingestion;
using EKA.Infrastructure;

namespace EKA.Ingestion.Worker;

/// <summary>
/// The worker's composition root, as a method rather than inline in <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// Public so the integration tests can boot <i>this exact</i> worker in-process: same
/// registrations, same job server, same recurring sweep. The worker-side counterpart of
/// <c>WebApplicationFactory&lt;Program&gt;</c>. A test that assembled its own worker would
/// prove the handlers work and nothing about whether this process is wired to run them.
/// </remarks>
public static class WorkerComposition
{
    /// <summary>Concurrent ingestion jobs per worker process when not configured.</summary>
    public const int DefaultWorkerCount = 2;

    public static IServiceCollection AddIngestionWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Background work has no interactive user. Audit columns written by the worker say
        // so, rather than null (ambiguous) or a borrowed user identity (false).
        services.AddSingleton<ICurrentUser, ServiceIdentity>();

        // Only the ingestion use cases: the worker answers no HTTP requests, so it must not
        // register handlers it never runs, or carry their dependencies (step-07 section 5.4).
        services.AddApplication().AddIngestionHandlers();

        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<IndexingOptions>()
            .Bind(configuration.GetSection(IndexingOptions.SectionName))
            .ValidateOnStart();

        // The embedding model, since Step 8. Same adapter, resilience and configuration shape
        // as the API's (ADR-0015), and its own key: the worker reads Ai:ApiKey from its own
        // User Secrets locally and its own environment in a container. ValidateOnStart inside
        // AddAi means a worker without a key refuses to start, rather than failing every job.
        services.AddAi(configuration);

        // Same SQL Server, same file storage, same job storage as the API. The worker and the
        // API meet only through those three, never by calling each other.
        services.AddInfrastructure(configuration);

        // Small by default. Extraction is CPU- and memory-heavy, and a worker that takes
        // twenty jobs at once is a worker that runs out of memory on twenty large PDFs.
        services.AddIngestionJobServer(
            configuration.GetValue("Ingestion:WorkerCount", DefaultWorkerCount));

        return services;
    }
}

/// <summary>The identity every write by the worker is attributed to.</summary>
internal sealed class ServiceIdentity : ICurrentUser
{
    public string? SubjectId => "system:ingestion-worker";

    /// <summary>
    /// None. The worker serves every tenant, and each job carries its tenant explicitly
    /// (ADR-0009). An ambient tenant here would be the wrong tenant for every job but one.
    /// </summary>
    public Guid? TenantId => null;
}
