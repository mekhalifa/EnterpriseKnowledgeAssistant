using EKA.Application.Abstractions;
using EKA.Application.Abstractions.Ingestion;
using EKA.Infrastructure.Extraction;
using EKA.Infrastructure.Jobs;
using EKA.Infrastructure.Storage;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using EKA.Application.Common;
using EKA.Infrastructure.Persistence;
using EKA.Infrastructure.Persistence.Interceptors;
using EKA.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        AddIngestion(services, configuration, connectionString);

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

        // The chain, in order. The first extractor that claims a type wins, so a more
        // specific extractor must be registered before a more general one.
        services.AddSingleton<ITextExtractor, PdfTextExtractor>();
        services.AddSingleton<ITextExtractor, DocxTextExtractor>();
        services.AddSingleton<ITextExtractor, PlainTextExtractor>();

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
