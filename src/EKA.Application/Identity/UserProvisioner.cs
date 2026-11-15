using EKA.Application.Abstractions;
using EKA.Application.Common;
using EKA.Domain.Identity;

namespace EKA.Application.Identity;

/// <summary>
/// Maps an authenticated caller to their local <see cref="User"/>, creating it on first
/// contact — just-in-time provisioning.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a local user row at all,</b> when the identity provider already knows who the
/// caller is: conversations, audit trails and (Step 9) document permissions need a stable
/// key that this database owns. The provider's subject id is that key's <i>source</i>, not
/// the key — an IdP migration rewrites subjects, and every foreign key holding one would
/// have to be rewritten with it.
/// </para>
/// <para>
/// <b>Scoped by tenant, deliberately.</b> The lookup is <c>(TenantId, ExternalId)</c>, and
/// so is the unique index. A subject id is only unique within one identity provider's
/// issuer, and nothing guarantees two tenants use different issuers; under the development
/// scheme they share <c>dev-user</c> outright. A global lookup would hand tenant B's first
/// request tenant A's user row (step-06 §2).
/// </para>
/// <para>
/// <b>Reads never provision.</b> <see cref="FindAsync"/> is for queries: a caller with no
/// local user has no conversations, and a GET that writes a row is a GET that can fail for
/// write reasons. Only commands call <see cref="GetOrProvisionAsync"/>.
/// </para>
/// <para>
/// <b>Commits on its own.</b> The new row is saved before the calling use case continues,
/// so a later failure in that use case (a provider outage, say) does not roll back the
/// account — which is a valid fact on its own. The cost is a known race, recorded in
/// PROGRESS: two concurrent <i>first</i> requests by one person both miss the lookup, and
/// the unique index rejects the second insert as a 500. The index makes the race safe; it
/// does not make it invisible. A retry succeeds.
/// </para>
/// </remarks>
public sealed class UserProvisioner(
    IUserRepository users,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public Task<User?> FindAsync(CallerIdentity caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        return users.FindByExternalIdAsync(caller.TenantId, caller.SubjectId, cancellationToken);
    }

    public async Task<Result<User>> GetOrProvisionAsync(
        CallerIdentity caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var existing = await FindAsync(caller, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            return existing.IsActive ? existing : UserErrors.Deactivated;
        }

        // No silent placeholder email. The domain requires one, and an invented address
        // would be indistinguishable from a real one in every report that lists users.
        if (string.IsNullOrWhiteSpace(caller.Email))
        {
            return UserErrors.IncompleteIdentity;
        }

        var displayName = string.IsNullOrWhiteSpace(caller.DisplayName)
            ? caller.Email
            : caller.DisplayName;

        var user = new User(
            Guid.CreateVersion7(),
            caller.TenantId,
            caller.SubjectId,
            caller.Email,
            displayName,
            timeProvider.GetUtcNow());

        users.Add(user);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return user;
    }
}
