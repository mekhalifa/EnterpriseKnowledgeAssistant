using EKA.Domain.Common;
using EKA.Domain.Conversations;

namespace EKA.Application.Abstractions;

/// <summary>
/// Collection-like access to the <see cref="Conversation"/> aggregate.
/// </summary>
/// <remarks>
/// Conversations are private to one person, so every method takes both the tenant and the
/// owning user. Scoping on tenant alone would let one colleague read another's chat
/// history — a narrower leak than cross-tenant, and just as unacceptable.
/// </remarks>
public interface IConversationRepository
{
    /// <summary>Loads a conversation without its messages.</summary>
    Task<Conversation?> GetByIdAsync(
        TenantId tenantId,
        Guid userId,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Loads a conversation with its messages and their citations.</summary>
    Task<Conversation?> GetWithMessagesAsync(
        TenantId tenantId,
        Guid userId,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Active conversations for the sidebar, most recently touched first.</summary>
    Task<IReadOnlyList<Conversation>> GetActiveForUserAsync(
        TenantId tenantId,
        Guid userId,
        int limit,
        CancellationToken cancellationToken = default);

    void Add(Conversation conversation);

    void Remove(Conversation conversation);
}
