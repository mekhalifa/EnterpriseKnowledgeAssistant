using EKA.Application.Abstractions;
using EKA.Application.Common;
using EKA.Application.Identity;
using EKA.Contracts.Conversations;

namespace EKA.Application.Conversations.ListConversations;

/// <summary>The caller's active conversations, most recently used first.</summary>
public sealed class ListConversationsHandler(
    UserProvisioner provisioner,
    IConversationRepository conversations)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public async Task<Result<IReadOnlyList<ConversationSummaryResponse>>> HandleAsync(
        ListConversationsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit is < 1 or > MaxLimit)
        {
            return Error.Validation(
                "conversation.limit_out_of_range",
                $"Limit must be between 1 and {MaxLimit}; got {query.Limit}.");
        }

        var user = await provisioner.FindAsync(query.Caller, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result<IReadOnlyList<ConversationSummaryResponse>>.Success([]);
        }

        var active = await conversations
            .GetActiveForUserAsync(query.Caller.TenantId, user.Id, query.Limit, cancellationToken)
            .ConfigureAwait(false);

        return Result<IReadOnlyList<ConversationSummaryResponse>>.Success([.. active.Select(c => c.ToSummary())]);
    }
}
