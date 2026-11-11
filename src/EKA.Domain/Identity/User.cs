using EKA.Domain.Common;

namespace EKA.Domain.Identity;

/// <summary>
/// A person who can sign in and ask questions, as this system knows them.
/// </summary>
/// <remarks>
/// <para>
/// <b>No password, no hash, no salt.</b> Authentication belongs to the identity provider
/// chosen in Step 12 (Entra ID, Keycloak, or similar); this entity holds only the local
/// projection of an account that exists elsewhere. Storing credentials would mean owning
/// password rotation, breach response, MFA, and lockout policy — all solved problems that
/// are expensive to re-solve badly.
/// </para>
/// <para>
/// <see cref="ExternalId"/> is the identity provider's immutable subject claim, not the
/// email address. Emails get reassigned when people leave and change when people marry;
/// keying on one eventually hands a departed employee's conversation history to whoever
/// inherits the address.
/// </para>
/// </remarks>
public sealed class User : EntityAudit<Guid>
{
    private readonly List<string> _roles = [];

    /// <summary>EF Core materialization only.</summary>
    private User()
    {
    }

    public User(
        Guid id,
        TenantId tenantId,
        string externalId,
        string email,
        string displayName,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        if (tenantId.IsEmpty)
        {
            throw new DomainException("A user must belong to a tenant.");
        }

        if (string.IsNullOrWhiteSpace(externalId))
        {
            throw new DomainException("A user must have an identity provider subject id.");
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new DomainException("A user must have an email address.");
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new DomainException("A user must have a display name.");
        }

        TenantId = tenantId;
        ExternalId = externalId.Trim();
        Email = email.Trim().ToLowerInvariant();
        DisplayName = displayName.Trim();
        IsActive = true;
    }

    public TenantId TenantId { get; private set; }

    /// <summary>The identity provider's stable subject claim. Immutable.</summary>
    public string ExternalId { get; private set; } = string.Empty;

    /// <summary>Lowercased for comparison. Display only — never an identity key.</summary>
    public string Email { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    /// <summary>
    /// Role names, mirrored from the identity provider's token claims on each sign-in.
    /// Read by document ACL evaluation in Step 9, which is why they are part of the domain
    /// rather than left in the token: retrieval needs them to build its filter.
    /// </summary>
    public IReadOnlyCollection<string> Roles => _roles.AsReadOnly();

    public bool IsInRole(string role) =>
        _roles.Contains(role, StringComparer.OrdinalIgnoreCase);

    public void GrantRole(string role, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new DomainException("Role name cannot be empty.");
        }

        var normalized = role.Trim();

        if (IsInRole(normalized))
        {
            return;
        }

        _roles.Add(normalized);
        MarkModified(at);
    }

    public void RevokeRole(string role, DateTimeOffset at)
    {
        var removed = _roles.RemoveAll(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
        {
            MarkModified(at);
        }
    }

    /// <summary>
    /// Marks the account inactive. Deactivation rather than deletion, because a deleted
    /// user would orphan the conversations and audit records that reference them — and
    /// those are exactly what a security review asks for.
    /// </summary>
    public void Deactivate(DateTimeOffset at)
    {
        IsActive = false;
        MarkModified(at);
    }

    public void Reactivate(DateTimeOffset at)
    {
        IsActive = true;
        MarkModified(at);
    }

    public override string ToString() => $"{DisplayName} <{Email}>";
}
