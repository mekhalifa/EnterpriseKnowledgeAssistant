using EKA.Application.Abstractions;
using EKA.Application.Abstractions.Ingestion;
using EKA.Application.Abstractions.Retrieval;
using EKA.Application.Abstractions.Usage;
using EKA.Application.Common;
using EKA.Infrastructure.Extraction;
using EKA.Infrastructure.Jobs;
using EKA.Infrastructure.Persistence;
using EKA.Infrastructure.Persistence.Interceptors;
using EKA.Infrastructure.Persistence.Repositories;
using EKA.Infrastructure.Storage;
using EKA.Infrastructure.Usage;
using EKA.Infrastructure.VectorStore;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Qdrant.Client;

namespace EKA.Infrastructure;

/// <summary>
/// Registers the infrastructure implementations behind the interfaces
/// <c>EKA.Application</c> declares.
/// </summary>
/// <remarks>
/// This is the only place in the system that knows <c>IDocumentRepository</c> means
/// <c>DocumentRepository</c> and that persistence is SQL Server. The composition root in
/// <c>EKA.Api</c> calls one method; swapping the database means changing this file and
/// nothing else.
/// <para>
/// The repository classes are deliberately <c>internal</c> — nothing outside this assembly
/// should be able to name them, which makes the abstraction enforced rather than
/// suggested.
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>Configuration key holding the SQL Server connection string.</summary>
    public const string ConnectionStringName = "EkaDatabase";


    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Fail at startup, not on the first request. A missing connection string
            // discovered by a user is a missing connection string discovered too late.
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. Set it via "
                + "User Secrets locally or an environment variable in containers.");
        }

        services.AddScoped<AuditInterceptor>();
        services.AddScoped<IDomainEventDispatcher, LoggingDomainEventDispatcher>();

        services.AddDbContext<EkaDbContext>((serviceProvider, options) =>
        {
            options.UseSqlServer(connectionString, sqlServer =>
            {
                // Migrations live in this assembly, alongside the DbContext they describe,
                // even though `dotnet ef` is driven from EKA.Api as the startup project.
                sqlServer.MigrationsAssembly(typeof(EkaDbContext).Assembly.GetName().Name);

                // SQL Server in a container, and any managed SQL service, produces
                // transient faults — a failover or a resource-governor throttle. Without
                // retries those surface as random request failures that look like bugs.
                sqlServer.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null);
            });

            options.AddInterceptors(serviceProvider.GetRequiredService<AuditInterceptor>());
        });

        // The same scoped DbContext instance serves as the unit of work, so a use case's
        // repository writes and its commit are one transaction.
        services.AddScoped<IUnitOfWork>(
            serviceProvider => serviceProvider.GetRequiredService<EkaDbContext>());

        // The readiness probe. Registered here because this is the layer that knows the
        // application talks to SQL Server at all — EKA.Api asks "are you ready", not "can
        // you reach the database". AddDbContextCheck opens a connection and runs
        // CanConnectAsync, so it fails on a dead server, a wrong password and an
        // unreachable network alike, which is exactly the set of faults that should take
        // an instance out of the load balancer.
        //
        // The tag is what /health/ready filters on; an untagged check would run on neither
        // probe and quietly never execute.
        services.AddHealthChecks()
            .AddDbContextCheck<EkaDbContext>(name: "sql-server", tags: [HealthTags.Ready]);

        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IPromptTemplateRepository, PromptTemplateRepository>();
        services.AddScoped<IAIProviderRepository, AIProviderRepository>();
        services.AddScoped<IChunkReader, ChunkReader>();

        // Step 12: per-tenant token usage. Singleton over the connection string, with its own
        // short-lived connections (see SqlTokenUsageLedger for why not the DbContext).
        services.AddSingleton<ITokenUsageLedger>(new SqlTokenUsageLedger(connectionString));

        AddIngestion(services, configuration, connectionString);
        AddVectorStore(services, configuration);

        return services;
    }

    /// <summary>
    /// Storage, extraction and the job client. Registered in every host, because the API
    /// enqueues and stores, and the worker extracts and stores.
    /// </summary>
    private static void AddIngestion(IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        services.AddOptions<FileStorageOptions>().Bind(configuration.GetSection(FileStorageOptions.SectionName));
        services.AddSingleton<IFileStorage, LocalFileStorage>();

        services.AddTextExtraction();
        services.AddOptions<ExtractionLimits>().Bind(configuration.GetSection(ExtractionLimits.SectionName));

        // Step 12: the scanning port. Step 14: ClamAV behind it, chosen by configuration
        // (MalwareScanning:Scanner). None stays the default so a laptop needs no clamd, and it
        // says so in the log once per process; the deployment sets ClamAv (ADR-0034).
        services.AddOptions<MalwareScanningOptions>()
            .Bind(configuration.GetSection(MalwareScanningOptions.SectionName))
            .Validate(
                scanning => scanning.Scanner is not MalwareScannerKind.ClamAv
                    || (!string.IsNullOrWhiteSpace(scanning.Host) && scanning.Port is > 0 and < 65536 && scanning.Timeout > TimeSpan.Zero),
                $"'{MalwareScanningOptions.SectionName}' with Scanner=ClamAv needs Host, a Port in 1-65535 and a positive Timeout.")
            .ValidateOnStart();

        var scanner = configuration.GetSection(MalwareScanningOptions.SectionName).Get<MalwareScanningOptions>()?.Scanner
            ?? MalwareScannerKind.None;

        if (scanner is MalwareScannerKind.ClamAv)
        {
            services.AddSingleton<IMalwareScanner, ClamAvMalwareScanner>();
        }
        else
        {
            services.AddSingleton<IMalwareScanner, NoMalwareScanner>();
        }

        // The API enqueues IngestionJobs by type, in an expression Hangfire serializes, and
        // never resolves it. Only the worker does, so IngestionJobs is registered by
        // AddIngestionJobServer.
        services.AddScoped<IIngestionQueue, HangfireIngestionQueue>();

        // Hangfire's own tables live in the same database, under their own schema, created on
        // first use by whichever process gets there first (Hangfire takes a lock for it). That
        // is why the API never touches job storage at startup: storage is built lazily, on the
        // first enqueue, and HangfireIngestionQueue absorbs a failure there (ADR-0020).
        services.AddHangfire(hangfire => hangfire
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(
                // Microsoft.Data.SqlClient, explicitly: without a factory Hangfire falls back to
                // the deprecated System.Data.SqlClient.
                () => new SqlConnection(connectionString),
                new SqlServerStorageOptions
                {
                    SchemaName = "HangFire",
                    PrepareSchemaIfNecessary = true,
                    // How often an idle worker checks for new jobs. One second keeps an upload's
                    // wait for extraction short, at the cost of one cheap query per second.
                    QueuePollInterval = TimeSpan.FromSeconds(1),
                }));
    }

    /// <summary>
    /// The extractor chain alone: no database, no storage, no jobs.
    /// </summary>
    /// <remarks>
    /// Public since Step 11, so the evaluation harness reads its corpus through exactly the
    /// extractors production uses, without making them public or standing up SQL Server.
    /// <see cref="AddInfrastructure"/> calls it too, so there is one list, not two.
    /// </remarks>
    public static IServiceCollection AddTextExtraction(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Step 12 limits. Defaults here; AddInfrastructure binds the Extraction section over them.
        services.AddOptions<ExtractionLimits>();

        // The chain, in order. The first extractor that claims a type wins, so a more
        // specific extractor must be registered before a more general one.
        services.AddSingleton<ITextExtractor, PdfTextExtractor>();
        services.AddSingleton<ITextExtractor, DocxTextExtractor>();
        services.AddSingleton<ITextExtractor, PlainTextExtractor>();

        return services;
    }

    /// <summary>
    /// The Qdrant adapter. Registered in both hosts: the worker writes vectors, and the API
    /// deletes them with their document (and searches them, from Step 9).
    /// </summary>
    private static void AddVectorStore(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<VectorStoreOptions>().Bind(configuration.GetSection(VectorStoreOptions.SectionName));

        // One client per process. It owns a gRPC channel (one HTTP/2 connection multiplexing
        // every call), which is built to be shared and is expensive to create. Constructing it
        // does not connect, so a host starts even when Qdrant is down, and the first call says so.
        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<VectorStoreOptions>>().Value;
            return new QdrantClient(
                options.Endpoint,
                string.IsNullOrWhiteSpace(options.ApiKey) ? null : options.ApiKey);
        });

        // Singleton: it remembers, per process, that the collection has been checked.
        services.AddSingleton<IVectorStore, QdrantVectorStore>();

        // Step 14: Qdrant joins SQL Server on the readiness probe. A timeout, because a probe
        // that hangs is worse than one that fails: the orchestrator learns nothing either way.
        services.AddHealthChecks()
            .AddCheck<QdrantHealthCheck>("qdrant", tags: [HealthTags.Ready], timeout: TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The worker's half: a Hangfire server processing the ingestion queue, and the recurring
    /// stalled-document sweep. Only <c>EKA.Ingestion.Worker</c> calls this. An API that also
    /// ran a server would bring back exactly the contention ADR-0004 moved out of it.
    /// </summary>
    public static IServiceCollection AddIngestionJobServer(this IServiceCollection services, int workerCount)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IngestionJobs>();

        services.AddHangfireServer(options =>
        {
            options.Queues = [IngestionJobs.Queue, "default"];
            options.WorkerCount = workerCount;
        });

        services.AddHostedService<RecurringJobRegistration>();

        return services;
    }
}

/// <summary>
/// Declares the recurring sweep at worker startup. <c>AddOrUpdate</c> is idempotent, so every
/// worker instance can run this safely: there is one recurring job, not one per instance.
/// </summary>
internal sealed class RecurringJobRegistration(IRecurringJobManager recurringJobs) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        recurringJobs.AddOrUpdate<IngestionJobs>(
            IngestionJobs.RecoverStalledJobId,
            jobs => jobs.RecoverStalledAsync(CancellationToken.None),
            "*/5 * * * *");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
