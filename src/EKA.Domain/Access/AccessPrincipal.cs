using EKA.Domain.Common;

namespace EKA.Domain.Access;

/// <summary>What kind of party an access grant names.</summary>
public enum AccessPrincipalKind
{
    /// <summary>Everyone in the document's tenant.</summary>
    Tenant = 1,

    /// <summary>One person, by the identity provider's subject id.</summary>
    User = 2,

    /// <summary>A group, by the identity provider's group id (Entra ID's object id).</summary>
    Group = 3,
}

/// <summary>
/// A party that can be granted access to a document: everyone in the tenant, one user, or
/// one group.
/// </summary>
/// <remarks>
/// <para>
/// <b>One canonical string form, used everywhere.</b> <c>tenant</c>, <c>user:&lt;subject&gt;</c>,
/// <c>group:&lt;group id&gt;</c>. The same string is stored on the document row, written into
/// the vector payload, and computed for the caller. Access is then one rule in three places:
/// <i>the caller's keys and the document's keys share at least one entry</i>. A rule that is
/// a set intersection over identical strings is a rule that SQL, Qdrant and C# cannot
/// disagree about (ADR-0027).
/// </para>
/// <para>
/// <b>Identities from the identity provider, not local user ids.</b> A grant names the
/// subject the token carries, so checking it needs no database lookup and works for a user
/// who has never signed in yet. The cost: a grant written for a subject id survives a user
/// being renamed, and dies if the provider ever reissues ids, which mainstream providers do
/// not.
/// </para>
/// <para>
/// Ids are case-sensitive, because subject ids are. <c>user:Alice</c> and <c>user:alice</c> are
/// two people.
/// </para>
/// </remarks>
public sealed record AccessPrincipal
{
    /// <summary>Longest id accepted. Entra object ids are 36 characters; subjects rarely pass 100.</summary>
    public const int MaxIdLength = 200;

    private const string TenantKey = "tenant";
    private const string UserPrefix = "user:";
    private const string GroupPrefix = "group:";

    private AccessPrincipal(AccessPrincipalKind kind, string? id)
    {
        Kind = kind;
        Id = id;
        Key = kind switch
        {
            AccessPrincipalKind.Tenant => TenantKey,
            AccessPrincipalKind.User => UserPrefix + id,
            AccessPrincipalKind.Group => GroupPrefix + id,
            _ => throw new DomainException($"Unknown access principal kind {kind}."),
        };
    }

    /// <summary>Everyone in the document's tenant. The default for a new document.</summary>
    public static AccessPrincipal Tenant { get; } = new(AccessPrincipalKind.Tenant, null);

    public AccessPrincipalKind Kind { get; }

    /// <summary>The user's subject or the group's id. Null for <see cref="AccessPrincipalKind.Tenant"/>.</summary>
    public string? Id { get; }

    /// <summary>The canonical form: <c>tenant</c>, <c>user:…</c> or <c>group:…</c>.</summary>
    public string Key { get; }

    public static AccessPrincipal User(string subjectId) => new(AccessPrincipalKind.User, ValidId(subjectId, "user"));

    public static AccessPrincipal Group(string groupId) => new(AccessPrincipalKind.Group, ValidId(groupId, "group"));

    /// <summary>Reads the canonical form back.</summary>
    /// <exception cref="DomainException">Not a canonical key.</exception>
    public static AccessPrincipal Parse(string key) =>
        TryParse(key, out var principal)
            ? principal
            : throw new DomainException(
                $"'{key}' is not an access principal. Expected 'tenant', 'user:<subject>' or 'group:<id>'.");

    /// <summary>
    /// Reads the canonical form back without throwing, for a validator checking request shape.
    /// </summary>
    public static bool TryParse(string? key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AccessPrincipal? principal)
    {
        principal = null;

        if (key is null)
        {
            return false;
        }

        if (string.Equals(key, TenantKey, StringComparison.Ordinal))
        {
            principal = Tenant;
            return true;
        }

        if (key.StartsWith(UserPrefix, StringComparison.Ordinal) && IsValidId(key[UserPrefix.Length..]))
        {
            principal = new AccessPrincipal(AccessPrincipalKind.User, key[UserPrefix.Length..]);
            return true;
        }

        if (key.StartsWith(GroupPrefix, StringComparison.Ordinal) && IsValidId(key[GroupPrefix.Length..]))
        {
            principal = new AccessPrincipal(AccessPrincipalKind.Group, key[GroupPrefix.Length..]);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Not trimmed into validity: an id with surrounding spaces is refused rather than
    /// silently repaired. A grant to <c>" alice"</c> that quietly became a grant to
    /// <c>"alice"</c> would be a grant nobody wrote.
    /// </summary>
    private static bool IsValidId(string id) =>
        id.Length is > 0 and <= MaxIdLength
        && !char.IsWhiteSpace(id[0])
        && !char.IsWhiteSpace(id[^1])
        && !id.Any(char.IsControl);

    private static string ValidId(string id, string kind)
    {
        ArgumentNullException.ThrowIfNull(id);

        return IsValidId(id)
            ? id
            : throw new DomainException(
                $"A {kind} id must be 1 to {MaxIdLength} characters, with no control characters "
                + "and no leading or trailing whitespace.");
    }

    public override string ToString() => Key;
}
