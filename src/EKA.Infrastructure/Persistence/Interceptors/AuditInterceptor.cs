using EKA.Application.Abstractions;
using EKA.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EKA.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Stamps <c>CreatedBy</c> and <c>ModifiedBy</c> on every entity being written.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not the domain's job.</b> The domain has no notion of "who is logged in"
/// — giving entities a current-user dependency would turn them into services with state.
/// So the two <c>By</c> fields are left null by the domain and filled here, at the moment
/// of writing, from <see cref="ICurrentUser"/>.
/// </para>
/// <para>
/// <b>Why timestamps are NOT set here.</b> <c>CreatedAt</c> and <c>ModifiedAt</c> come
/// from the domain, which receives the instant as a parameter
/// (<see href="../../../../docs/adr/0008-deterministic-time-in-domain.md">ADR-0008</see>).
/// Stamping them in an interceptor would make one business operation carry a timestamp
/// different from the one its own domain event recorded, which is exactly the ordering
/// anomaly that makes audit trails untrustworthy.
/// </para>
/// <para>
/// The properties have <c>protected</c> setters, which this class never calls: it writes
/// through EF's change tracker, which reaches the backing field via model metadata. The
/// domain's encapsulation stays intact.
/// </para>
/// </remarks>
public sealed class AuditInterceptor : SaveChangesInterceptor
{
    /// <summary>Recorded when no user is attached — background jobs and migrations.</summary>
    private const string SystemPrincipal = "system";

    private readonly ICurrentUser _currentUser;

    public AuditInterceptor(ICurrentUser currentUser)
    {
        _currentUser = currentUser;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var principal = _currentUser.SubjectId ?? SystemPrincipal;

        foreach (var entry in context.ChangeTracker.Entries<EntityAudit<Guid>>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property(nameof(EntityAudit<Guid>.CreatedBy)).CurrentValue = principal;
                    break;

                case EntityState.Modified:
                    // CreatedBy is deliberately not touched on update: an audit record that
                    // can be rewritten is not an audit record.
                    entry.Property(nameof(EntityAudit<Guid>.ModifiedBy)).CurrentValue = principal;
                    break;

                case EntityState.Detached:
                case EntityState.Unchanged:
                case EntityState.Deleted:
                default:
                    break;
            }
        }
    }
}
