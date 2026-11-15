using EKA.Application.Abstractions;
using EKA.Domain.Common;
using EKA.Domain.Conversations;
using Microsoft.EntityFrameworkCore;

namespace EKA.Infrastructure.Persistence.Repositories;

internal sealed class ConversationRepository : IConversationRepository
{
    private readonly EkaDbContext _context;

    public ConversationRepository(EkaDbContext context)
    {
        _context = context;
    }

    public Task<Conversation?> GetByIdAsync(
        TenantId tenantId,
        Guid userId,
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        _context.Conversations
            .FirstOrDefaultAsync(
                conversation => conversation.TenantId == tenantId
                                && conversation.UserId == userId
                                && conversation.Id == conversationId,
                cancellationToken);

    public Task<Conversation?> GetWithMessagesAsync(
        TenantId tenantId,
        Guid userId,
        Guid conversationId,
        CancellationToken cancellationToken = default) =>
        _context.Conversations
            // ORDERED include. Without OrderBy, SQL Server returns the messages in whatever
            // order the plan produces, and the aggregate rebuilds its list in that order.
            // LastMessage, the "answer must follow a question" rule and the prompt history
            // would all be computed from a shuffled thread. CreatedAt is the conversation's
            // real order; Id breaks ties for messages stamped in the same tick.
            .Include(conversation => conversation.Messages
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id))
            .ThenInclude(message => message.Citations)
            .FirstOrDefaultAsync(
                conversation => conversation.TenantId == tenantId
                                && conversation.UserId == userId
                                && conversation.Id == conversationId,
                cancellationToken);

    public async Task<IReadOnlyList<Conversation>> GetActiveForUserAsync(
        TenantId tenantId,
        Guid userId,
        int limit,
        CancellationToken cancellationToken = default) =>
        await _context.Conversations
            .Where(conversation => conversation.TenantId == tenantId
                                   && conversation.UserId == userId
                                   && conversation.ArchivedAt == null)
            // Most recently touched first. ModifiedAt is null until the first message, so
            // fall back to CreatedAt — otherwise brand-new conversations sort last.
            .OrderByDescending(conversation => conversation.ModifiedAt ?? conversation.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void Add(Conversation conversation) => _context.Conversations.Add(conversation);

    public void Remove(Conversation conversation) => _context.Conversations.Remove(conversation);
}
