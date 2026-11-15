namespace EKA.Application.Abstractions;

/// <summary>
/// Who is making the current request.
/// </summary>
/// <remarks>
/// The domain has no notion of "who is logged in" — that is why <c>CreatedBy</c> and
/// <c>ModifiedBy</c> are left null by entities and filled by the persistence layer from
/// this abstraction. Implemented in <c>EKA.Api</c> over <c>HttpContext</c>, and in the
/// ingestion worker as a fixed service identity, because background work has no
/// interactive user.
/// </remarks>
public interface ICurrentUser
{
    /// <summary>
    /// The identity provider's subject claim, or a service identity for background work.
    /// Null only when genuinely unauthenticated.
    /// </summary>
    string? SubjectId { get; }

    /// <summary>Tenant of the caller, used to scope every query. Null when
    /// unauthenticated.</summary>
    Guid? TenantId { get; }

    /// <summary>The caller's email as the identity provider asserts it, if it does.</summary>
    /// <remarks>Used only to provision a local user on first contact (Step 6). Default
    /// members so a service identity, which has no mailbox, need not implement them.</remarks>
    string? Email => null;

    /// <summary>The caller's display name as the identity provider asserts it, if it does.</summary>
    string? DisplayName => null;

    bool IsAuthenticated => SubjectId is not null;
}
