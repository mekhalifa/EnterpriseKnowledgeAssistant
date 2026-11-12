using System.Security.Claims;
using EKA.Application.Abstractions;

namespace EKA.Api.Security;

/// <summary>
/// Reads the caller's identity from the current HTTP request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Until Step 12 wires an identity provider, both properties return null for every
/// request.</b> That is not a bug and it does not fail: the audit interceptor records
/// <c>"system"</c> when no subject is present. It does mean audit columns carry no real
/// attribution yet, which is listed as a known limitation in docs/PROGRESS.md.
/// </para>
/// <para>
/// The claim types are the ones a standard OIDC provider issues. <c>sub</c> is checked as
/// well as <c>ClaimTypes.NameIdentifier</c> because ASP.NET Core's JWT handler maps
/// <c>sub</c> to the latter by default, but that mapping is frequently turned off — and
/// when it is, reading only one of the two silently yields null.
/// </para>
/// </remarks>
internal sealed class HttpContextCurrentUser : ICurrentUser
{
    /// <summary>Claim carrying the tenant id. Entra ID issues <c>tid</c>.</summary>
    private const string TenantClaim = "tid";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string? SubjectId
    {
        get
        {
            var principal = _httpContextAccessor.HttpContext?.User;

            return principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? principal?.FindFirst("sub")?.Value;
        }
    }

    public Guid? TenantId
    {
        get
        {
            var raw = _httpContextAccessor.HttpContext?.User.FindFirst(TenantClaim)?.Value;

            return Guid.TryParse(raw, out var tenantId) ? tenantId : null;
        }
    }
}
