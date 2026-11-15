using EKA.Domain.Common;

namespace EKA.Application.Identity;

/// <summary>
/// Who is calling, as the authenticated principal states it: the facts a use case needs to
/// find or create the caller's local <c>User</c>.
/// </summary>
/// <remarks>
/// Built by <c>EKA.Api</c> from claims, never from request content — the same rule that
/// governs <see cref="TenantId"/> on every command (ADR-0014). A command carrying this
/// record is a command whose caller was authenticated, which is why it is a type and not
/// four loose strings.
/// </remarks>
public sealed record CallerIdentity(
    TenantId TenantId,
    string SubjectId,
    string? Email,
    string? DisplayName);
