using EKA.Application.Abstractions;
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

        return services;
    }
}
