using EKA.Domain.Common;
using EKA.Domain.Identity;

namespace EKA.Application.Abstractions;

/// <summary>
/// Collection-like access to the <see cref="User"/> aggregate.
/// </summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(
        TenantId tenantId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks a user up by the identity provider's subject claim. This is the sign-in path:
    /// the token carries a subject, and the local projection is found or created from it.
    /// Deliberately not by email — see <see cref="User.ExternalId"/>.
    /// <para>
    /// <b>Tenant-scoped since Step 6.</b> It took only a subject id until then, which is
    /// unique per identity-provider issuer, not per system: two tenants can present the same
    /// subject, and under the development scheme every tenant does. The global lookup
    /// would have resolved tenant B's caller to tenant A's row. The unique index changed
    /// from <c>(ExternalId)</c> to <c>(TenantId, ExternalId)</c> in the same commit.
    /// </para>
    /// </summary>
    Task<User?> FindByExternalIdAsync(
        TenantId tenantId,
        string externalId,
        CancellationToken cancellationToken = default);

    void Add(User user);
}
