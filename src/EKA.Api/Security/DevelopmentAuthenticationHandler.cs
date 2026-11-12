using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EKA.Api.Security;

/// <summary>
/// Builds a <see cref="ClaimsPrincipal"/> from request headers. Development only.
/// </summary>
/// <remarks>
/// <para>
/// This is a stand-in for the OIDC bearer handler that Step 12 configures. It issues the
/// same two claims a real provider would — <c>sub</c> and <c>tid</c> — which is the point:
/// <see cref="HttpContextCurrentUser"/>, the repositories, the audit interceptor and every
/// endpoint read the principal, not this class. When the real provider replaces it, one
/// line in <c>Program.cs</c> changes and nothing downstream notices.
/// </para>
/// <para>
/// <b>This authenticates nothing.</b> Anyone who can reach the port can claim any tenant.
/// That is acceptable only because it is bound to Development by the three gates described
/// on <see cref="DevelopmentAuthenticationOptions"/>, and it is why the class is
/// <c>internal</c>, lives next to its options, and says so in its own name.
/// </para>
/// </remarks>
internal sealed class DevelopmentAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly DevelopmentAuthenticationOptions _development;

    public DevelopmentAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<DevelopmentAuthenticationOptions> development)
        : base(options, logger, encoder) =>
        _development = development.Value;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subject = Request.Headers[DevelopmentAuthenticationOptions.SubjectHeader].ToString();

        if (string.IsNullOrWhiteSpace(subject))
        {
            subject = _development.DefaultSubjectId;
        }

        var rawTenant = Request.Headers[DevelopmentAuthenticationOptions.TenantHeader].ToString();

        // A header that is present but unparseable is a client error, not a cue to fall
        // back. Silently substituting the default tenant would make a typo in a test look
        // like a passing cross-tenant check.
        if (!string.IsNullOrWhiteSpace(rawTenant) && !Guid.TryParse(rawTenant, out _))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                $"'{DevelopmentAuthenticationOptions.TenantHeader}' must be a GUID."));
        }

        var tenantId = Guid.TryParse(rawTenant, out var parsed)
            ? parsed
            : _development.DefaultTenantId;

        var identity = new ClaimsIdentity(
            [
                // Both spellings, for the same reason HttpContextCurrentUser reads both:
                // whether a real JWT handler maps `sub` to NameIdentifier depends on a
                // setting teams routinely turn off.
                new Claim(ClaimTypes.NameIdentifier, subject),
                new Claim("sub", subject),
                new Claim("tid", tenantId.ToString()),
            ],
            DevelopmentAuthenticationOptions.SchemeName);

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            DevelopmentAuthenticationOptions.SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
