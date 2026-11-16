using EKA.Domain.Common;

namespace EKA.Domain.Access;

/// <summary>
/// Who may read a document: everyone in its tenant, or a named list of users and groups.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never empty.</b> A document nobody may read cannot be read, renamed, deleted, or
/// given back to anyone. It would be a row that exists only to take up space. So an empty
/// list is refused here instead of being treated as a valid "locked" state.
/// </para>
/// <para>
/// <b>"Tenant" stands alone.</b> <c>tenant</c> already includes every user and group, so
/// <c>[tenant, user:alice]</c> means exactly what <c>[tenant]</c> means. Accepting it would
/// store a list that looks restricted and is not. The next person to remove <c>tenant</c> from
/// it, expecting to "narrow it a bit", would cut access off for everyone but Alice. Refused.
/// </para>
/// <para>
/// The principals are kept distinct and sorted by key, so two equal policies have the same
/// stored form and the same vector payload, whatever order a client sent them in.
/// </para>
/// </remarks>
public sealed class DocumentAccess
{
    /// <summary>
    /// More grants than this belong in a group. The bound also keeps the vector payload and
    /// the filter on each search small.
    /// </summary>
    public const int MaxPrincipals = 50;

    private DocumentAccess(IReadOnlyList<AccessPrincipal> principals) => Principals = principals;

    /// <summary>Everyone in the tenant. The default for a new document.</summary>
    public static DocumentAccess TenantWide { get; } = new([AccessPrincipal.Tenant]);

    /// <summary>Distinct, sorted by <see cref="AccessPrincipal.Key"/> (ordinal).</summary>
    public IReadOnlyList<AccessPrincipal> Principals { get; }

    public bool IsTenantWide => Principals.Count == 1 && Principals[0].Kind is AccessPrincipalKind.Tenant;

    /// <summary>The canonical keys, in <see cref="Principals"/> order.</summary>
    public IReadOnlyList<string> Keys => [.. Principals.Select(principal => principal.Key)];

    /// <exception cref="DomainException">Empty, too many, or <c>tenant</c> mixed with others.</exception>
    public static DocumentAccess Of(IEnumerable<AccessPrincipal> principals)
    {
        ArgumentNullException.ThrowIfNull(principals);

        var distinct = principals
            .DistinctBy(principal => principal.Key, StringComparer.Ordinal)
            .OrderBy(principal => principal.Key, StringComparer.Ordinal)
            .ToList();

        if (distinct.Count == 0)
        {
            throw new DomainException("A document must be readable by someone: grant 'tenant', or at least one user or group.");
        }

        if (distinct.Count > MaxPrincipals)
        {
            throw new DomainException(
                $"A document can name at most {MaxPrincipals} users and groups; got {distinct.Count}. Grant a group instead.");
        }

        if (distinct.Count > 1 && distinct.Exists(principal => principal.Kind is AccessPrincipalKind.Tenant))
        {
            throw new DomainException(
                "'tenant' already grants everyone in the tenant, so it cannot be combined with named users or groups.");
        }

        return distinct.Count == 1 && distinct[0].Kind is AccessPrincipalKind.Tenant
            ? TenantWide
            : new DocumentAccess(distinct);
    }

    /// <summary>Reads stored keys back. Throws if any is not canonical: stored data is trusted to be ours.</summary>
    public static DocumentAccess FromKeys(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        return Of(keys.Select(AccessPrincipal.Parse));
    }

    /// <summary>
    /// True when the caller holds at least one principal this policy grants. Says nothing about
    /// the tenant: <see cref="AccessScope"/> and the document check that separately.
    /// </summary>
    public bool Grants(AccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return Principals.Any(principal => scope.Holds(principal));
    }

    public override string ToString() => string.Join(", ", Keys);
}
