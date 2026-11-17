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

    /// <summary>
    /// Where to fetch the metadata, when that is not <c>{Authority}/.well-known/openid-configuration</c>
    /// (Step 14). In Compose the token's issuer is the address the <i>browser</i> uses
    /// (<c>http://127.0.0.1:8080/realms/eka</c>), which inside the API's container is the
    /// container itself. The issuer is still validated against <see cref="Authority"/>'s
    /// metadata; only the address it is read from changes.
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>
    /// Permits plain-HTTP metadata outside Development, but only from a single-label host name
    /// (a Compose service such as <c>keycloak</c>), which resolves on the container network and
    /// nowhere else. Off by default; the release checklist requires it off wherever the
    /// identity provider is not on the same private network (ADR-0034).
    /// </summary>
    public bool AllowHttpMetadataOnPrivateNetwork { get; set; }

    /// <summary>Tolerated clock difference with the issuer. The library default is five minutes,
    /// which keeps an expired token alive that long; one minute covers real clock drift.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Registers JWT bearer authentication and the scope check (Step 12, ADR-0031).</summary>
public static class JwtAuthentication
{
    /// <summary>Claim names as the token carries them. Inbound claim mapping is off (see below).</summary>
    public const string TenantClaim = "tid";
    public const string GroupsClaim = "groups";
    public const string RolesClaim = "roles";

    internal static void AddEkaAuthentication(this WebApplicationBuilder builder)
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

        if (MetadataTransportProblem(jwt, builder.Environment.IsDevelopment()) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        builder.Services.AddOptions<JwtAuthenticationOptions>().BindConfiguration(JwtAuthenticationOptions.SectionName);

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = jwt.Authority;
                options.Audience = jwt.Audience;
                options.RequireHttpsMetadata = jwt.RequireHttpsMetadata;

                if (!string.IsNullOrWhiteSpace(jwt.MetadataAddress))
                {
                    options.MetadataAddress = jwt.MetadataAddress;
                }

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
    /// Why the configured metadata transport is unsafe here, or <c>null</c> when it is fine.
    /// </summary>
    /// <remarks>
    /// Signing keys fetched over plain HTTP can be replaced by anyone on the network path, and
    /// with them every identity. Development may do it (Keycloak on loopback). Elsewhere it is
    /// refused, with one narrow exception: an explicit opt-in, and a metadata host that is a
    /// single label (no dot), i.e. a container-network service name that cannot be resolved,
    /// or intercepted, from outside the host. A pure function so the rule has tests, rather
    /// than being asserted by code review as it was in Step 12.
    /// </remarks>
    public static string? MetadataTransportProblem(JwtAuthenticationOptions jwt, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(jwt);

        if (jwt.RequireHttpsMetadata || isDevelopment)
        {
            return null;
        }

        var setting = $"'{JwtAuthenticationOptions.SectionName}:RequireHttpsMetadata' is false outside Development. ";

        if (!jwt.AllowHttpMetadataOnPrivateNetwork)
        {
            return setting + "Signing keys fetched over plain HTTP can be replaced in transit; only local development "
                + "may do that, or a private container network with AllowHttpMetadataOnPrivateNetwork set.";
        }

        var address = string.IsNullOrWhiteSpace(jwt.MetadataAddress) ? jwt.Authority : jwt.MetadataAddress;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.HostNameType is not UriHostNameType.Dns
            || uri.Host.Contains('.', StringComparison.Ordinal)
            || uri.IsLoopback)
        {
            return setting + $"AllowHttpMetadataOnPrivateNetwork accepts only a single-label service name as the "
                + $"metadata host (such as http://keycloak:8080/...), and '{address}' is not one.";
        }

        return null;
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
