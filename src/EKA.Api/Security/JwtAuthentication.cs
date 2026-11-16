using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace EKA.Api.Security;

/// <summary>The <c>Authentication:Jwt</c> section: which issuer to trust, for which audience.</summary>
/// <remarks>
/// <para>
/// <b>Provider-agnostic by construction</b> (ADR-0031). Keycloak locally, Entra ID in production:
/// both publish OpenID Connect metadata at <c>{Authority}/.well-known/openid-configuration</c>,
/// with the signing keys (JWKS) and the issuer name. The application names the authority and
/// its own audience, and everything else is read from there. Changing provider is
/// configuration.
/// </para>
/// <para>
/// No secret lives here. Validating a token needs the issuer's <i>public</i> keys only.
/// </para>
/// </remarks>
public sealed class JwtAuthenticationOptions
{
    public const string SectionName = "Authentication:Jwt";

    /// <summary>The issuer, e.g. <c>https://login.microsoftonline.com/{tenant}/v2.0</c> or
    /// <c>http://127.0.0.1:8080/realms/eka</c>. Its metadata supplies the keys.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>This API's identifier, which must appear in the token's <c>aud</c>. A token issued
    /// for another application of the same identity provider is refused.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// The scope a token must carry (<c>scope</c> for Keycloak, <c>scp</c> for Entra ID). A user
    /// signed in to some other application of the same identity provider, whose token happens to
    /// name this audience, still needs to have been granted access to <i>this</i> API.
    /// </summary>
    public string RequiredScope { get; set; } = "eka.access";

    /// <summary>The role (in the <c>roles</c> claim) that may manage any document in its tenant.</summary>
    public string AdminRole { get; set; } = "eka.admin";

    /// <summary>
    /// Fetch the issuer's metadata over HTTPS only. True unless explicitly turned off, and
    /// turning it off outside Development stops startup: keys fetched over plain HTTP can be
    /// swapped by anyone on the path, and with them every identity.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Tolerated clock difference with the issuer. The library default is five minutes,
    /// which keeps an expired token alive that long; one minute covers real clock drift.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Registers JWT bearer authentication and the scope check (Step 12, ADR-0031).</summary>
internal static class JwtAuthentication
{
    /// <summary>Claim names as the token carries them. Inbound claim mapping is off (see below).</summary>
    public const string TenantClaim = "tid";
    public const string GroupsClaim = "groups";
    public const string RolesClaim = "roles";

    public static void AddEkaAuthentication(this WebApplicationBuilder builder)
    {
        var jwt = builder.Configuration.GetSection(JwtAuthenticationOptions.SectionName).Get<JwtAuthenticationOptions>()
            ?? new JwtAuthenticationOptions();

        // Refuse to start rather than run with authentication that accepts nothing (or, worse,
        // whatever an unvalidated configuration happens to accept).
        if (string.IsNullOrWhiteSpace(jwt.Authority) || string.IsNullOrWhiteSpace(jwt.Audience))
        {
            throw new InvalidOperationException(
                $"'{JwtAuthenticationOptions.SectionName}:Authority' and ':Audience' must be configured. Locally they point "
                + "at the Keycloak realm in deploy/docker-compose.yml (see appsettings.Development.json).");
        }

        if (!jwt.RequireHttpsMetadata && !builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"'{JwtAuthenticationOptions.SectionName}:RequireHttpsMetadata' is false in '{builder.Environment.EnvironmentName}'. "
                + "Signing keys fetched over plain HTTP can be replaced in transit; only local development may do that.");
        }

        builder.Services.AddOptions<JwtAuthenticationOptions>().BindConfiguration(JwtAuthenticationOptions.SectionName);

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = jwt.Authority;
                options.Audience = jwt.Audience;
                options.RequireHttpsMetadata = jwt.RequireHttpsMetadata;

                // Keep claim names as issued (sub, tid, groups, roles, scope). The default mapping
                // renames them to long WS-Fed URIs; code that then looks for "sub" finds nothing,
                // which is how authorization quietly stops working after a library upgrade.
                options.MapInboundClaims = false;

                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidateAudience = true;
                options.TokenValidationParameters.ValidateLifetime = true;
                options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                options.TokenValidationParameters.RequireExpirationTime = true;
                options.TokenValidationParameters.RequireSignedTokens = true;
                options.TokenValidationParameters.ClockSkew = jwt.ClockSkew;
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = RolesClaim;
            });
    }

    /// <summary>
    /// True when the token grants <paramref name="scope"/>: in <c>scope</c> (Keycloak, RFC 8693)
    /// or <c>scp</c> (Entra ID), each a space-separated list.
    /// </summary>
    public static bool HasScope(ClaimsPrincipal user, string scope)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.FindAll("scope").Concat(user.FindAll("scp"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
    }
}
