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
    /// </summary>
    Task<User?> FindByExternalIdAsync(
        string externalId,
        CancellationToken cancellationToken = default);

    void Add(User user);
}
