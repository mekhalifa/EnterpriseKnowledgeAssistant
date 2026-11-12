using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EKA.Api.Security;

/// <summary>
/// The authentication scheme used when no identity provider is configured: it
/// authenticates nobody, so every protected endpoint answers <c>401</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not just "leave authentication unconfigured".</b> ASP.NET Core needs a
/// default scheme to exist; without one, <c>RequireAuthenticatedUser()</c> throws
/// <c>InvalidOperationException</c> at request time and the caller gets a <c>500</c>. A 500
/// is the wrong answer to "I did not send credentials" — it reads as a server fault,
/// sends an on-call engineer looking for a bug, and tells the client nothing about what to
/// do differently.
/// </para>
/// <para>
/// So the unconfigured state is made explicit and answers honestly: <c>401 Unauthorized</c>,
/// which is exactly what it is. Step 12 replaces this registration with JWT bearer
/// validation and nothing else in the application changes.
/// </para>
/// </remarks>
internal sealed class NoIdentityProviderHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "NoIdentityProvider";

    public NoIdentityProviderHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <remarks>
    /// <c>NoResult</c>, not <c>Fail</c>. "Fail" means credentials were presented and
    /// rejected, which would be a lie — none were examined. "NoResult" means this scheme
    /// has no opinion, and the authorization layer then issues the challenge.
    /// </remarks>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}
