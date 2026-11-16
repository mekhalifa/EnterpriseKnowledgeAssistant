using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EKA.IntegrationTests.Api;

/// <summary>
/// A stand-in identity provider for the tests: it signs access tokens with a key the API under
/// test is told to trust.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the key changes.</b> The API validates these tokens with the production
/// configuration: issuer, audience, lifetime, signature, required scope, tenant claim. The
/// factory replaces the issuer's published metadata with this issuer name and this key, so no
/// network call is made. Every negative case (expired, wrong audience, wrong issuer, foreign
/// key, unsigned) therefore exercises the same code that protects production.
/// </para>
/// <para>
/// No production code knows this key exists. There is no "test mode" switch in the API that a
/// misconfiguration could leave on.
/// </para>
/// </remarks>
internal static class TestTokens
{
    public const string Issuer = "https://issuer.eka.test/realms/eka";
    public const string Audience = "eka-api";
    public const string Scope = "eka.access";

    /// <summary>The key the API trusts.</summary>
    public static RsaSecurityKey SigningKey { get; } = NewKey("eka-test-key");

    /// <summary>A key the API has never heard of: what a forger would sign with.</summary>
    public static RsaSecurityKey ForeignKey { get; } = NewKey("forger-key");

    public static string For(
        Guid tenantId,
        string subject = "test-user",
        IReadOnlyList<string>? groups = null,
        IReadOnlyList<string>? roles = null,
        string? scope = Scope,
        bool includeTenant = true,
        TimeSpan? lifetime = null,
        string issuer = Issuer,
        string audience = Audience,
        SecurityKey? key = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["email"] = $"{subject}@eka.test",
            ["name"] = subject,
        };

        if (includeTenant)
        {
            claims["tid"] = tenantId.ToString();
        }

        if (scope is not null)
        {
            claims["scope"] = $"openid profile {scope}";
        }

        if (groups is { Count: > 0 })
        {
            claims["groups"] = groups.ToArray();
        }

        if (roles is { Count: > 0 })
        {
            claims["roles"] = roles.ToArray();
        }

        var now = DateTime.UtcNow;
        var validFor = lifetime ?? TimeSpan.FromMinutes(5);

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            // A negative lifetime produces a token that expired before it was issued.
            IssuedAt = validFor < TimeSpan.Zero ? now + validFor - TimeSpan.FromMinutes(1) : now,
            NotBefore = validFor < TimeSpan.Zero ? now + validFor - TimeSpan.FromMinutes(1) : now,
            Expires = now + validFor,
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>An unsigned token ("alg": "none"): must never be accepted.</summary>
    public static string Unsigned(Guid tenantId) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = new Dictionary<string, object> { ["sub"] = "forger", ["tid"] = tenantId.ToString(), ["scope"] = Scope },
            Expires = DateTime.UtcNow.AddMinutes(5),
        });

    private static RsaSecurityKey NewKey(string keyId) => new(RSA.Create(2048)) { KeyId = keyId };
}
