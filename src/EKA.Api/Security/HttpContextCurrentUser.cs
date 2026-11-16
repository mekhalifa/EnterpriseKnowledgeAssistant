using System.Security.Claims;
using EKA.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace EKA.Api.Security;

/// <summary>
/// Reads the caller's identity from the validated access token of the current request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Claim names as issued.</b> Inbound claim mapping is off (<see cref="JwtAuthentication"/>),
/// so <c>sub</c>, <c>tid</c>, <c>groups</c> and <c>roles</c> arrive under those names, from
/// Keycloak and Entra ID alike. <c>ClaimTypes.NameIdentifier</c> is still read as a fallback, in
/// case mapping is ever switched back on: reading only one of the two would then silently
/// yield null.
/// </para>
/// <para>
/// Everything here comes from a token whose signature, issuer, audience and lifetime were
/// checked before this class runs. Nothing is read from headers, the query or the body.
/// </para>
/// </remarks>
internal sealed class HttpContextCurrentUser(
    IHttpContextAccessor httpContextAccessor,
    IOptions<JwtAuthenticationOptions> jwt) : ICurrentUser
{
    public string? SubjectId => Claim(ClaimTypes.NameIdentifier, "sub");

    public Guid? TenantId =>
        Guid.TryParse(Claim(JwtAuthentication.TenantClaim), out var tenantId) ? tenantId : null;

    /// <summary>
    /// <c>email</c> as OIDC providers send it, or the mapped <see cref="ClaimTypes.Email"/>.
    /// <c>preferred_username</c> is the fallback because Entra ID often omits <c>email</c>
    /// for work accounts and puts the UPN there instead.
    /// </summary>
    public string? Email => Claim(ClaimTypes.Email, "email") ?? Claim("preferred_username");

    public string? DisplayName => Claim(ClaimTypes.Name, "name") ?? Claim("preferred_username");

    /// <summary>
    /// Every <c>groups</c> claim: one per group id (Entra ID object ids; Keycloak group names
    /// with the full-path option off).
    /// </summary>
    /// <remarks>
    /// Entra ID stops listing groups past a limit (200 in a JWT) and sends a pointer to Microsoft
    /// Graph instead (the "groups overage" claim). Resolving it needs a Graph call with its own
    /// permission, which this application does not have. A caller in that situation gets no
    /// groups here, so sees less, never more: fail closed. Recorded in the threat model.
    /// </remarks>
    public IReadOnlyList<string> Groups => Values(JwtAuthentication.GroupsClaim);

    /// <summary>The configured administrator role is among the token's <c>roles</c>.</summary>
    public bool IsAdministrator => Values(JwtAuthentication.RolesClaim).Contains(jwt.Value.AdminRole, StringComparer.Ordinal);

    private IReadOnlyList<string> Values(string type) =>
        httpContextAccessor.HttpContext?.User.FindAll(type).Select(claim => claim.Value).ToList() ?? [];

    private string? Claim(params string[] types)
    {
        var principal = httpContextAccessor.HttpContext?.User;

        return types
            .Select(type => principal?.FindFirst(type)?.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
