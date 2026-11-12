using System.ComponentModel.DataAnnotations;

namespace EKA.Api.Security;

/// <summary>
/// Settings for the development-only authentication scheme.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> Every endpoint filters on the caller's tenant, and no
/// identity provider is wired until Step 12. Without a stand-in, either the endpoints have
/// no tenant to filter on — which would mean accepting one from the request body, the exact
/// hole Step 3 was built to close — or they cannot be exercised end to end at all.
/// </para>
/// <para>
/// <b>Why a real authentication scheme rather than a hard-coded tenant.</b> A constant
/// would be fewer lines, but it makes the one test most worth writing impossible: two
/// callers from different tenants hitting the same endpoint and proving neither can see the
/// other's documents. It would also leave <c>RequireAuthorization()</c> untested until Step
/// 12, so the day the real provider arrives would be the day the authorization path runs
/// for the first time.
/// </para>
/// <para>
/// <b>How it cannot escape development.</b> Three independent gates, because one is a typo
/// away from nothing:
/// </para>
/// <list type="number">
/// <item><description><see cref="Enabled"/> defaults to <c>false</c>.</description></item>
/// <item><description>
/// <c>Program.cs</c> registers it only when <c>IHostEnvironment.IsDevelopment()</c>.
/// </description></item>
/// <item><description>
/// Startup throws if <see cref="Enabled"/> is true outside Development, so a
/// configuration file copied to staging stops the application instead of silently
/// accepting header identities.
/// </description></item>
/// </list>
/// </remarks>
public sealed class DevelopmentAuthenticationOptions
{
    public const string SectionName = "Authentication:Development";

    /// <summary>The scheme name. Not a secret; it just has to be unambiguous.</summary>
    public const string SchemeName = "DevelopmentHeader";

    /// <summary>Header carrying the tenant id, as a GUID.</summary>
    public const string TenantHeader = "X-EKA-Tenant";

    /// <summary>Header carrying the caller's subject id.</summary>
    public const string SubjectHeader = "X-EKA-Subject";

    /// <summary>
    /// Off unless something deliberately turns it on. A security control whose default is
    /// "on" is a security control that will one day be on somewhere nobody meant.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Tenant used when a request sends no <c>X-EKA-Tenant</c> header, so that opening the
    /// OpenAPI page and clicking a request works without configuring anything.
    /// </summary>
    [Required]
    public Guid DefaultTenantId { get; init; } = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>Subject used when a request sends no <c>X-EKA-Subject</c> header.</summary>
    [Required]
    [MinLength(1)]
    public string DefaultSubjectId { get; init; } = "dev-user";
}
