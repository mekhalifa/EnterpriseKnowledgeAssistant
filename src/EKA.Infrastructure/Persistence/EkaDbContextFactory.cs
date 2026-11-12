using EKA.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;

namespace EKA.Infrastructure.Persistence;

/// <summary>
/// Builds an <see cref="EkaDbContext"/> for the <c>dotnet ef</c> tooling.
/// </summary>
/// <remarks>
/// <para>
/// Without this, <c>dotnet ef</c> would have to boot the API's host to find the context,
/// which drags the whole application's configuration, authentication, and startup
/// validation into a command that only needs the model. A design-time factory keeps
/// migrations working even while <c>EKA.Api</c> is mid-refactor.
/// </para>
/// <para>
/// <b>Note that <c>migrations add</c> never connects to a database.</b> It only builds the
/// model, so the placeholder connection string below is sufficient for authoring
/// migrations and contains nothing secret. Commands that <i>do</i> connect —
/// <c>database update</c>, <c>migrations script</c> against a live schema — require the
/// real value in the <c>EKA_MIGRATIONS_CONNECTION</c> environment variable.
/// </para>
/// </remarks>
internal sealed class EkaDbContextFactory : IDesignTimeDbContextFactory<EkaDbContext>
{
    private const string ConnectionVariable = "EKA_MIGRATIONS_CONNECTION";

    /// <summary>
    /// Deliberately unusable. Enough for model building; guaranteed to fail loudly rather
    /// than quietly connect somewhere unintended if a connecting command is run without
    /// <see cref="ConnectionVariable"/> set.
    /// </summary>
    private const string DesignTimePlaceholder =
        "Server=design-time-placeholder;Database=EnterpriseKnowledgeAssistant;"
        + "Trusted_Connection=False;TrustServerCertificate=True";

    public EkaDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionVariable) ?? DesignTimePlaceholder;

        var options = new DbContextOptionsBuilder<EkaDbContext>()
            .UseSqlServer(
                connectionString,
                sqlServer => sqlServer.MigrationsAssembly(
                    typeof(EkaDbContext).Assembly.GetName().Name))
            .Options;

        // The tooling never saves, so no event is ever raised. A real dispatcher with a
        // null logger is still better than a second no-op implementation to maintain.
        var dispatcher = new LoggingDomainEventDispatcher(
            NullLogger<LoggingDomainEventDispatcher>.Instance);

        return new EkaDbContext(options, dispatcher);
    }
}
