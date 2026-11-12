using EKA.Application.Abstractions;
using EKA.Domain.Common;

namespace EKA.Api.Security;

/// <summary>
/// The authorization policy every tenant-scoped endpoint group requires, and the one way
/// to turn an authenticated caller into a <see cref="TenantId"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a policy and not an <c>[Authorize]</c>-equivalent on each endpoint.</b> A
/// forgotten attribute on one endpoint out of twenty is invisible in review and complete as
/// a breach. The policy is applied once to the route group, so a new endpoint added to that
/// group is protected by existing on the right line.
/// </para>
/// <para>
/// <b>Why the policy demands the tenant claim, not just authentication.</b> Every query in
/// this system filters on a tenant. A token that authenticates a human but carries no
/// tenant has nothing to filter by, and the only safe answers are "deny" or "invent one".
/// Denying at the policy produces a 403 before any handler runs; inventing one is how
/// cross-tenant reads happen.
/// </para>
/// </remarks>
internal static class TenantPolicy
{
    public const string Name = "TenantScoped";

    /// <summary>The claim a tenant id arrives in. Entra ID issues <c>tid</c>.</summary>
    public const string TenantClaim = "tid";

    /// <summary>
    /// The caller's tenant, for an endpoint already protected by <see cref="Name"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The claim is absent or unparseable. This is deliberately a 500, not a 403: the
    /// policy has already guaranteed the claim exists, so reaching here means the policy
    /// and this method disagree — a bug in our wiring, which should be loud. Returning a
    /// quiet 403 instead would hide a broken authorization policy behind what looks like a
    /// permissions problem, and someone would spend an afternoon on the wrong question.
    /// </exception>
    public static TenantId RequireTenantId(this ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(currentUser);

        var tenantId = currentUser.TenantId
            ?? throw new InvalidOperationException(
                $"The authenticated caller has no usable '{TenantClaim}' claim, but the "
                + $"'{Name}' policy should have rejected the request before this point.");

        return new TenantId(tenantId);
    }
}
