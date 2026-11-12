using EKA.Application.Abstractions;
using EKA.Domain.Common;
using EKA.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace EKA.Infrastructure.Persistence.Repositories;

internal sealed class UserRepository : IUserRepository
{
    private readonly EkaDbContext _context;

    public UserRepository(EkaDbContext context)
    {
        _context = context;
    }

    public Task<User?> GetByIdAsync(
        TenantId tenantId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        _context.Users
            .FirstOrDefaultAsync(
                user => user.TenantId == tenantId && user.Id == userId,
                cancellationToken);

    /// <remarks>
    /// Not tenant-scoped, and cannot be: this runs during sign-in, <i>before</i> the
    /// tenant is known — the user record is what tells us which tenant they belong to.
    /// Safe because <c>ExternalId</c> is globally unique (enforced by
    /// <c>UX_Users_ExternalId</c>) and comes from the identity provider's signed token, not
    /// from caller input.
    /// </remarks>
    public Task<User?> FindByExternalIdAsync(
        string externalId,
        CancellationToken cancellationToken = default) =>
        _context.Users
            .FirstOrDefaultAsync(user => user.ExternalId == externalId, cancellationToken);

    public void Add(User user) => _context.Users.Add(user);
}
