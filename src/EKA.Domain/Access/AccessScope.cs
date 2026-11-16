using EKA.Domain.Common;

namespace EKA.Domain.Access;

/// <summary>
/// Everything a caller may read is decided by these facts: their tenant and the principals
/// they hold (everyone-in-the-tenant, themselves, their groups).
/// </summary>
/// <remarks>
/// <para>
/// <b>Built only from an authenticated identity</b>, by the API from token claims (ADR-0014).
/// It is the argument every read of document content takes, so a caller's access cannot be
/// "forgotten" on one path. A method that needs it cannot be called without one.
/// </para>
/// <para>
/// <b>Always holds <see cref="AccessPrincipal.Tenant"/> and the caller's own user.</b> The
/// factory adds both, so "a tenant-wide document" and "a document granted to me" cannot be
/// missed by a scope somebody assembled by hand and got wrong.
/// </para>
/// </remarks>
public sealed class AccessScope
{
    private readonly HashSet<string> _keys;

    private AccessScope(TenantId tenantId, IReadOnlyList<AccessPrincipal> principals)
    {
        TenantId = tenantId;
        Principals = principals;
        _keys = new HashSet<string>(principals.Select(principal => principal.Key), StringComparer.Ordinal);
    }

    public TenantId TenantId { get; }

    /// <summary>Tenant, the user, then the groups, distinct.</summary>
    public IReadOnlyList<AccessPrincipal> Principals { get; }

    /// <summary>The canonical keys of <see cref="Principals"/>, for a store's filter.</summary>
    public IReadOnlyList<string> Keys => [.. Principals.Select(principal => principal.Key)];

    /// <param name="tenantId">The caller's tenant, from the token.</param>
    /// <param name="subjectId">The caller's subject, from the token.</param>
    /// <param name="groupIds">The caller's groups, from the token. Blank entries are ignored,
    /// because identity providers emit them more often than one would hope.</param>
    /// <exception cref="DomainException">An empty tenant, or an unusable subject or group id.</exception>
    public static AccessScope For(TenantId tenantId, string subjectId, IEnumerable<string>? groupIds = null)
    {
        if (tenantId.IsEmpty)
        {
            throw new DomainException("An access scope must belong to a tenant.");
        }

        var principals = new List<AccessPrincipal> { AccessPrincipal.Tenant, AccessPrincipal.User(subjectId) };

        principals.AddRange((groupIds ?? [])
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.Ordinal)
            .Select(AccessPrincipal.Group));

        return new AccessScope(tenantId, principals);
    }

    public bool Holds(AccessPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return _keys.Contains(principal.Key);
    }

    public override string ToString() => $"{TenantId} [{string.Join(", ", _keys)}]";
}
