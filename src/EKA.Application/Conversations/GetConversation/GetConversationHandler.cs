using EKA.Application.Abstractions;
using EKA.Application.Common;
using EKA.Application.Identity;
using EKA.Contracts.Conversations;

namespace EKA.Application.Conversations.GetConversation;

/// <summary>Reads one of the caller's conversations with its messages.</summary>
/// <remarks>
/// Looks the caller up and never provisions them: a caller with no local user owns no
/// conversations, so the answer is a 404 without writing anything (see
/// <see cref="UserProvisioner"/>).
/// </remarks>
public sealed class GetConversationHandler(
    UserProvisioner provisioner,
    IConversationRepository conversations)
{
    public async Task<Result<ConversationResponse>> HandleAsync(
        GetConversationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var user = await provisioner.FindAsync(query.Caller, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return ConversationErrors.NotFound(query.ConversationId);
        }

        // Tenant AND user inside the query. Another user's conversation in the same
        // tenant is as invisible as another tenant's.
        var conversation = await conversations
            .GetWithMessagesAsync(query.Caller.TenantId, user.Id, query.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        return conversation is null
            ? ConversationErrors.NotFound(query.ConversationId)
            : conversation.ToResponse();
    }
}
