using EKA.Application.Abstractions;
using EKA.Domain.Common;
using EKA.Domain.Conversations;
using EKA.Domain.Identity;
using EKA.Domain.Ingestion;
using EKA.Domain.Prompting;
using EKA.Domain.Providers;
using EKA.Domain.Retrieval;
using Microsoft.EntityFrameworkCore;

namespace EKA.Infrastructure.Persistence;

/// <summary>
/// The unit of work over SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// Implements <see cref="IUnitOfWork"/>, which is declared in <c>EKA.Application</c>. Use
/// cases depend on that interface and never see this class — the dependency arrow points
/// inward even though the implementation lives further out.
/// </para>
/// <para>
/// EF Core's <c>DbContext</c> <i>is</i> a Unit of Work: it tracks changes and writes them
/// in one transaction per <see cref="SaveChangesAsync"/>. There is no need to build that
/// machinery, only to expose it narrowly.
/// </para>
/// </remarks>
public sealed class EkaDbContext : DbContext, IUnitOfWork
{
    private readonly IDomainEventDispatcher _domainEventDispatcher;

    public EkaDbContext(
        DbContextOptions<EkaDbContext> options,
        IDomainEventDispatcher domainEventDispatcher)
        : base(options)
    {
        _domainEventDispatcher = domainEventDispatcher;
    }

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();

    public DbSet<AIProvider> AIProviders => Set<AIProvider>();

    public DbSet<User> Users => Set<User>();

    /// <summary>
    /// Commits the transaction, then dispatches whatever domain events the tracked
    /// aggregates raised.
    /// </summary>
    /// <remarks>
    /// Order matters and is the whole point. Events are <b>collected</b> before the write
    /// (while the entities are still tracked and their event lists populated) and
    /// <b>dispatched</b> after it succeeds. Dispatching first would let a handler notify a
    /// user that their document is searchable, and then have the transaction roll back.
    /// <para>
    /// The event lists are cleared before dispatch so that a handler which itself calls
    /// <see cref="SaveChangesAsync"/> cannot re-dispatch the same events in an infinite
    /// loop.
    /// </para>
    /// </remarks>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var aggregates = ChangeTracker
            .Entries<EntityAudit<Guid>>()
            .Select(entry => entry.Entity)
            .Where(entity => entity.DomainEvents.Count > 0)
            .ToList();

        var domainEvents = aggregates
            .SelectMany(aggregate => aggregate.DomainEvents)
            .ToList();

        var written = await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var aggregate in aggregates)
        {
            aggregate.ClearDomainEvents();
        }

        if (domainEvents.Count > 0)
        {
            await _domainEventDispatcher
                .DispatchAsync(domainEvents, cancellationToken)
                .ConfigureAwait(false);
        }

        return written;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Picks up every IEntityTypeConfiguration<T> in this assembly, so adding an entity
        // means adding a configuration file — never editing this method.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EkaDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Registers value-object conversions by CLR type, once, for the whole model.
    /// </summary>
    /// <remarks>
    /// Doing this here rather than per entity configuration means a new entity using
    /// <c>TenantId</c> or <c>TokenCount</c> is mapped correctly automatically. Per-property
    /// registration is how one property somewhere ends up unconverted, which surfaces as a
    /// confusing "cannot map type" error months later.
    /// <para>
    /// Lengths are declared alongside: a converted string with no <c>HaveMaxLength</c>
    /// becomes <c>nvarchar(max)</c>, which cannot be indexed and silently costs
    /// performance.
    /// </para>
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<TenantId>()
            .HaveConversion<ValueObjectConverters.TenantIdConverter>();

        configurationBuilder.Properties<DocumentTitle>()
            .HaveConversion<ValueObjectConverters.DocumentTitleConverter>()
            .HaveMaxLength(DocumentTitle.MaxLength);

        configurationBuilder.Properties<ChunkIndex>()
            .HaveConversion<ValueObjectConverters.ChunkIndexConverter>();

        configurationBuilder.Properties<TokenCount>()
            .HaveConversion<ValueObjectConverters.TokenCountConverter>();

        configurationBuilder.Properties<AuthorityScore>()
            .HaveConversion<ValueObjectConverters.AuthorityScoreConverter>();

        configurationBuilder.Properties<SimilarityScore>()
            .HaveConversion<ValueObjectConverters.SimilarityScoreConverter>();

        base.ConfigureConventions(configurationBuilder);
    }
}
